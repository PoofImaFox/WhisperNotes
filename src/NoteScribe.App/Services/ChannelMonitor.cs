using NoteScribe.Core.Audio;

namespace NoteScribe.App.Services;

/// <summary>
/// Opens a throwaway tap on the selected endpoint purely to drive the level meter, so the user
/// can prove they picked the right channel <em>before</em> committing an hour of a client call
/// to it. Stops while a real recording owns the device.
/// </summary>
internal sealed class ChannelMonitor(IAudioCaptureSourceFactory factory, Action<float> onPeak, Action<Exception> onError)
    : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public string? MonitoredChannelId { get; private set; }

    public async Task StartAsync(AudioChannel channel)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (string.Equals(MonitoredChannelId, channel.Id, StringComparison.Ordinal) && _pump is { IsCompleted: false })
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);

            var cts = new CancellationTokenSource();
            _cts = cts;
            MonitoredChannelId = channel.Id;
            _pump = PumpAsync(channel, cts.Token);
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

    private async Task StopCoreAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the monitor is torn down by cancelling it.
            }
        }

        _cts.Dispose();
        _cts = null;
        _pump = null;
        MonitoredChannelId = null;
        onPeak(0f);
    }

    private async Task PumpAsync(AudioChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            await using var source = factory.Create(channel);
            await foreach (var frame in source.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                onPeak(AudioLevel.Peak(frame.Samples.Span));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception ex)
        {
            onPeak(0f);
            onError(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
