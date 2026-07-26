using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperNotes.Core.Audio;

/// <summary>
/// Taps a single WASAPI endpoint — a render endpoint in loopback mode, or a capture endpoint
/// directly — and yields <see cref="AudioFrame"/>s normalised to 16 kHz mono float.
/// </summary>
/// <remarks>
/// Single-use: one <see cref="CaptureAsync"/> per instance. Create a new source to capture again.
/// </remarks>
public sealed class WasapiCaptureSource : IAudioCaptureSource
{
    /// <summary>100 ms per frame: responsive enough for a level meter, coarse enough not to thrash.</summary>
    private const int FrameSamples = AudioFrame.SampleRate / 10;

    /// <summary>~3 s of slack before a slow consumer starts costing us the oldest audio.</summary>
    private const int QueueCapacity = 30;

    private const int MicrophoneBufferMilliseconds = 100;

    private readonly Lock _gate = new();

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private MMDeviceEnumerator? _devices;
    private AudioResampler? _resampler;
    private ChannelWriter<AudioFrame>? _writer;

    // Touched only by the NAudio capture thread between StartRecording and teardown.
    private float[] _pending = [];
    private int _pendingCount;
    private long _emittedSamples;

    private int _started;
    private int _disposed;

    public WasapiCaptureSource(AudioChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        Channel = channel;
    }

    public AudioChannel Channel { get; }

    public IAsyncEnumerable<AudioFrame> CaptureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                $"'{Channel.Name}' is already being captured by this source. A {nameof(WasapiCaptureSource)} is single-use — create another one.");
        }

        return CaptureCoreAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Teardown();
        return ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<AudioFrame> CaptureCoreAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Bounded + DropOldest is what lets the WASAPI callback hand frames off without ever
        // blocking: a consumer that falls behind loses the oldest audio instead of stalling
        // the capture device (which would eventually make WASAPI drop packets anyway).
        var queue = System.Threading.Channels.Channel.CreateBounded<AudioFrame>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

        Start(queue.Writer);

        try
        {
            ChannelReader<AudioFrame> reader = queue.Reader;

            while (true)
            {
                bool more;
                try
                {
                    // A silent render endpoint raises no DataAvailable at all on some drivers, so
                    // this simply waits — the stream produces nothing until audio starts flowing,
                    // and cancellation is what ends it.
                    more = await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancelling a live tap is a normal end of stream, not a fault.
                    more = false;
                }

                if (!more)
                {
                    break;
                }

                while (reader.TryRead(out AudioFrame frame))
                {
                    yield return frame;
                }
            }
        }
        finally
        {
            Teardown();
        }
    }

    private void Start(ChannelWriter<AudioFrame> writer)
    {
        MMDeviceEnumerator? devices = null;
        MMDevice? device = null;
        WasapiCapture? capture = null;

        try
        {
            devices = new MMDeviceEnumerator();
            device = devices.GetDevice(Channel.Id);

            if (device.State != DeviceState.Active)
            {
                throw new AudioCaptureException(
                    $"Audio endpoint '{Channel.Name}' is not available (state: {device.State}).");
            }

            capture = Channel.Kind switch
            {
                AudioChannelKind.Loopback => CreateLoopbackCapture(device),
                AudioChannelKind.Microphone => CreateMicrophoneCapture(device),
                _ => throw new AudioCaptureException($"Unsupported audio channel kind '{Channel.Kind}'.")
            };

            // Never assume the format: shared-mode WASAPI gives you whatever the endpoint's
            // mix format happens to be, and it differs per machine and per driver.
            var resampler = new AudioResampler(capture.WaveFormat);

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                _devices = devices;
                _device = device;
                _capture = capture;
                _resampler = resampler;
                _writer = writer;
                _pending = new float[FrameSamples];
                _pendingCount = 0;
                _emittedSamples = 0;
            }

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
        }
        catch (Exception ex)
        {
            capture?.Dispose();
            device?.Dispose();
            devices?.Dispose();

            lock (_gate)
            {
                _capture = null;
                _device = null;
                _devices = null;
                _resampler = null;
                _writer = null;
            }

            writer.TryComplete();

            if (ex is AudioCaptureException or ObjectDisposedException)
            {
                throw;
            }

            throw new AudioCaptureException($"Could not start audio capture on '{Channel.Name}'.", ex);
        }
    }

    private static WasapiCapture CreateLoopbackCapture(MMDevice device)
    {
        if (device.DataFlow != DataFlow.Render)
        {
            throw new AudioCaptureException(
                $"'{device.FriendlyName}' is a {device.DataFlow} endpoint; loopback capture needs a render endpoint.");
        }

        return new WasapiLoopbackCapture(device);
    }

    private static WasapiCapture CreateMicrophoneCapture(MMDevice device)
    {
        if (device.DataFlow != DataFlow.Capture)
        {
            throw new AudioCaptureException(
                $"'{device.FriendlyName}' is a {device.DataFlow} endpoint; microphone capture needs a capture endpoint.");
        }

        return new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: MicrophoneBufferMilliseconds);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        ChannelWriter<AudioFrame>? writer = _writer;
        AudioResampler? resampler = _resampler;

        if (writer is null || resampler is null || e.BytesRecorded <= 0)
        {
            return;
        }

        try
        {
            resampler.Write(e.Buffer, 0, e.BytesRecorded);

            while (true)
            {
                int read = resampler.Read(_pending, _pendingCount, _pending.Length - _pendingCount);
                if (read == 0)
                {
                    break;
                }

                _pendingCount += read;
                if (_pendingCount < _pending.Length)
                {
                    continue;
                }

                // Offset is derived from samples emitted, never from a wall clock: the two drift
                // apart under buffering and driver jitter, and transcript timestamps have to line
                // up with the audio rather than with elapsed real time.
                var offset = TimeSpan.FromSeconds((double)_emittedSamples / AudioFrame.SampleRate);

                float[] full = _pending;
                _emittedSamples += full.Length;

                // AudioFrame hands ownership to the consumer, so the next frame needs its own array.
                _pending = new float[FrameSamples];
                _pendingCount = 0;

                writer.TryWrite(new AudioFrame(full, offset));
            }
        }
        catch (Exception ex)
        {
            writer.TryComplete(
                new AudioCaptureException($"Audio capture on '{Channel.Name}' failed while converting data.", ex));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        ChannelWriter<AudioFrame>? writer = _writer;
        if (writer is null)
        {
            return;
        }

        if (e.Exception is null)
        {
            writer.TryComplete();
        }
        else
        {
            writer.TryComplete(
                new AudioCaptureException($"Audio capture on '{Channel.Name}' stopped unexpectedly.", e.Exception));
        }
    }

    private void Teardown()
    {
        WasapiCapture? capture;
        MMDevice? device;
        MMDeviceEnumerator? devices;
        ChannelWriter<AudioFrame>? writer;

        lock (_gate)
        {
            capture = _capture;
            device = _device;
            devices = _devices;
            writer = _writer;

            _capture = null;
            _device = null;
            _devices = null;
            _resampler = null;
            _writer = null;
        }

        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;

            // Teardown is best-effort by design: a device that vanished mid-session throws from
            // both calls, and there is nothing left to salvage by then.
            try
            {
                capture.StopRecording();
            }
            catch (Exception)
            {
                // ignored
            }

            try
            {
                capture.Dispose();
            }
            catch (Exception)
            {
                // ignored
            }
        }

        device?.Dispose();
        devices?.Dispose();
        writer?.TryComplete();

        _pending = [];
        _pendingCount = 0;
    }
}
