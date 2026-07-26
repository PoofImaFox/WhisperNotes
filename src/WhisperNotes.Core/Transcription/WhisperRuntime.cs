using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace WhisperNotes.Core.Transcription;

/// <summary>Which native whisper.cpp build the process actually ended up running on.</summary>
public enum WhisperBackend
{
    /// <summary>No model has been loaded yet, so nothing has been resolved.</summary>
    Unresolved,

    /// <summary>Plain CPU inference. Roughly 2x realtime on large-v3-turbo.</summary>
    Cpu,

    /// <summary>Vulkan compute. The shipped GPU path.</summary>
    Vulkan,

    /// <summary>CUDA, only reachable in a <c>-p:WhisperCudaRuntime=true</c> build.</summary>
    Cuda,

    /// <summary>A backend we do not ship — reported rather than guessed at.</summary>
    Other
}

/// <summary>One compute device ggml reported while it initialised.</summary>
/// <param name="Index">What to put in <c>Gpu.Device</c> to select this adapter.</param>
/// <param name="Description">
/// ggml's own words for it, minus the index — e.g.
/// <c>NVIDIA GeForce RTX 3080 (NVIDIA) | uma: 0 | fp16: 1 | …</c>. Kept verbatim rather than picked
/// apart, because the wording differs per backend and belongs to whisper.cpp, not to us.
/// </param>
public readonly record struct WhisperDevice(int Index, string Description);

/// <summary>
/// Owns the two process-global decisions Whisper.net makes exactly once: which native runtime to
/// load, and where its log goes. Both have to be set before the first <c>WhisperFactory</c> exists,
/// which is why they live here rather than in <see cref="WhisperTranscriber"/>'s constructor.
/// </summary>
public static class WhisperRuntime
{
    /// <summary>
    /// Plenty for the handful of adapters a machine has, and a hard stop on a static list growing
    /// for the life of a long-running UI session.
    /// </summary>
    private const int MaxDevices = 16;

    /// <summary>ggml-vulkan tags its device lines; ggml-cuda indents its own and tags only the header.</summary>
    private const string VulkanPrefix = "ggml_vulkan: ";
    private const string CudaPrefix = "Device ";

    private static readonly Lock Gate = new();
    private static readonly List<WhisperDevice> Devices = [];

    private static bool _prepared;
    private static bool _resolved;

    /// <summary>
    /// Raised once, the first time a model load settles on a backend, after
    /// <see cref="LoadedBackend"/> and <see cref="DeviceReport"/> are populated.
    /// </summary>
    /// <remarks>
    /// A UI cannot report the backend without this. Nothing resolves until the first model is
    /// loaded, and that happens on a background thread inside a decode that has already started —
    /// well after any transport flag a view model could hang a refresh off. Handlers run on the
    /// loading thread, so a subscriber that touches UI state has to marshal.
    /// <para>
    /// Static, and therefore a root: subscribers stay alive until they unsubscribe.
    /// </para>
    /// </remarks>
    public static event EventHandler? Resolved;

    /// <summary>
    /// Picks the native runtime and starts capturing ggml's device banner. Safe to call before
    /// every load; only the first call in the process does anything.
    /// </summary>
    /// <param name="preferGpu">
    /// False pins the CPU build. This only decides which native library is loaded — turning the GPU
    /// off for one transcriber while leaving it on for others is <c>UseGpu</c> on the factory.
    /// </param>
    /// <remarks>
    /// Whisper.net resolves the native library once, on the first factory, and ignores
    /// <c>RuntimeLibraryOrder</c> from then on. The first transcriber built therefore fixes the
    /// backend for the whole process and a later <paramref name="preferGpu"/> cannot move it.
    /// </remarks>
    public static void Prepare(bool preferGpu)
    {
        lock (Gate)
        {
            if (_prepared)
            {
                return;
            }

            _prepared = true;

            // Cuda leads even though we do not ship it by default: in a -p:WhisperCudaRuntime=true
            // build it should win, and when the package is absent the loader simply steps past it.
            RuntimeOptions.RuntimeLibraryOrder = preferGpu
                ? [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu]
                : [RuntimeLibrary.Cpu];

            // Deliberately never disposed: the subscription has to outlive every transcriber, and
            // the process exiting is the only point at which it stops being wanted.
            LogProvider.AddLogger(Capture);
        }
    }

    /// <summary>
    /// Called by <see cref="WhisperTranscriber"/> once a factory has been built, which is the point
    /// the native library has been chosen and ggml has finished announcing its devices.
    /// </summary>
    internal static void MarkResolved()
    {
        lock (Gate)
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;
        }

        // Outside the lock: a handler is free to read DeviceReport, which takes the same one.
        Resolved?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// What the first load resolved to, or <see cref="WhisperBackend.Unresolved"/> before then.
    /// </summary>
    public static WhisperBackend LoadedBackend => RuntimeOptions.LoadedLibrary switch
    {
        null => WhisperBackend.Unresolved,
        RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx => WhisperBackend.Cpu,
        RuntimeLibrary.Vulkan => WhisperBackend.Vulkan,
        RuntimeLibrary.Cuda => WhisperBackend.Cuda,
        _ => WhisperBackend.Other
    };

    /// <summary>True once a load has resolved to a backend that decodes on the GPU.</summary>
    public static bool IsGpuAccelerated => LoadedBackend is WhisperBackend.Vulkan or WhisperBackend.Cuda;

    /// <summary>
    /// The compute devices ggml reported, in enumeration order. Empty until a model has been
    /// loaded, and empty on the CPU backend, which has no devices to announce.
    /// </summary>
    /// <remarks>
    /// Recovered from log lines, because whisper.cpp offers no other way to find out which adapter
    /// it bound to. Only the index is interpreted; the rest is passed through, so a change to
    /// ggml's wording costs detail rather than silently mis-reporting the device.
    /// </remarks>
    public static IReadOnlyList<WhisperDevice> DeviceReport
    {
        get
        {
            lock (Gate)
            {
                return [.. Devices];
            }
        }
    }

    private static void Capture(WhisperLogLevel level, string? message)
    {
        if (level > WhisperLogLevel.Info || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (TryReadDevice(message) is not { } device)
        {
            return;
        }

        lock (Gate)
        {
            // Loading a second model re-announces the same hardware; the report is about the
            // machine, not about how many times it has been asked.
            if (Devices.Count < MaxDevices && !Devices.Contains(device))
            {
                Devices.Add(device);
            }
        }
    }

    /// <summary>
    /// Recognises the two shapes ggml announces a device in, or returns null for the rest of the
    /// log. The backends do not agree on a format:
    /// <code>
    /// ggml_vulkan: Found 2 Vulkan devices:                       &lt;- header, not a device
    /// ggml_vulkan: 0 = NVIDIA GeForce RTX 3080 (NVIDIA) | uma: 0 | ...
    /// ggml_cuda_init: found 1 CUDA devices (Total VRAM: 10240 MiB):   &lt;- header, not a device
    ///   Device 0: NVIDIA GeForce RTX 3080, compute capability 8.6, ...
    /// </code>
    /// CUDA indents its entries and does not repeat its own prefix on them, so matching on the
    /// prefixes alone would take the headers and miss the devices. What both entries have and
    /// neither header has is a leading device index, and that is what is matched.
    /// </summary>
    internal static WhisperDevice? TryReadDevice(string message)
    {
        var line = message.Trim();

        if (line.StartsWith(VulkanPrefix, StringComparison.Ordinal))
        {
            return TryReadIndexed(line[VulkanPrefix.Length..], '=');
        }

        return line.StartsWith(CudaPrefix, StringComparison.Ordinal)
            ? TryReadIndexed(line[CudaPrefix.Length..], ':')
            : null;
    }

    /// <summary>Parses "<c>0 = NVIDIA …</c>" / "<c>0: NVIDIA …</c>" into its index and the rest.</summary>
    private static WhisperDevice? TryReadIndexed(string text, char separator)
    {
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        if (digits == 0 || !int.TryParse(text[..digits], out var index))
        {
            return null;
        }

        var rest = text[digits..].TrimStart();
        if (rest.Length == 0 || rest[0] != separator)
        {
            return null;
        }

        var description = rest[1..].Trim();
        return description.Length == 0 ? null : new WhisperDevice(index, description);
    }
}
