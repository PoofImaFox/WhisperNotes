using System.Runtime.CompilerServices;
using WhisperNotes.Cli.Rendering;
using WhisperNotes.Core.Audio;

namespace WhisperNotes.Cli.Audio;

/// <summary>
/// Passes capture through untouched while also writing it to disk, for <c>listen --keep-audio</c>.
/// </summary>
/// <remarks>
/// A failure writing the copy is reported once and then ignored: losing the optional recording is a
/// nuisance, losing the transcript that is being written at the same time is not acceptable.
/// </remarks>
internal sealed class RecordingCaptureSource : IAudioCaptureSource
{
    private readonly IAudioCaptureSource _inner;
    private readonly WavStreamWriter _writer;
    private readonly ConsoleOutput _console;

    private bool _writeFailed;

    public RecordingCaptureSource(IAudioCaptureSource inner, WavStreamWriter writer, ConsoleOutput console)
    {
        _inner = inner;
        _writer = writer;
        _console = console;
    }

    public AudioChannel Channel => _inner.Channel;

    public async IAsyncEnumerable<AudioFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (AudioFrame frame in _inner.CaptureAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_writeFailed)
            {
                try
                {
                    // CancellationToken.None: the last frames arrive as we are shutting down and
                    // they belong in the file just as much as the ones before them.
                    await _writer.WriteAsync(frame.Samples, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    _writeFailed = true;
                    _console.Warn($"stopped saving the audio copy: {ex.Message}");
                }
            }

            yield return frame;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
