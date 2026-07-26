using System.Buffers.Binary;
using System.Text;
using WhisperNotes.Core.Media;

namespace WhisperNotes.Core.Tests.Media;

/// <summary>
/// Characterisation tests for the RIFF/WAVE header walk. Every rejection here is a file the rest of
/// the pipeline would otherwise decode as noise, so which files are refused matters as much as which
/// are accepted.
/// </summary>
public sealed class WaveFileReaderServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "whispernotes-wav-" + Guid.CreateVersion7().ToString("n"));

    public WaveFileReaderServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory must not fail the suite.
        }
    }

    [Fact]
    public async Task ReadsSamplesAndDuration_FromACanonicalFile()
    {
        var path = await WriteAsync(Wave(Fmt(), Data(Samples(32_000))));

        var reader = new WaveFileReaderService();
        Assert.Equal(TimeSpan.FromSeconds(2), reader.GetDuration(path));

        var chunks = new List<int>();
        await foreach (var chunk in reader.ReadChunksAsync(path, 16_000, CancellationToken.None))
        {
            chunks.Add(chunk.Length);
        }

        Assert.Equal([16_000, 16_000], chunks);
    }

    [Fact]
    public async Task SkipsUnknownChunks_IncludingOddSizedOnesWithAPadByte()
    {
        var path = await WriteAsync(Wave(
            Chunk("LIST", [1, 2, 3]),
            Fmt(),
            Chunk("fact", [4, 5, 6, 7]),
            Data(Samples(1_600))));

        Assert.Equal(TimeSpan.FromSeconds(0.1), new WaveFileReaderService().GetDuration(path));
    }

    [Fact]
    public async Task AcceptsExtensibleFormat_WhenItsSubFormatIsPcm()
    {
        var path = await WriteAsync(Wave(ExtensibleFmt(pcmSubFormat: true), Data(Samples(1_600))));

        Assert.Equal(TimeSpan.FromSeconds(0.1), new WaveFileReaderService().GetDuration(path));
    }

    [Fact]
    public async Task RejectsExtensibleFormat_WhenItsSubFormatIsNotPcm()
    {
        var path = await WriteAsync(Wave(ExtensibleFmt(pcmSubFormat: false), Data(Samples(1_600))));

        Assert.Throws<MediaConversionException>(() => new WaveFileReaderService().GetDuration(path));
    }

    /// <summary>
    /// A streamed writer that never went back to patch the size field leaves 0 there; the audio is
    /// still all present, so the file length is the honest answer.
    /// </summary>
    [Fact]
    public async Task TrustsTheFileLength_WhenTheDataChunkSizeIsUnset()
    {
        var data = Data(Samples(1_600));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 0);

        var path = await WriteAsync(Wave(Fmt(), data));

        Assert.Equal(TimeSpan.FromSeconds(0.1), new WaveFileReaderService().GetDuration(path));
    }

    /// <summary>A half-written final frame is dropped rather than decoded as a sample of noise.</summary>
    [Fact]
    public async Task RoundsTheDataLengthDownToAWholeFrame()
    {
        var path = await WriteAsync(Wave(Fmt(), Chunk("data", new byte[(16_000 * 2) + 1])));

        Assert.Equal(TimeSpan.FromSeconds(1), new WaveFileReaderService().GetDuration(path));
    }

    [Theory]
    [InlineData(44_100, 1, 16)]
    [InlineData(16_000, 2, 16)]
    [InlineData(16_000, 1, 8)]
    public async Task RejectsAnythingButSixteenKilohertzMonoSixteenBit(int sampleRate, int channels, int bits)
    {
        var path = await WriteAsync(Wave(Fmt(sampleRate, channels, bits), Data(Samples(1_600))));

        Assert.Throws<MediaConversionException>(() => new WaveFileReaderService().GetDuration(path));
    }

    [Fact]
    public async Task RejectsCompressedAudio()
    {
        var path = await WriteAsync(Wave(Fmt(formatTag: 3), Data(Samples(1_600))));

        Assert.Throws<MediaConversionException>(() => new WaveFileReaderService().GetDuration(path));
    }

    [Fact]
    public async Task RejectsAFileThatIsNotRiffWave()
    {
        var bogus = Wave(Fmt(), Data(Samples(1_600)));
        Encoding.ASCII.GetBytes("RIFX").CopyTo(bogus, 0);

        var path = await WriteAsync(bogus);

        Assert.Throws<MediaConversionException>(() => new WaveFileReaderService().GetDuration(path));
    }

    [Fact]
    public async Task RejectsAFileWithNoFmtChunk()
    {
        var path = await WriteAsync(Wave(Data(Samples(1_600))));

        Assert.Throws<MediaConversionException>(() => new WaveFileReaderService().GetDuration(path));
    }

    [Fact]
    public async Task RejectsAFileWithNoDataChunk()
    {
        var path = await WriteAsync(Wave(Fmt()));

        Assert.Throws<MediaConversionException>(() => new WaveFileReaderService().GetDuration(path));
    }

    [Fact]
    public async Task RejectsATruncatedFmtChunk()
    {
        var path = await WriteAsync(Wave(Chunk("fmt ", new byte[12]), Data(Samples(1_600))));

        Assert.Throws<MediaConversionException>(() => new WaveFileReaderService().GetDuration(path));
    }

    /// <summary>A chunk claiming more bytes than the file holds is malformed, not merely short.</summary>
    [Fact]
    public async Task RejectsAChunkThatRunsPastTheEndOfTheFile()
    {
        var junk = Chunk("junk", [0, 0, 0, 0]);
        BinaryPrimitives.WriteUInt32LittleEndian(junk.AsSpan(4, 4), 4096);

        var path = await WriteAsync(Wave(junk, Fmt(), Data(Samples(1_600))));

        Assert.Throws<MediaConversionException>(() => new WaveFileReaderService().GetDuration(path));
    }

    [Fact]
    public async Task RejectsAFileTooShortToHoldAHeader()
    {
        var path = await WriteAsync([0x52, 0x49, 0x46, 0x46]);

        Assert.Throws<MediaConversionException>(() => new WaveFileReaderService().GetDuration(path));
    }

    private async Task<string> WriteAsync(byte[] content)
    {
        var path = Path.Combine(_directory, Guid.CreateVersion7().ToString("n") + ".wav");
        await File.WriteAllBytesAsync(path, content, CancellationToken.None);
        return path;
    }

    private static byte[] Wave(params byte[][] chunks)
    {
        var body = chunks.SelectMany(chunk => chunk).ToArray();
        var file = new byte[12 + body.Length];

        Encoding.ASCII.GetBytes("RIFF").CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4, 4), (uint)(4 + body.Length));
        Encoding.ASCII.GetBytes("WAVE").CopyTo(file, 8);
        body.CopyTo(file, 12);

        return file;
    }

    private static byte[] Chunk(string id, byte[] body)
    {
        // Chunk bodies are word-aligned: an odd size is followed by a pad byte.
        var padded = body.Length + (body.Length % 2);
        var chunk = new byte[8 + padded];

        Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4, 4), (uint)body.Length);
        body.CopyTo(chunk, 8);

        return chunk;
    }

    private static byte[] Fmt(
        int sampleRate = 16_000,
        int channels = 1,
        int bitsPerSample = 16,
        int formatTag = 1)
    {
        var body = new byte[16];
        WriteCommonFormat(body, formatTag, sampleRate, channels, bitsPerSample);
        return Chunk("fmt ", body);
    }

    private static byte[] ExtensibleFmt(bool pcmSubFormat)
    {
        var body = new byte[40];
        WriteCommonFormat(body, 0xFFFE, 16_000, 1, 16);

        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(16, 2), 22);   // cbSize
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(18, 2), 16);   // valid bits
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20, 4), 4);    // channel mask

        // KSDATAFORMAT_SUBTYPE_PCM is {00000001-...}; IEEE_FLOAT is {00000003-...}.
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24, 4), pcmSubFormat ? 1u : 3u);

        return Chunk("fmt ", body);
    }

    private static void WriteCommonFormat(byte[] body, int formatTag, int sampleRate, int channels, int bitsPerSample)
    {
        var blockAlign = channels * (bitsPerSample / 8);

        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), (ushort)formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2, 2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8, 4), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(12, 2), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14, 2), (ushort)bitsPerSample);
    }

    private static byte[] Data(byte[] samples) => Chunk("data", samples);

    private static byte[] Samples(int count) => new byte[count * 2];
}
