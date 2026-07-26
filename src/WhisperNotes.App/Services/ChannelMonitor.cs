using WhisperNotes.Core.Audio;

namespace WhisperNotes.App.Services;

/// <summary>
/// Opens a throwaway tap on every enabled endpoint purely to drive the level meter, so the user
/// can prove they picked the right inputs <em>before</em> committing an hour of a client call to
/// them. The meter shows the loudest of them. Stops while a real recording owns the devices.
/// </summary>
internal sealed class ChannelMonitor(
    IAudioCaptureSourceFactory factory,
    Action<float> onPeak,
    Action<AudioChannel, Exception> onError)
    : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task[] _pumps = [];
    private string[] _monitored = [];
    private int _liveTaps;

    /// <summary>Endpoint ids currently tapped, in the order they were started.</summary>
    public IReadOnlyList<string> MonitoredChannelIds => _monitored;

    /// <summary>True while at least one tap is still delivering frames.</summary>
    public bool IsMonitoring => Volatile.Read(ref _liveTaps) > 0;

    /// <summary>
    /// Taps the whole set at once. The set is the unit of work: any change to it — an input added,
    /// removed, enabled, disabled or re-pointed — restarts every tap, which is cheap next to the
    /// alternative of diffing device ownership against live pumps.
    /// </summary>
    public async Task StartAsync(IReadOnlyList<AudioChannel> channels)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsAlreadyMonitoring(channels))
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            if (channels.Count == 0)
            {
                return;
            }

            var combined = new CombinedPeakMeter(channels.Count, onPeak);
            var cts = new CancellationTokenSource();
            _cts = cts;
            _monitored = [.. channels.Select(channel => channel.Id)];
            Volatile.Write(ref _liveTaps, channels.Count);
            _pumps = [.. channels.Select((channel, index) => PumpAsync(channel, combined.SinkFor(index), cts.Token))];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Same endpoints in the same order, and none of them has fallen over.</summary>
    private bool IsAlreadyMonitoring(IReadOnlyList<AudioChannel> channels)
    {
        if (_monitored.Length != channels.Count || _pumps.Length != channels.Count)
        {
            return false;
        }

        for (var i = 0; i < channels.Count; i++)
        {
            if (!string.Equals(_monitored[i], channels[i].Id, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // A retired tap means a restart is worth attempting; the device may be back.
        return _pumps.All(pump => !pump.IsCompleted);
    }

    private async Task StopCoreAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_pumps).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the monitor is torn down by cancelling it.
        }

        _cts.Dispose();
        _cts = null;
        _pumps = [];
        _monitored = [];
        Volatile.Write(ref _liveTaps, 0);
        onPeak(0f);
    }

    /// <summary>
    /// One endpoint's tap. It owns its own failure: only this tap retires, the surviving taps keep
    /// feeding the meter, and the shell is told which input went away.
    /// </summary>
    private async Task PumpAsync(AudioChannel channel, Action<float> report, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await using var source = factory.Create(channel);
            await foreach (var frame in source.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                report(AudioLevel.Peak(frame.Samples.Span));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            report(0f);
            Interlocked.Decrement(ref _liveTaps);
        }

        // Reported outside the catch so the tap is already counted out when the shell asks
        // whether anything is still listening.
        if (failure is not null)
        {
            onError(channel, failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
