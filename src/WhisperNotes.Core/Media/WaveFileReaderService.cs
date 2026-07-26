using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace WhisperNotes.Core.Media;

/// <summary>
/// Minimal RIFF/WAVE reader for the exact format the rest of the pipeline uses:
/// 16 kHz, mono, 16-bit PCM. Anything else is rejected rather than silently mis-decoded.
/// </summary>
public sealed class WaveFileReaderService : IWavReader
{
    public const int ExpectedSampleRate = 16_000;
    public const int ExpectedChannels = 1;
    public const int ExpectedBitsPerSample = 16;

    private const int WaveFormatPcm = 1;
    private const int WaveFormatExtensible = 0xFFFE;

    public async IAsyncEnumerable<ReadOnlyMemory<float>> ReadChunksAsync(
        string wavPath,
        int chunkSamples,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSamples, 1);

        await using var stream = new FileStream(
            wavPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var header = ReadHeader(stream, wavPath);
        stream.Seek(header.DataOffset, SeekOrigin.Begin);

        var bytes = new byte[chunkSamples * sizeof(short)];
        var remaining = header.DataLength;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var want = (int)Math.Min(bytes.Length, remaining);
            var filled = 0;
            while (filled < want)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(filled, want - filled), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            if (filled < sizeof(short))
            {
                break;
            }

            remaining -= filled;

            var sampleCount = filled / sizeof(short);
            var samples = new float[sampleCount];
            var span = bytes.AsSpan(0, sampleCount * sizeof(short));
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * sizeof(short), sizeof(short))) / 32768f;
            }

            yield return samples;
        }
    }

    public TimeSpan GetDuration(string wavPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);

        using var stream = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = ReadHeader(stream, wavPath);
        var bytesPerSecond = (long)header.SampleRate * header.Channels * (header.BitsPerSample / 8);
        return bytesPerSecond <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)header.DataLength / bytesPerSecond);
    }

    private static WaveHeader ReadHeader(Stream stream, string wavPath)
    {
        Span<byte> riff = stackalloc byte[12];
        ReadExactly(stream, riff, wavPath);

        if (!Matches(riff[..4], "RIFF") || !Matches(riff[8..12], "WAVE"))
        {
            throw new MediaConversionException($"{wavPath} is not a RIFF/WAVE file.");
        }

        WaveFormat? format = null;
        long dataOffset = -1, dataLength = 0;

        Span<byte> chunkHeader = stackalloc byte[8];

        while (stream.Position + 8 <= stream.Length)
        {
            ReadExactly(stream, chunkHeader, wavPath);
            var id = Encoding.ASCII.GetString(chunkHeader[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            var bodyStart = stream.Position;

            if (id is "fmt ")
            {
                format = ParseFmtChunk(stream, size, wavPath);
            }
            else if (id is "data")
            {
                dataOffset = bodyStart;
                dataLength = size;
            }

            if (NextChunkStart(stream, bodyStart, size) is not { } next)
            {
                if (dataOffset >= 0)
                {
                    break;
                }

                throw new MediaConversionException($"{wavPath} has a malformed chunk '{id}'.");
            }

            stream.Seek(next, SeekOrigin.Begin);

            if (dataOffset >= 0 && format is not null)
            {
                break;
            }
        }

        return ToHeader(format, dataOffset, dataLength, stream.Length, wavPath);
    }

    /// <summary>
    /// Reads the 16 bytes every 'fmt ' chunk starts with, plus the SubFormat tag when the chunk is
    /// WAVE_FORMAT_EXTENSIBLE. Leaves the stream wherever it stopped; the caller seeks on.
    /// </summary>
    private static WaveFormat ParseFmtChunk(Stream stream, long size, string wavPath)
    {
        if (size < 16)
        {
            throw new MediaConversionException($"{wavPath} has a truncated 'fmt ' chunk.");
        }

        Span<byte> fmt = stackalloc byte[16];
        ReadExactly(stream, fmt, wavPath);

        int formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[..2]);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..4]);
        var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..8]);
        int bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..16]);

        if (formatTag == WaveFormatExtensible && size >= 40)
        {
            // WAVE_FORMAT_EXTENSIBLE keeps the real tag in the first two bytes of SubFormat.
            Span<byte> extension = stackalloc byte[24];
            ReadExactly(stream, extension, wavPath);
            formatTag = BinaryPrimitives.ReadUInt16LittleEndian(extension[8..10]);
        }

        return new WaveFormat(formatTag, sampleRate, channels, bitsPerSample);
    }

    /// <summary>
    /// Where the next chunk header begins, or null when the declared size points outside the file.
    /// Chunk bodies are word-aligned: an odd size is followed by a pad byte.
    /// </summary>
    private static long? NextChunkStart(Stream stream, long bodyStart, long size)
    {
        var next = bodyStart + size + (size % 2);
        return next <= bodyStart || next > stream.Length ? null : next;
    }

    /// <summary>
    /// Turns what the chunk walk found into a header, or explains what the file is missing.
    /// </summary>
    private static WaveHeader ToHeader(
        WaveFormat? format,
        long dataOffset,
        long dataLength,
        long fileLength,
        string wavPath)
    {
        if (format is not { } wave)
        {
            throw new MediaConversionException($"{wavPath} has no 'fmt ' chunk.");
        }

        if (dataOffset < 0)
        {
            throw new MediaConversionException($"{wavPath} has no 'data' chunk.");
        }

        EnsureSupported(wave, wavPath);

        // Streamed writers sometimes leave the data size as 0 or 0xFFFFFFFF; trust the file length.
        var available = fileLength - dataOffset;
        if (dataLength <= 0 || dataLength > available)
        {
            dataLength = available;
        }

        dataLength -= dataLength % (wave.Channels * (wave.BitsPerSample / 8));

        return new WaveHeader(wave.SampleRate, wave.Channels, wave.BitsPerSample, dataOffset, dataLength);
    }

    /// <summary>
    /// The pipeline decodes exactly one format, so anything else is rejected here with the reason
    /// rather than mis-decoded downstream.
    /// </summary>
    private static void EnsureSupported(WaveFormat format, string wavPath)
    {
        if (format.FormatTag != WaveFormatPcm)
        {
            throw new MediaConversionException(
                $"{wavPath} is not uncompressed PCM (format tag 0x{format.FormatTag:X4}). " +
                "Re-extract it with ffmpeg using -acodec pcm_s16le.");
        }

        if (format.SampleRate != ExpectedSampleRate ||
            format.Channels != ExpectedChannels ||
            format.BitsPerSample != ExpectedBitsPerSample)
        {
            throw new MediaConversionException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} is {1} Hz / {2}ch / {3}-bit; expected {4} Hz mono 16-bit PCM.",
                wavPath, format.SampleRate, format.Channels, format.BitsPerSample,
                ExpectedSampleRate));
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer, string wavPath)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException ex)
        {
            throw new MediaConversionException($"{wavPath} ended unexpectedly while reading its header.", ex);
        }
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, string ascii)
    {
        if (bytes.Length != ascii.Length)
        {
            return false;
        }

        for (var i = 0; i < ascii.Length; i++)
        {
            if (bytes[i] != (byte)ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>What a 'fmt ' chunk declares, with WAVE_FORMAT_EXTENSIBLE already resolved.</summary>
    private readonly record struct WaveFormat(
        int FormatTag,
        int SampleRate,
        int Channels,
        int BitsPerSample);

    private readonly record struct WaveHeader(
        int SampleRate,
        int Channels,
        int BitsPerSample,
        long DataOffset,
        long DataLength);
}
