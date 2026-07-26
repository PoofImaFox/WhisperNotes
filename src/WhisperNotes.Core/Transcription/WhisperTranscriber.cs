using System.Runtime.CompilerServices;
using System.Text;
using WhisperNotes.Core.Audio;
using Whisper.net;

namespace WhisperNotes.Core.Transcription;

/// <summary>
/// A loaded whisper.cpp model plus a configured processor. Building one costs a full read of the
/// weights, so a single instance is meant to be reused for every buffer of a session.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    /// <summary>
    /// whisper.cpp works on 30 s mel windows and behaves badly on sub-second input, so very short
    /// buffers (the tail flushed when the user hits Stop) get zero-padded up to one second.
    /// </summary>
    private const int MinimumDecodeSamples = AudioFrame.SampleRate;

    /// <summary>Only second-guess a stock phrase once whisper itself doubts there was speech at all.</summary>
    private const float StockPhraseNoSpeechThreshold = 0.6f;

    // Whisper reliably invents these on silence or on room tone. They are annotations, never the
    // speaker's words, so dropping them cannot lose meeting content.
    private static readonly HashSet<string> BracketedArtefacts = new(StringComparer.Ordinal)
    {
        "blank audio", "silence", "silent", "no speech", "no audio", "music", "background music",
        "background noise", "noise", "sound", "inaudible", "applause", "laughter", "laughs",
        "typing", "beep", "click", "static", "breathing", "coughing", "sighs", "pause",
        "clears throat", "wind", "buzzing", "speaking foreign language", "foreign"
    };

    // Caption-corpus residue. These are real words, so they are only dropped when the segment also
    // carries a high no-speech probability — a contractor who actually said "thank you" keeps it.
    private static readonly HashSet<string> StockPhrases = new(StringComparer.Ordinal)
    {
        "thank you", "thank you very much", "thanks for watching", "thank you for watching",
        "thanks for watching everyone", "please subscribe", "subscribe to my channel",
        "you", "bye", "bye bye", "the end"
    };

    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;

    // WhisperProcessor wraps a single native whisper_context; concurrent ProcessAsync calls corrupt it.
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private readonly string? _forcedLanguage;
    private int _disposed;

    private WhisperTranscriber(WhisperFactory factory, WhisperProcessor processor, string? forcedLanguage)
    {
        _factory = factory;
        _processor = processor;
        _forcedLanguage = forcedLanguage;
    }

    /// <summary>Loads the weights at <paramref name="modelPath"/> and configures a decoder. Blocking.</summary>
    public static WhisperTranscriber Create(string modelPath, TranscriptionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Whisper model not found at '{modelPath}'.", modelPath);
        }

        WhisperRuntime.Prepare(options.UseGpu);

        WhisperFactory factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions
        {
            UseGpu = options.UseGpu,
            GpuDevice = options.GpuDevice,

            // Not a marginal tuning knob — it is most of the GPU win. Measured on an RTX 3080 with
            // large-v3-turbo: 11x realtime without it, ~80x with. It helps the CPU path too
            // (1.7x -> 2.1x), so there is no backend worth switching it off for. The one thing it
            // is incompatible with is DTW timestamps, which we do not ask for.
            UseFlashAttention = true
        });

        // The factory is what forces the native library to load, so this is the earliest moment
        // anyone can be told which backend the process ended up on.
        WhisperRuntime.MarkResolved();

        try
        {
            bool detectLanguage = string.IsNullOrWhiteSpace(options.Language)
                || options.Language.Equals("auto", StringComparison.OrdinalIgnoreCase);

            WhisperProcessorBuilder builder = factory.CreateBuilder()
                .WithThreads(options.Threads is > 0 ? options.Threads.Value : Math.Clamp(Environment.ProcessorCount, 1, 8))
                .WithTemperature(0f)
                .WithProbabilities()
                // The same processor decodes many chunks in a row; carrying the previous chunk's text
                // forward as context is what makes whisper fall into runaway repetition loops.
                .WithNoContext();

            builder = detectLanguage ? builder.WithLanguageDetection() : builder.WithLanguage(options.Language);

            if (options.Translate)
            {
                builder = builder.WithTranslate();
            }

            if (!string.IsNullOrWhiteSpace(options.InitialPrompt))
            {
                builder = builder.WithPrompt(options.InitialPrompt);
            }

            return new WhisperTranscriber(factory, builder.Build(), detectLanguage ? null : options.Language);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        ReadOnlyMemory<float> pcm16kMono,
        TimeSpan offset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (pcm16kMono.Length == 0)
        {
            yield break;
        }

        ReadOnlyMemory<float> audio = Pad(pcm16kMono);

        await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (SegmentData segment in _processor.ProcessAsync(audio, cancellationToken).ConfigureAwait(false))
            {
                TranscriptSegment? mapped = Map(segment, offset, _forcedLanguage);
                if (mapped is not null)
                {
                    yield return mapped;
                }
            }
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Never free the native context while a decode is still inside it.
        await _decodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _decodeGate.Release();
            _decodeGate.Dispose();
            _factory.Dispose();
        }
    }

    private static ReadOnlyMemory<float> Pad(ReadOnlyMemory<float> audio)
    {
        if (audio.Length >= MinimumDecodeSamples)
        {
            return audio;
        }

        float[] padded = new float[MinimumDecodeSamples];
        audio.Span.CopyTo(padded);
        return padded;
    }

    private static TranscriptSegment? Map(SegmentData segment, TimeSpan offset, string? forcedLanguage)
    {
        string text = segment.Text.Trim();
        if (!IsTrustworthy(text, segment))
        {
            return null;
        }

        float confidence = segment.Probability;
        if (!float.IsFinite(confidence) || confidence <= 0f)
        {
            // whisper.cpp leaves the token-probability aggregate at zero for some sampling paths.
            // NoSpeechProbability is always populated, so invert it as a coarse stand-in.
            confidence = 1f - segment.NoSpeechProbability;
        }

        string? language = string.IsNullOrWhiteSpace(segment.Language) ? forcedLanguage : segment.Language;

        return new TranscriptSegment(
            offset + segment.Start,
            offset + segment.End,
            text,
            Math.Clamp(confidence, 0f, 1f),
            language);
    }

    private static bool IsTrustworthy(string text, SegmentData segment)
    {
        if (text.Length == 0)
        {
            return false;
        }

        string normalised = NormaliseForMatch(text);
        if (normalised.Length == 0)
        {
            // Nothing but punctuation or symbols: ".", "...", "♪♪", "- -".
            return false;
        }

        if (IsFullyBracketed(text) && BracketedArtefacts.Contains(normalised))
        {
            return false;
        }

        return !(segment.NoSpeechProbability >= StockPhraseNoSpeechThreshold && StockPhrases.Contains(normalised));
    }

    private static bool IsFullyBracketed(string text) =>
        text.Length >= 2
        && text[0] is '[' or '(' or '{' or '*'
        && text[^1] is ']' or ')' or '}' or '*';

    /// <summary>Lower-cases and reduces everything that is not a letter, digit or apostrophe to a single space.</summary>
    private static string NormaliseForMatch(string text)
    {
        StringBuilder builder = new(text.Length);
        bool pendingSpace = false;

        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c) || c == '\'')
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }
}
