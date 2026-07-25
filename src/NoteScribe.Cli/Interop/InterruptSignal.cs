using System.Runtime.InteropServices;

namespace NoteScribe.Cli.Interop;

/// <summary>
/// Turns Ctrl+C into a cancellation request instead of a process kill.
/// </summary>
/// <remarks>
/// This is how every meeting ends, so it must not lose the tail: the first interrupt cancels the
/// token and lets <c>listen</c> flush the buffered audio, append the last segments and finalize.
/// A second interrupt is passed through to the runtime, so a wedged flush is still escapable.
/// SIGTERM gets the same treatment — a console close or a <c>taskkill</c> should still finalize.
/// </remarks>
internal sealed class InterruptSignal : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly PosixSignalRegistration? _termination;

    private int _requests;
    private bool _disposed;

    public InterruptSignal()
    {
        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            _termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTermination);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
        {
            // Ctrl+C still works; only the "closed the window" path is lost.
        }

        // SIGINT is deliberately not registered: on Windows the runtime raises both CancelKeyPress
        // and the POSIX signal for the same Ctrl+C, which would consume the "second press" budget.
    }

    public CancellationToken Token => _cts.Token;

    /// <summary>True once the user has asked to stop — drives the 130 exit code.</summary>
    public bool Requested => Volatile.Read(ref _requests) > 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Console.CancelKeyPress -= OnCancelKeyPress;
        _termination?.Dispose();
        _cts.Dispose();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Cancel = true keeps the process alive so the finalize path can run.
        e.Cancel = Request();
    }

    private void OnTermination(PosixSignalContext context) => context.Cancel = Request();

    private bool Request()
    {
        if (Interlocked.Increment(ref _requests) > 1)
        {
            return false;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
            // A cancellation callback threw; the token is cancelled either way.
        }

        return true;
    }
}
