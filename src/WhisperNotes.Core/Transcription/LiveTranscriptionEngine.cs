using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Diarization;

namespace WhisperNotes.Core.Transcription;

/// <summary>
/// Capture -> silence-aware chunker -> bounded queue -> whisper decode -> segment stream.
/// </summary>
public sealed class LiveTranscriptionEngine : ILiveTranscriptionEngine, IAsyncDisposable
{
    /// <summary>
    /// Queue depth in chunks. At the 15 s ceiling this is roughly two minutes of audio in flight,
    /// which absorbs any realistic decode hiccup. When it does fill we block the capture loop rather
    /// than discard chunks: these are billable meeting notes, and silently losing a sentence is far
    /// worse than a brief stall. A stall long enough to matter shows up as a device-side overrun
    /// from the capture source, which surfaces as an AudioCaptureException instead of vanishing.
    /// </summary>
    private const int QueueDepthChunks = 8;

    /// <summary>20 ms of audio — fine enough to place a cut, long enough for a stable RMS.</summary>
    private const int RmsWindowSamples = AudioFrame.SampleRate / 50;

    /// <summary>
    /// Each chunk re-includes the tail of the previous one so a word straddling a cut is decoded
    /// whole at least once. The repeated words are stitched out again by <see cref="SeamStitcher"/>.
    /// </summary>
    private static readonly TimeSpan SeamOverlap = TimeSpan.FromMilliseconds(250);

    /// <summary>Below this a flushed remainder is device tail, not a word worth a decode.</summary>
    private static readonly TimeSpan MinimumFlush = TimeSpan.FromMilliseconds(200);

    private readonly ITranscriberFactory _transcriberFactory;
    private readonly ChunkingOptions _chunking;
    private readonly SemaphoreSlim _transcriberGate = new(1, 1);

    private ITranscriber? _transcriber;
    private TranscriptionOptions? _transcriberOptions;
    private bool _disposed;

    public LiveTranscriptionEngine(ITranscriberFactory transcriberFactory, ChunkingOptions? chunking = null)
    {
        ArgumentNullException.ThrowIfNull(transcriberFactory);
        _transcriberFactory = transcriberFactory;
        _chunking = Validate(chunking ?? ChunkingOptions.Default);
    }

    public async IAsyncEnumerable<TranscriptSegment> RunAsync(
        IAudioCaptureSource source,
        TranscriptionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        ISpeakerAttributor? speakerAttributor = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ITranscriber transcriber = await GetTranscriberAsync(options, cancellationToken).ConfigureAwait(false);

        Channel<AudioFrame> queue = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(QueueDepthChunks)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        CancellationTokenSource captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task capture = Task.Run(() => CaptureLoopAsync(source, queue.Writer, captureCts.Token), CancellationToken.None);

        SeamStitcher stitcher = new();
        try
        {
            // Decoding happens here, on the consumer side of the queue, so a slow model can never
            // stall the capture loop that is draining the audio device.
            await foreach (AudioFrame chunk in queue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                stitcher.BeginChunk();

                // Deliberately not cancellationToken: once audio is captured it gets decoded, even
                // if the user already pressed Stop. Otherwise the last sentence of every session
                // would be thrown away.
                await foreach (TranscriptSegment segment in transcriber
                    .TranscribeAsync(chunk.Samples, chunk.Offset, CancellationToken.None)
                    .ConfigureAwait(false))
                {
                    TranscriptSegment? stitched = stitcher.Accept(segment);
                    if (stitched is not null)
                    {
                        // The chunk is the only place the decoded words and their source PCM still
                        // coexist. Observe it here so live capture can perform the same end-of-
                        // session clustering as imported media without retaining the whole meeting.
                        speakerAttributor?.Observe(
                            stitched.Start,
                            stitched.End,
                            chunk.Samples.Span,
                            chunk.Offset);

                        yield return stitched;
                    }
                }
            }
        }
        finally
        {
            await captureCts.CancelAsync().ConfigureAwait(false);

            // If the caller abandoned the enumeration the capture loop may be parked in WriteAsync;
            // draining unblocks it. Chunks read here are intentionally not decoded — nobody is listening.
            Task drain = Task.Run(
                async () =>
                {
                    await foreach (AudioFrame _ in queue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                    }
                },
                CancellationToken.None);

            try
            {
                await capture.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await drain.ConfigureAwait(false);
            captureCts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _transcriberGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_transcriber is not null)
            {
                await _transcriber.DisposeAsync().ConfigureAwait(false);
                _transcriber = null;
                _transcriberOptions = null;
            }
        }
        finally
        {
            _transcriberGate.Release();
            _transcriberGate.Dispose();
        }
    }

    private async Task CaptureLoopAsync(
        IAudioCaptureSource source,
        ChannelWriter<AudioFrame> writer,
        CancellationToken cancellationToken)
    {
        Chunker chunker = new(_chunking, SeamOverlap, MinimumFlush);
        List<AudioFrame> ready = [];
        Exception? failure = null;

        try
        {
            await foreach (AudioFrame frame in source.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                ready.Clear();
                chunker.Append(frame, ready);

                foreach (AudioFrame chunk in ready)
                {
                    await writer.WriteAsync(chunk, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Runs even when the device died mid-session: whatever was already captured is still worth
        // decoding, and the failure is reported to the consumer immediately afterwards.
        try
        {
            if (chunker.TryFlush(out AudioFrame tail))
            {
                await writer.WriteAsync(tail, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (failure is null)
        {
            failure = ex;
        }

        writer.TryComplete(failure);
    }

    private async ValueTask<ITranscriber> GetTranscriberAsync(
        TranscriptionOptions options,
        CancellationToken cancellationToken)
    {
        await _transcriberGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_transcriber is not null && _transcriberOptions == options)
            {
                return _transcriber;
            }

            // The weights stay loaded between sessions so pressing Record again is instant; only a
            // genuine options change pays for a reload.
            if (_transcriber is not null)
            {
                ITranscriber stale = _transcriber;
                _transcriber = null;
                _transcriberOptions = null;
                await stale.DisposeAsync().ConfigureAwait(false);
            }

            _transcriber = await _transcriberFactory.CreateAsync(options, cancellationToken).ConfigureAwait(false);
            _transcriberOptions = options;
            return _transcriber;
        }
        finally
        {
            _transcriberGate.Release();
        }
    }

    private static ChunkingOptions Validate(ChunkingOptions options)
    {
        if (options.MinChunk <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MinChunk, "MinChunk must be positive.");
        }

        if (options.MaxChunk < options.MinChunk)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxChunk, "MaxChunk must be at least MinChunk.");
        }

        if (options.SilenceDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.SilenceDuration, "SilenceDuration cannot be negative.");
        }

        if (options.SilenceThreshold is < 0f or > 1f || !float.IsFinite(options.SilenceThreshold))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.SilenceThreshold, "SilenceThreshold must be within 0..1.");
        }

        return options;
    }

    /// <summary>
    /// Accumulates frames and emits decodable chunks. Analysis runs on fixed 20 ms windows rather
    /// than whole frames so the cut position does not depend on whatever buffer size the capture
    /// device happens to hand us.
    /// </summary>
    private sealed class Chunker
    {
        private readonly int _minSamples;
        private readonly int _maxSamples;
        private readonly int _silenceSamples;
        private readonly int _overlapSamples;
        private readonly int _minFlushSamples;
        private readonly float _threshold;

        private float[] _buffer;
        private int _count;
        private int _analysed;
        private int _trailingSilence;
        private bool _hasVoice;
        private TimeSpan _bufferStart;

        public Chunker(ChunkingOptions options, TimeSpan overlap, TimeSpan minimumFlush)
        {
            _minSamples = ToSamples(options.MinChunk);
            _maxSamples = Math.Max(ToSamples(options.MaxChunk), RmsWindowSamples);
            _silenceSamples = ToSamples(options.SilenceDuration);
            _threshold = options.SilenceThreshold;
            _minFlushSamples = ToSamples(minimumFlush);

            // Overlap must stay well inside a chunk or a cut would immediately re-trigger.
            _overlapSamples = Math.Min(ToSamples(overlap), _maxSamples / 4);
            _buffer = new float[Math.Max(_maxSamples + AudioFrame.SampleRate, AudioFrame.SampleRate * 4)];
        }

        public void Append(AudioFrame frame, List<AudioFrame> ready)
        {
            ReadOnlySpan<float> samples = frame.Samples.Span;
            if (samples.Length == 0)
            {
                return;
            }

            if (_count == 0)
            {
                _bufferStart = frame.Offset;
            }

            EnsureCapacity(_count + samples.Length);
            samples.CopyTo(_buffer.AsSpan(_count));
            _count += samples.Length;

            while (_analysed + RmsWindowSamples <= _count)
            {
                bool voiced = Rms(_buffer.AsSpan(_analysed, RmsWindowSamples)) >= _threshold;
                _analysed += RmsWindowSamples;

                if (voiced)
                {
                    _trailingSilence = 0;
                    _hasVoice = true;
                }
                else
                {
                    _trailingSilence += RmsWindowSamples;
                }

                if (_analysed >= _maxSamples)
                {
                    Emit(_analysed, ready);
                }
                else if (_trailingSilence >= _silenceSamples)
                {
                    if (_hasVoice)
                    {
                        if (_analysed >= _minSamples)
                        {
                            Emit(_analysed, ready);
                        }
                    }
                    else
                    {
                        // Nothing but room tone so far. Handing it to whisper only invites
                        // hallucinated captions, so recycle the buffer and keep the timeline moving.
                        Retain(_analysed);
                    }
                }
            }
        }

        public bool TryFlush(out AudioFrame chunk)
        {
            chunk = default;
            if (_count == 0)
            {
                return false;
            }

            bool worthDecoding = _count >= _minFlushSamples && ContainsVoice(_buffer.AsSpan(0, _count));
            if (worthDecoding)
            {
                chunk = new AudioFrame(_buffer.AsSpan(0, _count).ToArray(), _bufferStart);
            }

            _count = 0;
            _analysed = 0;
            _trailingSilence = 0;
            _hasVoice = false;
            return worthDecoding;
        }

        private void Emit(int cutPosition, List<AudioFrame> ready)
        {
            ready.Add(new AudioFrame(_buffer.AsSpan(0, cutPosition).ToArray(), _bufferStart));
            Retain(cutPosition);
        }

        private void Retain(int cutPosition)
        {
            int retainFrom = Math.Max(0, cutPosition - _overlapSamples);
            int remaining = _count - retainFrom;

            if (retainFrom > 0)
            {
                Array.Copy(_buffer, retainFrom, _buffer, 0, remaining);
            }

            _bufferStart += SamplesToTime(retainFrom);
            _count = remaining;
            _analysed = cutPosition - retainFrom;

            // Re-derive the silence run and voice flag from the carried-over tail; it is already
            // analysed audio and its state decides when the next cut can happen.
            _trailingSilence = 0;
            _hasVoice = false;
            for (int start = 0; start + RmsWindowSamples <= _analysed; start += RmsWindowSamples)
            {
                if (Rms(_buffer.AsSpan(start, RmsWindowSamples)) >= _threshold)
                {
                    _trailingSilence = 0;
                    _hasVoice = true;
                }
                else
                {
                    _trailingSilence += RmsWindowSamples;
                }
            }
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length)
            {
                return;
            }

            int size = _buffer.Length;
            while (size < required)
            {
                size *= 2;
            }

            Array.Resize(ref _buffer, size);
        }

        private bool ContainsVoice(ReadOnlySpan<float> samples)
        {
            for (int start = 0; start + RmsWindowSamples <= samples.Length; start += RmsWindowSamples)
            {
                if (Rms(samples.Slice(start, RmsWindowSamples)) >= _threshold)
                {
                    return true;
                }
            }

            // A remainder shorter than one window still counts if it is loud.
            int tail = samples.Length % RmsWindowSamples;
            return tail > 0 && Rms(samples[^tail..]) >= _threshold;
        }

        private static float Rms(ReadOnlySpan<float> samples)
        {
            double sum = 0;
            foreach (float sample in samples)
            {
                sum += (double)sample * sample;
            }

            return (float)Math.Sqrt(sum / samples.Length);
        }

        private static int ToSamples(TimeSpan duration) =>
            (int)Math.Round(duration.TotalSeconds * AudioFrame.SampleRate);

        private static TimeSpan SamplesToTime(int samples) =>
            TimeSpan.FromSeconds((double)samples / AudioFrame.SampleRate);
    }

    /// <summary>
    /// Removes the words the overlap causes to be decoded twice, and keeps timestamps monotonic
    /// across chunk boundaries.
    /// </summary>
    private sealed class SeamStitcher
    {
        private const int TailWords = 16;

        private readonly List<string> _tail = [];
        private TimeSpan _lastEnd = TimeSpan.Zero;
        private bool _atSeam;

        public void BeginChunk() => _atSeam = true;

        public TranscriptSegment? Accept(TranscriptSegment segment)
        {
            string text = segment.Text;

            if (_atSeam && _tail.Count > 0)
            {
                text = StripDuplicatedPrefix(text, _tail);
                if (text.Length == 0)
                {
                    // The whole segment was re-decoded overlap. Stay at the seam so the next
                    // segment of this chunk is checked too.
                    return null;
                }
            }

            _atSeam = false;

            TimeSpan start = segment.Start < _lastEnd ? _lastEnd : segment.Start;
            TimeSpan end = segment.End > start ? segment.End : start + TimeSpan.FromMilliseconds(1);
            _lastEnd = end;

            AppendTail(text);
            return segment with { Start = start, End = end, Text = text };
        }

        private void AppendTail(string text)
        {
            foreach (Word word in SplitWords(text))
            {
                _tail.Add(word.Normalised);
            }

            if (_tail.Count > TailWords)
            {
                _tail.RemoveRange(0, _tail.Count - TailWords);
            }
        }

        private static string StripDuplicatedPrefix(string text, List<string> tail)
        {
            List<Word> words = SplitWords(text);
            if (words.Count == 0)
            {
                return string.Empty;
            }

            // Longest match wins: prefer removing a whole repeated phrase over a single word.
            int longest = Math.Min(words.Count, tail.Count);
            for (int k = longest; k >= 1; k--)
            {
                bool matches = true;
                for (int i = 0; i < k && matches; i++)
                {
                    matches = string.Equals(tail[^(k - i)], words[i].Normalised, StringComparison.Ordinal);
                }

                if (!matches)
                {
                    continue;
                }

                if (k == words.Count)
                {
                    return string.Empty;
                }

                return text[words[k].Start..].TrimStart(' ', ',', '.', '!', '?', ';', ':', '-').Trim();
            }

            return text;
        }

        private static List<Word> SplitWords(string text)
        {
            List<Word> words = [];
            int i = 0;

            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                if (i >= text.Length)
                {
                    break;
                }

                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                string normalised = Normalise(text.AsSpan(start, i - start));
                if (normalised.Length > 0)
                {
                    words.Add(new Word(start, normalised));
                }
            }

            return words;
        }

        private static string Normalise(ReadOnlySpan<char> word)
        {
            char[]? rented = word.Length > 256 ? new char[word.Length] : null;
            Span<char> buffer = rented ?? stackalloc char[256];
            int length = 0;

            foreach (char c in word)
            {
                if (char.IsLetterOrDigit(c) || c == '\'')
                {
                    buffer[length++] = char.ToLowerInvariant(c);
                }
            }

            return new string(buffer[..length]);
        }

        private readonly record struct Word(int Start, string Normalised);
    }
}
