using System.Globalization;
using System.Runtime.InteropServices;

namespace WhisperNotes.Core.Audio;

/// <summary>
/// The Win32/COM surface needed to open a WASAPI audio client bound to one process's render
/// stream instead of to an audio endpoint.
/// </summary>
/// <remarks>
/// <para>
/// NAudio cannot reach this path at all: a process loopback client is not produced from an
/// <c>IMMDevice</c>. It comes from <c>ActivateAudioInterfaceAsync</c> against the virtual device
/// path <c>VAD\Process_Loopback</c>, with the target process id smuggled through a
/// <c>PROPVARIANT</c> of type <c>VT_BLOB</c> pointing at an <c>AUDIOCLIENT_ACTIVATION_PARAMS</c>.
/// Everything here mirrors Microsoft's <c>ApplicationLoopback</c> sample, which is the only
/// normative description of the sequence.
/// </para>
/// <para>
/// Two consequences of that design leak into every caller. First, activation is asynchronous and
/// its completion handler is invoked on an arbitrary MTA worker thread, so the calling thread must
/// itself be in the MTA (see <see cref="EnterMultiThreadedApartment"/>) and must implement
/// <c>IAgileObject</c> on the handler or the call fails with <c>E_ILLEGAL_METHOD_CALL</c>. Second,
/// <c>IAudioClient::GetMixFormat</c> does not work on the virtual device — there is no mix format
/// to report — so the caller has to name a format itself and let the engine convert.
/// </para>
/// </remarks>
internal static class ProcessLoopbackInterop
{
    /// <summary>The <c>VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK</c> pseudo device path.</summary>
    internal const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";

    /// <summary><c>AUDCLNT_SHAREMODE_SHARED</c>. Exclusive mode is not available on this device.</summary>
    internal const int ShareModeShared = 0;

    /// <summary><c>AUDCLNT_STREAMFLAGS_LOOPBACK</c> — capture what the target renders.</summary>
    internal const int StreamFlagsLoopback = 0x0002_0000;

    /// <summary><c>AUDCLNT_STREAMFLAGS_EVENTCALLBACK</c> — signal an event per packet.</summary>
    internal const int StreamFlagsEventCallback = 0x0004_0000;

    /// <summary><c>AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY</c>.</summary>
    internal const int StreamFlagsSrcDefaultQuality = 0x0800_0000;

    /// <summary><c>AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM</c> — insert the engine's format converter.</summary>
    internal const int StreamFlagsAutoConvertPcm = unchecked((int)0x8000_0000);

    /// <summary><c>AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY</c> — audio was dropped before this packet.</summary>
    internal const int BufferFlagsDataDiscontinuity = 0x1;

    /// <summary><c>AUDCLNT_BUFFERFLAGS_SILENT</c> — treat the packet as zeroes whatever it contains.</summary>
    internal const int BufferFlagsSilent = 0x2;

    /// <summary><c>WAVE_FORMAT_PCM</c>.</summary>
    internal const ushort WaveFormatPcm = 1;

    /// <summary><c>WAVE_FORMAT_IEEE_FLOAT</c>.</summary>
    internal const ushort WaveFormatIeeeFloat = 3;

    /// <summary><c>AUDCLNT_S_BUFFER_EMPTY</c> — a success code, so it must be tested before the sign.</summary>
    internal const int BufferEmpty = 0x0889_0001;

    /// <summary><c>COINIT_MULTITHREADED</c>.</summary>
    private const int CoinitMultithreaded = 0x0;

    private const ushort VtBlob = 65;

    private const int ActivationTypeProcessLoopback = 1;
    private const int LoopbackModeIncludeTargetProcessTree = 0;
    private const int LoopbackModeExcludeTargetProcessTree = 1;

    private const int RpcChangedMode = unchecked((int)0x8001_0106);
    private const int NotInitialized = unchecked((int)0x8889_0001);
    private const int AlreadyInitialized = unchecked((int)0x8889_0002);
    private const int WrongEndpointType = unchecked((int)0x8889_0003);
    private const int DeviceInvalidated = unchecked((int)0x8889_0004);
    private const int UnsupportedFormat = unchecked((int)0x8889_0008);
    private const int ServiceNotRunning = unchecked((int)0x8889_0010);
    private const int ResourcesInvalidated = unchecked((int)0x8889_0026);
    private const int ProcessNotFound = unchecked((int)0x8007_0490);

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    /// <summary>
    /// Activates an <see cref="IAudioClient"/> that taps <paramref name="processId"/>'s render
    /// stream. The returned client is <em>not</em> initialised.
    /// </summary>
    /// <param name="processId">Live target process id.</param>
    /// <param name="includeProcessTree">
    /// True to capture the target and its children — the right default for browsers and Electron
    /// apps, where the audio is rendered by a child process and not by the window you picked.
    /// </param>
    /// <param name="timeoutMilliseconds">How long to wait for the activation callback.</param>
    /// <remarks>
    /// Must be called from an MTA thread. The activation parameters live in unmanaged memory only
    /// because <c>ActivateAudioInterfaceAsync</c> reads them through a raw pointer; they are freed
    /// as soon as the callback has run, which is the same lifetime the C++ sample gets by putting
    /// them on the stack.
    /// </remarks>
    internal static IAudioClient Activate(int processId, bool includeProcessTree, int timeoutMilliseconds)
    {
        var handler = new ActivationCompletionHandler();
        nint activationParams = AllocateActivationParams(processId, includeProcessTree, out int paramsSize);
        nint propVariant = 0;
        bool abandonNativeMemory = false;

        try
        {
            propVariant = AllocateBlobPropVariant(activationParams, paramsSize);

            Guid iid = IidAudioClient;
            ThrowIfFailed(
                ActivateAudioInterfaceAsync(
                    VirtualAudioDeviceProcessLoopback, in iid, propVariant, handler, out IActivateAudioInterfaceAsyncOperation? operation),
                "ActivateAudioInterfaceAsync");

            try
            {
                if (!handler.Completed.Wait(timeoutMilliseconds))
                {
                    // The callback may still fire and re-read the blob, so the memory is
                    // deliberately leaked rather than freed under a live reader. This is a
                    // terminal path: the session is about to fail either way.
                    abandonNativeMemory = true;
                    throw new AudioCaptureException(string.Create(
                        CultureInfo.CurrentCulture,
                        $"Windows did not answer the per-application capture request for process {processId} within {timeoutMilliseconds / 1000} seconds. The Windows Audio service may be wedged; restarting it (or the machine) usually clears this."));
                }
            }
            finally
            {
                ReleaseComObject(operation);
            }

            return TakeAudioClient(handler, processId);
        }
        finally
        {
            if (!abandonNativeMemory)
            {
                // FreeHGlobal ignores a null pointer, so the PROPVARIANT does not need a guard for
                // the case where its own allocation is what threw.
                Marshal.FreeHGlobal(propVariant);
                Marshal.FreeHGlobal(activationParams);
            }

            // Windows AddRefs the CCW, which is what really keeps the handler alive; this only
            // stops the JIT collecting it before the interop call has been made.
            GC.KeepAlive(handler);
        }
    }

    /// <summary>
    /// Initialises <paramref name="client"/> for shared-mode event-driven loopback capture in
    /// <paramref name="format"/>, returning the raw HRESULT so a caller can try another format.
    /// </summary>
    /// <remarks>
    /// Both duration arguments are zero, matching the sample: the virtual device has no device
    /// period to align to, and naming one earns <c>AUDCLNT_E_INVALID_DEVICE_PERIOD</c>.
    /// </remarks>
    internal static int Initialize(IAudioClient client, WaveFormatEx format, int streamFlags)
    {
        ArgumentNullException.ThrowIfNull(client);

        nint native = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(format, native, false);
            return client.Initialize(ShareModeShared, streamFlags, 0L, 0L, native, 0);
        }
        finally
        {
            // WASAPI copies the format, so the block is dead the moment Initialize returns.
            Marshal.FreeHGlobal(native);
        }
    }

    /// <summary>Fetches the <see cref="IAudioCaptureClient"/> service from an initialised client.</summary>
    internal static IAudioCaptureClient GetCaptureClient(IAudioClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        Guid iid = IidAudioCaptureClient;
        ThrowIfFailed(client.GetService(in iid, out object? service), "IAudioClient::GetService(IAudioCaptureClient)");

        return service as IAudioCaptureClient
               ?? throw new AudioCaptureException("WASAPI returned an audio client that does not expose IAudioCaptureClient.");
    }

    /// <summary>
    /// Joins the multi-threaded apartment, returning true when the caller now owes a matching
    /// <see cref="LeaveMultiThreadedApartment"/>.
    /// </summary>
    /// <remarks>
    /// <c>Thread.SetApartmentState(MTA)</c> is normally enough, but the runtime only initialises
    /// COM lazily on first managed interop, and <c>ActivateAudioInterfaceAsync</c> is a plain
    /// P/Invoke that would get there first. Initialising explicitly costs one refcount and removes
    /// a whole class of <c>CO_E_NOTINITIALIZED</c> failure.
    /// </remarks>
    internal static bool EnterMultiThreadedApartment()
    {
        int hr = CoInitializeEx(0, CoinitMultithreaded);

        // The thread was already made an STA by someone else. COM is usable, we just do not own
        // the initialisation; activation will fail loudly later if the apartment really matters.
        if (hr == RpcChangedMode)
        {
            return false;
        }

        ThrowIfFailed(hr, "CoInitializeEx(COINIT_MULTITHREADED)");
        return true;
    }

    /// <summary>Balances a successful <see cref="EnterMultiThreadedApartment"/>.</summary>
    internal static void LeaveMultiThreadedApartment() => CoUninitialize();

    /// <summary>
    /// Registers the calling thread with the "Audio" MMCSS class, returning a handle to pass to
    /// <see cref="LeaveAudioSchedulingClass"/> — or 0, which is not an error worth reporting.
    /// </summary>
    /// <remarks>
    /// Without this the packet loop is an ordinary background thread, and a busy machine can leave
    /// it descheduled long enough for WASAPI's ring buffer to wrap, which shows up as dropped audio
    /// rather than as a failure. Microsoft's sample gets the same treatment by running on a locked
    /// Media Foundation work queue; this is the same guarantee without the MF dependency. Failure
    /// is survivable — the service is disabled on some server SKUs — so the handle is simply 0.
    /// </remarks>
    internal static nint JoinAudioSchedulingClass()
    {
        try
        {
            uint taskIndex = 0;
            return AvSetMmThreadCharacteristicsW("Audio", ref taskIndex);
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>Balances <see cref="JoinAudioSchedulingClass"/>; a zero handle is a no-op.</summary>
    internal static void LeaveAudioSchedulingClass(nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        _ = AvRevertMmThreadCharacteristics(handle);
    }

    /// <summary>Throws an <see cref="AudioCaptureException"/> when <paramref name="hr"/> is a failure.</summary>
    internal static void ThrowIfFailed(int hr, string operation)
    {
        if (hr >= 0)
        {
            return;
        }

        throw new AudioCaptureException(string.Create(CultureInfo.CurrentCulture, $"{operation} failed: {Describe(hr)}."));
    }

    /// <summary>Renders an HRESULT as something a user could act on.</summary>
    internal static string Describe(int hr)
    {
        string name = hr switch
        {
            NotInitialized => "AUDCLNT_E_NOT_INITIALIZED",
            AlreadyInitialized => "AUDCLNT_E_ALREADY_INITIALIZED",
            WrongEndpointType => "AUDCLNT_E_WRONG_ENDPOINT_TYPE",
            DeviceInvalidated => "AUDCLNT_E_DEVICE_INVALIDATED (the audio stream went away)",
            UnsupportedFormat => "AUDCLNT_E_UNSUPPORTED_FORMAT",
            ServiceNotRunning => "AUDCLNT_E_SERVICE_NOT_RUNNING (the Windows Audio service is stopped)",
            ResourcesInvalidated => "AUDCLNT_E_RESOURCES_INVALIDATED",
            ProcessNotFound => "the target process no longer exists",
            // IntPtr.Zero, not the default overload: the default consults the calling thread's
            // IErrorInfo, which here belongs to whatever COM call ran last and is usually a lie.
            _ => Marshal.GetExceptionForHR(hr, IntPtr.Zero)?.Message ?? "unrecognised error"
        };

        return string.Create(CultureInfo.InvariantCulture, $"{name} (0x{hr:X8})");
    }

    /// <summary>Releases an RCW we own outright, swallowing the failures that only happen at teardown.</summary>
    internal static void ReleaseComObject(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(comObject);
        }
        catch (Exception)
        {
            // Teardown is best-effort: an already-released or invalidated RCW has nothing left to
            // free, and throwing here would mask the failure that caused the teardown.
        }
    }

    /// <summary>Builds a <c>WAVEFORMATEX</c> from the four numbers that actually vary.</summary>
    internal static WaveFormatEx CreateWaveFormat(ushort formatTag, ushort channels, uint sampleRate, ushort bitsPerSample)
    {
        ushort blockAlign = (ushort)(channels * bitsPerSample / 8);

        return new WaveFormatEx
        {
            FormatTag = formatTag,
            Channels = channels,
            SamplesPerSecond = sampleRate,
            AverageBytesPerSecond = sampleRate * blockAlign,
            BlockAlign = blockAlign,
            BitsPerSample = bitsPerSample,
            ExtraSize = 0
        };
    }

    private static IAudioClient TakeAudioClient(ActivationCompletionHandler handler, int processId)
    {
        if (handler.Failure is not null)
        {
            throw new AudioCaptureException(
                string.Create(CultureInfo.CurrentCulture, $"Per-application capture activation for process {processId} faulted."),
                handler.Failure);
        }

        ThrowIfFailed(handler.ActivateResult, string.Create(CultureInfo.CurrentCulture, $"Process loopback activation for process {processId}"));

        return handler.ActivatedInterface as IAudioClient
               ?? throw new AudioCaptureException(string.Create(
                   CultureInfo.CurrentCulture,
                   $"Process loopback activation for process {processId} reported success but produced no IAudioClient."));
    }

    private static nint AllocateActivationParams(int processId, bool includeProcessTree, out int size)
    {
        var activation = new AudioClientActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = includeProcessTree
                ? LoopbackModeIncludeTargetProcessTree
                : LoopbackModeExcludeTargetProcessTree
        };

        size = Marshal.SizeOf<AudioClientActivationParams>();
        nint block = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(activation, block, false);
        return block;
    }

    private static nint AllocateBlobPropVariant(nint blob, int blobSize)
    {
        int size = Marshal.SizeOf<PropVariantBlob>();
        nint block = Marshal.AllocHGlobal(size);

        // PROPVARIANT has padding the managed struct does not describe; zero it so nothing but
        // the four fields below is ever read by mmdevapi.
        Marshal.Copy(new byte[size], 0, block, size);

        var propVariant = new PropVariantBlob
        {
            VarType = VtBlob,
            BlobSize = (uint)blobSize,
            BlobData = blob
        };

        Marshal.StructureToPtr(propVariant, block, false);
        return block;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        in Guid riid,
        nint activationParams,
        [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceCompletionHandler completionHandler,
        [MarshalAs(UnmanagedType.Interface)] out IActivateAudioInterfaceAsyncOperation? activationOperation);

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoInitializeEx(nint reserved, int coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("avrt.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AvSetMmThreadCharacteristicsW(
        [MarshalAs(UnmanagedType.LPWStr)] string taskName,
        ref uint taskIndex);

    [DllImport("avrt.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(nint handle);

    /// <summary>
    /// <c>AUDIOCLIENT_ACTIVATION_PARAMS</c>. The union in the C definition has exactly one member,
    /// so it flattens to the two <c>AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS</c> fields laid out after
    /// the activation type — three 4-byte fields, 12 bytes, identical on x86 and x64.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>
    /// The <c>VT_BLOB</c> shape of <c>PROPVARIANT</c>: 8 bytes of type/reserved, then the
    /// <c>BLOB</c> union member. Natural alignment puts <see cref="BlobData"/> at offset 16 on x64
    /// and 12 on x86, which is exactly where the real union lands.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariantBlob
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public nint BlobData;
    }

    /// <summary><c>WAVEFORMATEX</c>, packed to 1 so it is exactly the 18 bytes WASAPI expects.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }
}

/// <summary>Marker interface declaring a COM object safe to call from any apartment.</summary>
/// <remarks>
/// <c>ActivateAudioInterfaceAsync</c> rejects a completion handler that is not agile with
/// <c>E_ILLEGAL_METHOD_CALL</c>, because it has to call back from an MTA worker without risking a
/// marshalling deadlock. Implementing this on the managed handler is what makes its CCW answer
/// <c>QueryInterface(IID_IAgileObject)</c>.
/// </remarks>
[ComImport]
[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

/// <summary>Receives the result of an <c>ActivateAudioInterfaceAsync</c> call.</summary>
[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

/// <summary>Handle to an in-flight audio interface activation.</summary>
[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    /// <summary>Yields both the activation HRESULT and, on success, the activated interface.</summary>
    [PreserveSig]
    int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
}

/// <summary>
/// The WASAPI client. Declared here rather than reused from NAudio because NAudio's copy is
/// <c>internal</c> to its own assembly.
/// </summary>
/// <remarks>
/// Method order is the vtable order and must not be rearranged. Every method is
/// <see cref="PreserveSigAttribute"/> so a failure such as an unsupported format can be inspected
/// and retried instead of arriving as an opaque <see cref="COMException"/>.
/// </remarks>
[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, nint format, nint audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out int bufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out int paddingFrames);

    [PreserveSig]
    int IsFormatSupported(int shareMode, nint format, out nint closestMatch);

    [PreserveSig]
    int GetMixFormat(out nint deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(nint eventHandle);

    [PreserveSig]
    int GetService(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
}

/// <summary>The capture half of a WASAPI stream: packets in, packets released.</summary>
[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out nint data, out int framesToRead, out int flags, out long devicePosition, out long qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(int framesRead);

    [PreserveSig]
    int GetNextPacketSize(out int framesInNextPacket);
}

/// <summary>
/// Managed implementation of the activation callback: records the outcome and wakes the thread
/// that started the activation.
/// </summary>
/// <remarks>
/// It does nothing but capture the result. The C++ sample initialises the whole audio client
/// inside this callback, but that runs on a system worker thread we do not own — doing the work
/// back on our own capture thread keeps every WASAPI call, and every failure, on one thread.
/// </remarks>
internal sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    private const int Ok = 0;
    private const int Unexpected = unchecked((int)0x8000_FFFF);

    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once <see cref="ActivateCompleted"/> has run, successfully or not.</summary>
    internal Task Completed => _completed.Task;

    /// <summary>The HRESULT Windows reported for the activation itself.</summary>
    internal int ActivateResult { get; private set; } = Unexpected;

    /// <summary>The activated interface, or null when the activation failed.</summary>
    internal object? ActivatedInterface { get; private set; }

    /// <summary>A managed fault raised inside the callback, kept so the waiter can rethrow it.</summary>
    internal Exception? Failure { get; private set; }

    /// <inheritdoc />
    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(activateOperation);

            int callResult = activateOperation.GetActivateResult(out int activateResult, out object? activated);
            if (callResult < 0)
            {
                ActivateResult = callResult;
            }
            else
            {
                ActivateResult = activateResult;
                ActivatedInterface = activated;
            }
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
        finally
        {
            // The waiter is blocked on this; missing it would hang the capture thread until its
            // activation timeout, so it has to be signalled on every path.
            _completed.TrySetResult();
        }

        // Never let a managed exception cross back into COM: the caller is a system worker thread.
        return Ok;
    }
}
