using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.Wave;

namespace WhisperNotes.Core.Audio;

/// <summary>
/// Taps a single application's render stream via WASAPI process loopback and yields
/// <see cref="AudioFrame"/>s normalised to 16 kHz mono float.
/// </summary>
/// <remarks>
/// <para>
/// This is the "just Teams, not the whole machine" capture. Where <see cref="WasapiCaptureSource"/>
/// opens an endpoint and gets everything mixed into it, this opens the virtual
/// <c>VAD\Process_Loopback</c> device scoped to one process id, so a notification chime or a
/// browser tab playing music never lands in the transcript.
/// </para>
/// <para>
/// The whole WASAPI conversation runs on one dedicated MTA background thread, because
/// <c>ActivateAudioInterfaceAsync</c> demands the multi-threaded apartment and because keeping
/// activation, the event-driven packet loop and teardown on a single thread is the only cheap way
/// to keep the COM lifetimes honest. Frames reach the consumer through the same bounded
/// drop-oldest channel <see cref="WasapiCaptureSource"/> uses, so a slow consumer loses the oldest
/// audio instead of stalling the audio thread.
/// </para>
/// <para>
/// Single-use: one <see cref="CaptureAsync"/> per instance. Create a new source to capture again.
/// </para>
/// </remarks>
internal sealed class ProcessLoopbackCaptureSource : IAudioCaptureSource
{
    /// <summary>100 ms per frame: responsive enough for a level meter, coarse enough not to thrash.</summary>
    private const int FrameSamples = AudioFrame.SampleRate / 10;

    /// <summary>~3 s of slack before a slow consumer starts costing us the oldest audio.</summary>
    private const int QueueCapacity = 30;

    /// <summary>How long to wait for the activation callback before declaring the audio stack wedged.</summary>
    private const int ActivationTimeoutMilliseconds = 10_000;

    /// <summary>Upper bound on the whole activate-initialise-start sequence.</summary>
    private const int StartTimeoutMilliseconds = 20_000;

    /// <summary>How long teardown waits for the capture thread to unwind before giving up on it.</summary>
    private const int ThreadJoinMilliseconds = 5_000;

    /// <summary>
    /// Wake-up interval for the packet loop. A process that renders nothing signals no events at
    /// all, so this is only a liveness poll — the stop handle is what actually ends the loop.
    /// </summary>
    private const int PacketWaitMilliseconds = 2_000;

    /// <summary>Index of the stop handle in the wait set.</summary>
    private const int StopHandleIndex = 1;

    /// <summary>Floor for the scratch buffer so a tiny reported buffer size still avoids regrowth.</summary>
    private const int MinimumScratchBytes = 8 * 1024;

    /// <summary>
    /// Flags every rung shares.
    /// </summary>
    /// <remarks>
    /// <c>AUTOCONVERTPCM</c> is not the optional extra it is on a real endpoint. There is no mix
    /// format to match here, so without the engine's converter in the graph the device is free to
    /// refuse anything that is not exactly what the target happens to be rendering. Microsoft's
    /// sample sets it for the same reason.
    /// </remarks>
    private const int BaseStreamFlags =
        ProcessLoopbackInterop.StreamFlagsLoopback
        | ProcessLoopbackInterop.StreamFlagsEventCallback
        | ProcessLoopbackInterop.StreamFlagsAutoConvertPcm;

    /// <summary>
    /// Formats to offer <c>IAudioClient::Initialize</c>, best first.
    /// </summary>
    /// <remarks>
    /// <c>GetMixFormat</c> is not implemented on the process loopback device, so unlike endpoint
    /// capture there is nothing to ask — the format has to be asserted, and a wrong guess is a
    /// refusal rather than a renegotiation. 32-bit float at 48 kHz is what the engine mixes in, so
    /// it is the cheapest conversion and the one worth asking for first. The second rung is byte
    /// for byte the configuration Microsoft's own sample ships, flags included, so if the first is
    /// ever refused there is still one combination known to have shipped working.
    /// <see cref="AudioResampler"/> normalises either one.
    /// </remarks>
    private static readonly CaptureFormat[] FormatLadder =
    [
        new(ProcessLoopbackInterop.WaveFormatIeeeFloat, 2, 48_000, 32,
            BaseStreamFlags | ProcessLoopbackInterop.StreamFlagsSrcDefaultQuality, "32-bit float 48 kHz stereo"),
        new(ProcessLoopbackInterop.WaveFormatPcm, 2, 44_100, 16,
            BaseStreamFlags, "16-bit PCM 44.1 kHz stereo")
    ];

    private readonly Lock _gate = new();
    private readonly bool _includeProcessTree;

    private Thread? _thread;
    private AutoResetEvent? _packetReady;
    private ManualResetEvent? _stop;
    private ChannelWriter<AudioFrame>? _writer;
    private Exception? _startFailure;

    // Touched only by the capture thread between activation and teardown.
    private float[] _pending = [];
    private byte[] _scratch = [];
    private int _pendingCount;
    private long _emittedSamples;
    private int _discontinuities;

    private int _started;
    private int _disposed;

    /// <summary>Creates a tap on <paramref name="channel"/>'s process.</summary>
    /// <param name="channel">
    /// Must be an <see cref="AudioChannelKind.Application"/> channel whose
    /// <see cref="AudioChannel.ProcessId"/> the caller has already resolved to a live process.
    /// </param>
    /// <param name="includeProcessTree">
    /// True (the default) to capture the target and its children. Browsers, Electron apps and
    /// Teams all render audio from a child process, so excluding the tree usually captures silence.
    /// </param>
    /// <exception cref="AudioCaptureException">
    /// This Windows build predates the process loopback API — see <see cref="ProcessLoopbackSupport"/>.
    /// </exception>
    public ProcessLoopbackCaptureSource(AudioChannel channel, bool includeProcessTree = true)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.Kind != AudioChannelKind.Application)
        {
            throw new ArgumentException(
                $"Process loopback capture needs an {nameof(AudioChannelKind.Application)} channel, not {channel.Kind}.",
                nameof(channel));
        }

        if (channel.ProcessId <= 0)
        {
            throw new ArgumentException(
                $"Application channel '{channel.Name}' has no resolved process id; the caller must map the executable name to a live process first.",
                nameof(channel));
        }

        if (!ProcessLoopbackSupport.IsSupported)
        {
            throw new AudioCaptureException(ProcessLoopbackSupport.UnsupportedReason!);
        }

        Channel = channel;
        _includeProcessTree = includeProcessTree;
    }

    /// <inheritdoc />
    public AudioChannel Channel { get; }

    /// <summary>
    /// How many packets WASAPI has flagged as discontinuous, i.e. how many times audio was dropped
    /// before it reached us.
    /// </summary>
    /// <remarks>
    /// Deliberately reported rather than corrected. <see cref="AudioFrame.Offset"/> counts samples
    /// we emitted, so a drop shifts every later timestamp earlier by the length of the gap; the
    /// alternative — padding with silence from the device position — trades a known small drift for
    /// an unbounded one whenever the device clock and the engine clock disagree.
    /// </remarks>
    internal int DiscontinuityCount => Volatile.Read(ref _discontinuities);

    /// <inheritdoc />
    public IAsyncEnumerable<AudioFrame> CaptureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                $"'{Channel.Name}' is already being captured by this source. A {nameof(ProcessLoopbackCaptureSource)} is single-use — create another one.");
        }

        return CaptureCoreAsync(cancellationToken);
    }

    /// <inheritdoc />
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
        // Bounded + DropOldest is what lets the capture thread hand frames off without ever
        // blocking: a consumer that falls behind loses the oldest audio instead of stalling the
        // packet loop (which would eventually make WASAPI drop packets anyway).
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
                    // A process that is not playing anything produces no packets at all, so this
                    // simply waits — cancellation is what ends the stream.
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

    /// <summary>
    /// Spins up the capture thread and blocks until it has either started streaming or failed.
    /// </summary>
    /// <remarks>
    /// Deliberately synchronous, so that "the process has no audio session" or "this build cannot
    /// do process loopback" surfaces out of the first <c>await foreach</c> exactly as an endpoint
    /// failure does, rather than as a stream that silently produces nothing.
    /// </remarks>
    private void Start(ChannelWriter<AudioFrame> writer)
    {
        try
        {
            var packetReady = new AutoResetEvent(false);
            var stop = new ManualResetEvent(false);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() => CaptureThreadMain(writer, packetReady, stop, ready))
            {
                IsBackground = true,
                Name = string.Create(CultureInfo.InvariantCulture, $"WhisperNotes loopback pid {Channel.ProcessId}")
            };

            // ActivateAudioInterfaceAsync calls back from an MTA worker and rejects callers that
            // are not themselves agile; an STA here would deadlock or fail outright.
            thread.SetApartmentState(ApartmentState.MTA);

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                _packetReady = packetReady;
                _stop = stop;
                _writer = writer;
                _thread = thread;

                // Started while still holding the gate so a concurrent DisposeAsync either loses
                // outright (the check above throws) or finds a thread it can actually join —
                // Thread.Join on a thread that was published but never started would throw.
                thread.Start();
            }

            if (!ready.Task.Wait(StartTimeoutMilliseconds))
            {
                throw new AudioCaptureException(string.Create(
                    CultureInfo.CurrentCulture,
                    $"Per-application capture of '{Channel.Name}' did not start within {StartTimeoutMilliseconds / 1000} seconds."));
            }

            if (_startFailure is not null)
            {
                // Rethrow with the capture thread's stack intact — that is where the WASAPI call
                // that actually failed lives.
                ExceptionDispatchInfo.Capture(_startFailure).Throw();
            }
        }
        catch (Exception ex)
        {
            Teardown();
            writer.TryComplete();

            if (ex is AudioCaptureException or ObjectDisposedException)
            {
                throw;
            }

            throw new AudioCaptureException($"Could not start per-application audio capture on '{Channel.Name}'.", ex);
        }
    }

    private void Teardown()
    {
        Thread? thread;
        AutoResetEvent? packetReady;
        ManualResetEvent? stop;
        ChannelWriter<AudioFrame>? writer;

        lock (_gate)
        {
            thread = _thread;
            packetReady = _packetReady;
            stop = _stop;
            writer = _writer;

            _thread = null;
            _packetReady = null;
            _stop = null;
            _writer = null;
        }

        try
        {
            stop?.Set();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by the other caller of Teardown.
        }

        bool drained = thread is null || thread.Join(ThreadJoinMilliseconds);

        if (drained)
        {
            // Only safe once the thread is provably gone: WASAPI signals the packet handle from a
            // thread of its own, and closing it while the device still holds it would leave the OS
            // setting a recycled handle.
            packetReady?.Dispose();
            stop?.Dispose();

            _pending = [];
            _scratch = [];
            _pendingCount = 0;
        }

        writer?.TryComplete();
    }

    /// <summary>Owns every WASAPI call for one capture session, start to finish.</summary>
    private void CaptureThreadMain(
        ChannelWriter<AudioFrame> writer,
        AutoResetEvent packetReady,
        ManualResetEvent stop,
        TaskCompletionSource ready)
    {
        bool apartmentEntered = false;
        CaptureSession? session = null;

        try
        {
            try
            {
                apartmentEntered = ProcessLoopbackInterop.EnterMultiThreadedApartment();
                session = OpenSession(packetReady);
            }
            catch (Exception ex)
            {
                _startFailure = ex;
            }
            finally
            {
                // Start() is blocked on this, so it has to be signalled on every path.
                ready.TrySetResult();
            }

            if (session is null)
            {
                writer.TryComplete();
                return;
            }

            RunPacketLoop(session, writer, packetReady, stop);
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            // An exception escaping a thread entry point kills the process, and the consumer would
            // otherwise wait forever, so every fault becomes the stream's terminal exception.
            writer.TryComplete(
                new AudioCaptureException($"Per-application audio capture on '{Channel.Name}' stopped unexpectedly.", ex));
        }
        finally
        {
            session?.Close();

            if (apartmentEntered)
            {
                ProcessLoopbackInterop.LeaveMultiThreadedApartment();
            }
        }
    }

    /// <summary>Activates and initialises a client, walking <see cref="FormatLadder"/> until one sticks.</summary>
    /// <remarks>
    /// Each rung gets a freshly activated client. A rejected <c>Initialize</c> leaves the client in
    /// an officially undefined state, and Microsoft's own buffer-alignment workaround says to
    /// re-activate rather than retry in place, so re-using it to try the next format would be
    /// building on sand.
    /// </remarks>
    private CaptureSession OpenSession(AutoResetEvent packetReady)
    {
        List<string> refusals = [];

        foreach (CaptureFormat candidate in FormatLadder)
        {
            IAudioClient client = ProcessLoopbackInterop.Activate(
                Channel.ProcessId, _includeProcessTree, ActivationTimeoutMilliseconds);

            CaptureSession? session;
            try
            {
                session = TryOpen(client, candidate, packetReady, out int result);
                if (session is null)
                {
                    refusals.Add(string.Create(
                        CultureInfo.CurrentCulture,
                        $"{candidate.Description} refused with {ProcessLoopbackInterop.Describe(result)}"));
                }
            }
            catch (Exception)
            {
                ProcessLoopbackInterop.ReleaseComObject(client);
                throw;
            }

            if (session is not null)
            {
                return session;
            }

            ProcessLoopbackInterop.ReleaseComObject(client);
        }

        throw new AudioCaptureException(string.Create(
            CultureInfo.CurrentCulture,
            $"WASAPI would not open a process loopback stream on '{Channel.Name}' (process {Channel.ProcessId}) in any supported format: {string.Join("; ", refusals)}."));
    }

    /// <summary>
    /// Initialises and starts one candidate format, returning null (with the HRESULT) when only the
    /// format was rejected and throwing when something more fundamental went wrong.
    /// </summary>
    /// <remarks>
    /// Starting here rather than in the packet loop is what makes a dead stream a synchronous
    /// failure out of <see cref="Start"/> instead of a stream that quietly produces nothing.
    /// </remarks>
    private static CaptureSession? TryOpen(
        IAudioClient client,
        CaptureFormat candidate,
        AutoResetEvent packetReady,
        out int hresult)
    {
        hresult = ProcessLoopbackInterop.Initialize(client, candidate.ToNative(), candidate.StreamFlags);
        if (hresult < 0)
        {
            return null;
        }

        IAudioCaptureClient capture = ProcessLoopbackInterop.GetCaptureClient(client);

        try
        {
            // DangerousGetHandle is safe here because the event outlives the client: teardown only
            // disposes it once the capture thread has been joined and the client released.
            ProcessLoopbackInterop.ThrowIfFailed(
                client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle()), "IAudioClient::SetEventHandle");

            ProcessLoopbackInterop.ThrowIfFailed(client.GetBufferSize(out int bufferFrames), "IAudioClient::GetBufferSize");

            var session = new CaptureSession(client, capture, candidate, bufferFrames);
            ProcessLoopbackInterop.ThrowIfFailed(client.Start(), "IAudioClient::Start");
            return session;
        }
        catch (Exception)
        {
            // The client itself belongs to the caller, which releases it on the way out.
            ProcessLoopbackInterop.ReleaseComObject(capture);
            throw;
        }
    }

    /// <summary>Starts the stream and pumps it until teardown signals <paramref name="stop"/>.</summary>
    private void RunPacketLoop(
        CaptureSession session,
        ChannelWriter<AudioFrame> writer,
        AutoResetEvent packetReady,
        ManualResetEvent stop)
    {
        _pending = new float[FrameSamples];
        _pendingCount = 0;
        _emittedSamples = 0;
        _scratch = new byte[Math.Max(MinimumScratchBytes, session.BufferFrames * session.Format.BlockAlign)];

        WaitHandle[] waits = [packetReady, stop];
        nint mmcss = ProcessLoopbackInterop.JoinAudioSchedulingClass();

        try
        {
            while (WaitHandle.WaitAny(waits, PacketWaitMilliseconds) != StopHandleIndex)
            {
                // Drain on timeouts too: a driver that misses an event should cost us latency, not
                // the rest of the meeting.
                DrainPackets(session, writer);
            }
        }
        finally
        {
            ProcessLoopbackInterop.LeaveAudioSchedulingClass(mmcss);
        }
    }

    /// <summary>
    /// Reads every packet WASAPI has queued. There is no guarantee of one packet per event, so the
    /// loop runs until <c>GetNextPacketSize</c> reports nothing left.
    /// </summary>
    private void DrainPackets(CaptureSession session, ChannelWriter<AudioFrame> writer)
    {
        IAudioCaptureClient capture = session.Capture;

        while (true)
        {
            ProcessLoopbackInterop.ThrowIfFailed(
                capture.GetNextPacketSize(out int queued), "IAudioCaptureClient::GetNextPacketSize");

            if (queued <= 0)
            {
                return;
            }

            int result = capture.GetBuffer(out nint data, out int frames, out int flags, out _, out _);
            if (result == ProcessLoopbackInterop.BufferEmpty)
            {
                // Nothing was acquired, so there is nothing to release.
                return;
            }

            ProcessLoopbackInterop.ThrowIfFailed(result, "IAudioCaptureClient::GetBuffer");

            try
            {
                Consume(session, writer, data, frames, flags);
            }
            finally
            {
                // The HRESULT is deliberately dropped: this runs on the failure path too, where
                // the real error is already on its way up.
                _ = capture.ReleaseBuffer(frames);
            }
        }
    }

    /// <summary>Copies one WASAPI packet into the resampler and emits whatever whole frames fall out.</summary>
    private void Consume(CaptureSession session, ChannelWriter<AudioFrame> writer, nint data, int frames, int flags)
    {
        if ((flags & ProcessLoopbackInterop.BufferFlagsDataDiscontinuity) != 0)
        {
            // Plain increment: the capture thread is the only writer, and readers only ever want
            // the count for diagnostics.
            _discontinuities++;
        }

        int bytes = frames * session.Format.BlockAlign;
        if (bytes <= 0)
        {
            return;
        }

        if (_scratch.Length < bytes)
        {
            _scratch = new byte[bytes];
        }

        // Silence is written out rather than skipped: a gap that is not fed through the resampler
        // would compress the timeline and pull every later transcript timestamp forward.
        if ((flags & ProcessLoopbackInterop.BufferFlagsSilent) != 0 || data == 0)
        {
            Array.Clear(_scratch, 0, bytes);
        }
        else
        {
            Marshal.Copy(data, _scratch, 0, bytes);
        }

        session.Resampler.Write(_scratch, 0, bytes);
        Emit(session, writer);
    }

    private void Emit(CaptureSession session, ChannelWriter<AudioFrame> writer)
    {
        while (true)
        {
            int read = session.Resampler.Read(_pending, _pendingCount, _pending.Length - _pendingCount);
            if (read == 0)
            {
                return;
            }

            _pendingCount += read;
            if (_pendingCount < _pending.Length)
            {
                continue;
            }

            // Offset is derived from samples emitted, never from a wall clock: the two drift apart
            // under buffering and driver jitter, and transcript timestamps have to line up with the
            // audio rather than with elapsed real time.
            var offset = TimeSpan.FromSeconds((double)_emittedSamples / AudioFrame.SampleRate);

            float[] full = _pending;
            _emittedSamples += full.Length;

            // AudioFrame hands ownership to the consumer, so the next frame needs its own array.
            _pending = new float[FrameSamples];
            _pendingCount = 0;

            writer.TryWrite(new AudioFrame(full, offset));
        }
    }

    /// <summary>One rung of the <c>Initialize</c> format ladder.</summary>
    private sealed record CaptureFormat(
        ushort FormatTag,
        ushort Channels,
        uint SampleRate,
        ushort BitsPerSample,
        int StreamFlags,
        string Description)
    {
        /// <summary>Bytes per interleaved sample frame in this format.</summary>
        internal int BlockAlign => Channels * BitsPerSample / 8;

        /// <summary>The <c>WAVEFORMATEX</c> handed to <c>IAudioClient::Initialize</c>.</summary>
        internal ProcessLoopbackInterop.WaveFormatEx ToNative() =>
            ProcessLoopbackInterop.CreateWaveFormat(FormatTag, Channels, SampleRate, BitsPerSample);

        /// <summary>The same format as NAudio describes it, for the <see cref="AudioResampler"/> handoff.</summary>
        internal WaveFormat ToManaged() =>
            FormatTag == ProcessLoopbackInterop.WaveFormatIeeeFloat
                ? WaveFormat.CreateIeeeFloatWaveFormat((int)SampleRate, Channels)
                : new WaveFormat((int)SampleRate, BitsPerSample, Channels);
    }

    /// <summary>
    /// The live WASAPI objects for one capture run, plus the resampler built for their format.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="IDisposable"/>. These are COM references and an MTA affinity,
    /// not a resource a caller could sensibly dispose from anywhere: <see cref="Close"/> is only
    /// ever called from the thread that opened them, in that thread's own <c>finally</c>.
    /// </remarks>
    private sealed class CaptureSession
    {
        internal CaptureSession(IAudioClient client, IAudioCaptureClient capture, CaptureFormat format, int bufferFrames)
        {
            Client = client;
            Capture = capture;
            Format = format;
            BufferFrames = bufferFrames;
            Resampler = new AudioResampler(format.ToManaged());
        }

        internal IAudioClient Client { get; }

        internal IAudioCaptureClient Capture { get; }

        internal CaptureFormat Format { get; }

        internal int BufferFrames { get; }

        internal AudioResampler Resampler { get; }

        /// <summary>Stops the stream and releases both COM references.</summary>
        /// <remarks>
        /// A session only ever exists in the started state — <see cref="TryOpen"/> does not hand
        /// one back until <c>IAudioClient::Start</c> has succeeded — so there is no "started?" flag
        /// to consult here.
        /// </remarks>
        internal void Close()
        {
            try
            {
                // Best-effort: a stream whose target process exited has already stopped itself,
                // and there is nothing left to salvage by the time teardown runs.
                _ = Client.Stop();
            }
            catch (Exception)
            {
                // ignored
            }

            ProcessLoopbackInterop.ReleaseComObject(Capture);
            ProcessLoopbackInterop.ReleaseComObject(Client);
        }
    }
}
