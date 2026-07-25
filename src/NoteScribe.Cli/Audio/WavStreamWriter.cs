using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace NoteScribe.Cli.Audio;

/// <summary>
/// Writes 16 kHz mono 16-bit PCM WAV — the one format <see cref="Core.Media.WaveFileReaderService"/>
/// accepts, so a kept session recording can be re-transcribed later without conversion.
/// </summary>
/// <remarks>
/// The RIFF sizes are patched on close. If the process dies first the file is still playable by
/// anything that trusts the stream length over the header, which is the common case.
/// </remarks>
internal sealed class WavStreamWriter : IAsyncDisposable
{
    private const int HeaderBytes = 44;
    private const int BitsPerSample = 16;

    private readonly FileStream _stream;
    private readonly int _sampleRate;
    private readonly int _channels;

    private long _dataBytes;
    private bool _closed;

    public WavStreamWriter(string path, int sampleRate, int channels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _sampleRate = sampleRate;
        _channels = channels;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);

        // Reserve the header; the sizes in it are only knowable once the stream is closed.
        _stream.Write(new byte[HeaderBytes]);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
    {
        if (_closed || samples.Length == 0)
        {
            return;
        }

        var byteCount = samples.Length * sizeof(short);
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            ReadOnlySpan<float> source = samples.Span;
            Span<byte> destination = buffer.AsSpan(0, byteCount);

            for (var i = 0; i < source.Length; i++)
            {
                var scaled = (int)Math.Round(Math.Clamp(source[i], -1f, 1f) * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(destination[(i * sizeof(short))..], (short)scaled);
            }

            await _stream.WriteAsync(buffer.AsMemory(0, byteCount), cancellationToken).ConfigureAwait(false);
            _dataBytes += byteCount;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;

        try
        {
            await _stream.FlushAsync().ConfigureAwait(false);
            _stream.Seek(0, SeekOrigin.Begin);
            await _stream.WriteAsync(BuildHeader()).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private byte[] BuildHeader()
    {
        var blockAlign = _channels * (BitsPerSample / 8);
        var header = new byte[HeaderBytes];
        Span<byte> span = header;

        Encoding.ASCII.GetBytes("RIFF", span[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], (uint)Math.Min(_dataBytes + HeaderBytes - 8, uint.MaxValue));
        Encoding.ASCII.GetBytes("WAVE", span[8..12]);

        Encoding.ASCII.GetBytes("fmt ", span[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..22], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..24], (ushort)_channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], (uint)_sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..32], (uint)(_sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..34], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..36], BitsPerSample);

        Encoding.ASCII.GetBytes("data", span[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..44], (uint)Math.Min(_dataBytes, uint.MaxValue));

        return header;
    }
}
