using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace NoteScribe.Core.Media;

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

        int? sampleRate = null, channels = null, bitsPerSample = null, formatTag = null;
        long dataOffset = -1, dataLength = 0;

        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[16];
        Span<byte> extension = stackalloc byte[24];

        while (stream.Position + 8 <= stream.Length)
        {
            ReadExactly(stream, chunkHeader, wavPath);
            var id = Encoding.ASCII.GetString(chunkHeader[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            var bodyStart = stream.Position;

            if (id is "fmt ")
            {
                if (size < 16)
                {
                    throw new MediaConversionException($"{wavPath} has a truncated 'fmt ' chunk.");
                }

                ReadExactly(stream, fmt, wavPath);
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[..2]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..4]);
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..8]);
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..16]);

                if (formatTag == WaveFormatExtensible && size >= 40)
                {
                    // WAVE_FORMAT_EXTENSIBLE keeps the real tag in the first two bytes of SubFormat.
                    ReadExactly(stream, extension, wavPath);
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(extension[8..10]);
                }
            }
            else if (id is "data")
            {
                dataOffset = bodyStart;
                dataLength = size;
            }

            // Chunk bodies are word-aligned: an odd size is followed by a pad byte.
            var next = bodyStart + size + (size % 2);
            if (next <= bodyStart || next > stream.Length)
            {
                if (dataOffset >= 0)
                {
                    break;
                }

                throw new MediaConversionException($"{wavPath} has a malformed chunk '{id}'.");
            }

            stream.Seek(next, SeekOrigin.Begin);

            if (dataOffset >= 0 && sampleRate is not null)
            {
                break;
            }
        }

        if (sampleRate is null || channels is null || bitsPerSample is null)
        {
            throw new MediaConversionException($"{wavPath} has no 'fmt ' chunk.");
        }

        if (dataOffset < 0)
        {
            throw new MediaConversionException($"{wavPath} has no 'data' chunk.");
        }

        if (formatTag != WaveFormatPcm)
        {
            throw new MediaConversionException(
                $"{wavPath} is not uncompressed PCM (format tag 0x{formatTag:X4}). " +
                "Re-extract it with ffmpeg using -acodec pcm_s16le.");
        }

        if (sampleRate != ExpectedSampleRate || channels != ExpectedChannels || bitsPerSample != ExpectedBitsPerSample)
        {
            throw new MediaConversionException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} is {1} Hz / {2}ch / {3}-bit; expected {4} Hz mono 16-bit PCM.",
                wavPath, sampleRate, channels, bitsPerSample,
                ExpectedSampleRate));
        }

        // Streamed writers sometimes leave the data size as 0 or 0xFFFFFFFF; trust the file length.
        var available = stream.Length - dataOffset;
        if (dataLength <= 0 || dataLength > available)
        {
            dataLength = available;
        }

        dataLength -= dataLength % (channels.Value * (bitsPerSample.Value / 8));

        return new WaveHeader(sampleRate.Value, channels.Value, bitsPerSample.Value, dataOffset, dataLength);
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

    private readonly record struct WaveHeader(
        int SampleRate,
        int Channels,
        int BitsPerSample,
        long DataOffset,
        long DataLength);
}
