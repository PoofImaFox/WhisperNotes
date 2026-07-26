using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WhisperNotes.Core.Audio;

/// <summary>
/// Normalises an arbitrary WASAPI endpoint format to <see cref="AudioFrame.SampleRate"/> mono
/// 32-bit float. Device bytes go in via <see cref="Write"/>, normalised samples come out via
/// <see cref="Read"/>.
/// </summary>
/// <remarks>
/// Not thread-safe. Drive it from a single thread — in practice the NAudio capture callback thread,
/// which raises <c>DataAvailable</c> serially.
/// </remarks>
internal sealed class AudioResampler
{
    private const int SourceBufferSeconds = 30;

    /// <summary>Slack on the read gate to absorb rounding in the source-frames-per-output estimate.</summary>
    private const int GateHeadroomFrames = 16;

    private readonly BufferedWaveProvider _source;
    private readonly ISampleProvider _pipeline;
    private readonly byte[] _partialFrame;
    private int _partialFrameBytes;

    public AudioResampler(WaveFormat sourceFormat)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);

        SourceFormat = Flatten(sourceFormat);
        EnsureSupported(SourceFormat);
        _partialFrame = new byte[Math.Max(1, SourceFormat.BlockAlign)];

        _source = new BufferedWaveProvider(SourceFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(SourceBufferSeconds),
            // ReadFully defaults to true, which makes the provider pad with silence and never
            // report 0 bytes — a drain-until-empty loop against it would spin forever.
            ReadFully = false
        };

        // ToSampleProvider() picks the right converter for Pcm 8/16/24/32 and IeeeFloat 32/64,
        // so everything downstream of here is guaranteed to be float in [-1, 1].
        ISampleProvider provider = _source.ToSampleProvider();

        provider = provider.WaveFormat.Channels switch
        {
            1 => provider,
            2 => new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f },
            _ => new ChannelAverageSampleProvider(provider)
        };

        if (provider.WaveFormat.SampleRate != AudioFrame.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, AudioFrame.SampleRate);
        }

        _pipeline = provider;
    }

    /// <summary>The endpoint format this instance was built for, flattened out of WAVEFORMATEXTENSIBLE.</summary>
    public WaveFormat SourceFormat { get; }

    /// <summary>Queues raw endpoint bytes. Overflow drops the incoming packet rather than throwing.</summary>
    /// <remarks>
    /// Everything downstream reinterprets these bytes as fixed-width samples, so the buffer must
    /// only ever contain whole interleaved frames. A byte count that is not a multiple of
    /// <see cref="WaveFormat.BlockAlign"/> would shift every subsequent sample by a byte or two —
    /// silently, and permanently, turning audio into NaNs. WASAPI always delivers whole frames,
    /// but the cost of guaranteeing it here is one carry buffer.
    /// </remarks>
    public void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0)
        {
            return;
        }

        int align = _partialFrame.Length;

        if (_partialFrameBytes > 0)
        {
            int fill = Math.Min(align - _partialFrameBytes, count);
            Array.Copy(buffer, offset, _partialFrame, _partialFrameBytes, fill);
            _partialFrameBytes += fill;
            offset += fill;
            count -= fill;

            if (_partialFrameBytes == align)
            {
                Append(_partialFrame, 0, align);
                _partialFrameBytes = 0;
            }
        }

        int whole = count - (count % align);
        if (whole > 0)
        {
            Append(buffer, offset, whole);
        }

        int leftover = count - whole;
        if (leftover > 0)
        {
            Array.Copy(buffer, offset + whole, _partialFrame, 0, leftover);
            _partialFrameBytes = leftover;
        }
    }

    /// <summary>
    /// Drains up to <paramref name="count"/> normalised samples into <paramref name="destination"/>.
    /// Returns 0 until enough source audio is buffered to satisfy the request in full, which is
    /// also the drain loop's terminator.
    /// </summary>
    /// <remarks>
    /// The all-or-nothing gate is not just tidiness. <see cref="WdlResamplingSampleProvider"/>
    /// silently loses the fractional part of its conversion whenever it is handed less input than
    /// it asked for, so draining it to exhaustion after every capture packet leaks a slice of every
    /// packet — measured here at up to 28 seconds of timestamp drift per hour, varying with the
    /// packet size. Only ever asking for what the buffer can fully cover makes the conversion exact;
    /// the cost is one frame of extra latency.
    /// </remarks>
    public int Read(float[] destination, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (count <= 0 || _source.BufferedBytes < RequiredSourceBytes(count))
        {
            return 0;
        }

        return _pipeline.Read(destination, offset, count);
    }

    private long RequiredSourceBytes(int count)
    {
        long frames = (((long)count * SourceFormat.SampleRate) + AudioFrame.SampleRate - 1) / AudioFrame.SampleRate;
        return (frames + GateHeadroomFrames) * SourceFormat.BlockAlign;
    }

    private void Append(byte[] buffer, int offset, int count)
    {
        // BufferedWaveProvider's own overflow handling truncates the write at whatever space is
        // left, which would leave a fraction of a frame in the buffer. Dropping the whole packet
        // discards the same audio while keeping the stream frame-aligned.
        if (count > _source.BufferLength - _source.BufferedBytes)
        {
            return;
        }

        _source.AddSamples(buffer, offset, count);
    }

    private static WaveFormat Flatten(WaveFormat format)
    {
        try
        {
            // WASAPI mix formats are WAVEFORMATEXTENSIBLE; the sample-provider converters only
            // recognise the plain Pcm/IeeeFloat encodings.
            return format.AsStandardWaveFormat();
        }
        catch (InvalidOperationException)
        {
            return format;
        }
    }

    private static void EnsureSupported(WaveFormat format)
    {
        if (format.SampleRate <= 0 || format.Channels <= 0)
        {
            throw new NotSupportedException($"Audio endpoint reported a nonsensical format: {format}.");
        }

        bool supported = format.Encoding switch
        {
            WaveFormatEncoding.Pcm => format.BitsPerSample is 8 or 16 or 24 or 32,
            WaveFormatEncoding.IeeeFloat => format.BitsPerSample is 32 or 64,
            _ => false
        };

        if (!supported)
        {
            throw new NotSupportedException(
                $"Cannot normalise audio format {format.Encoding} {format.BitsPerSample}-bit " +
                $"{format.SampleRate}Hz {format.Channels}ch; only PCM 8/16/24/32-bit and IEEE float 32/64-bit are supported.");
        }
    }

    /// <summary>
    /// Downmixes any channel count to mono by straight averaging. NAudio's
    /// <see cref="StereoToMonoSampleProvider"/> only handles two channels, so surround endpoints
    /// (5.1, 7.1) come through here.
    /// </summary>
    private sealed class ChannelAverageSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _sourceChannels;
        private readonly float _scale;
        private readonly float[] _straddle;
        private int _straddleCount;
        private float[] _interleaved = [];

        public ChannelAverageSampleProvider(ISampleProvider source)
        {
            _source = source;
            _sourceChannels = source.WaveFormat.Channels;
            _scale = 1f / _sourceChannels;
            _straddle = new float[_sourceChannels];
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int wanted = (count * _sourceChannels) - _straddleCount;
            if (wanted <= 0)
            {
                return 0;
            }

            if (_interleaved.Length < _straddleCount + wanted)
            {
                _interleaved = new float[_straddleCount + wanted];
            }

            Array.Copy(_straddle, _interleaved, _straddleCount);
            int available = _straddleCount + _source.Read(_interleaved, _straddleCount, wanted);

            // An upstream read can stop part-way through an interleaved frame; carrying the tail
            // over keeps channel alignment instead of rotating the channel order forever after.
            int frames = available / _sourceChannels;
            _straddleCount = available - (frames * _sourceChannels);
            if (_straddleCount > 0)
            {
                Array.Copy(_interleaved, frames * _sourceChannels, _straddle, 0, _straddleCount);
            }

            for (int frame = 0; frame < frames; frame++)
            {
                int start = frame * _sourceChannels;
                float sum = 0f;
                for (int channel = 0; channel < _sourceChannels; channel++)
                {
                    sum += _interleaved[start + channel];
                }

                buffer[offset + frame] = sum * _scale;
            }

            return frames;
        }
    }
}
