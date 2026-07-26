namespace WhisperNotes.Core.Audio;

/// <summary>
/// Creates WASAPI-backed capture sources: device loopback, microphone, or a single application.
/// Holds no per-capture state and is safe to share; each call hands back a fresh single-use source.
/// </summary>
public sealed class WasapiCaptureSourceFactory : IAudioCaptureSourceFactory
{
    private readonly IAudioChannelEnumerator _channels;

    /// <summary>Production constructor: enumerates endpoints through <see cref="WasapiChannelEnumerator"/>.</summary>
    public WasapiCaptureSourceFactory()
        : this(new WasapiChannelEnumerator())
    {
    }

    /// <summary>
    /// Takes the enumerator used to locate the default render endpoint for the system-audio fallback.
    /// </summary>
    /// <remarks>
    /// Only the fallback path touches it, but injecting it is what makes that path testable without a
    /// sound card. Composition still uses the parameterless constructor.
    /// </remarks>
    public WasapiCaptureSourceFactory(IAudioChannelEnumerator channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _channels = channels;
    }

    public IAudioCaptureSource Create(AudioChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Kind switch
        {
            AudioChannelKind.Loopback or AudioChannelKind.Microphone => new WasapiCaptureSource(channel),
            AudioChannelKind.Application => CreateApplicationSource(channel),
            _ => throw new AudioCaptureException($"Unsupported audio channel kind '{channel.Kind}'.")
        };
    }

    /// <summary>
    /// Captures one application, or degrades to whole-system audio on a Windows build that cannot do it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fallback records everything the machine is playing, not just the chosen application.</b>
    /// Per-application capture is <c>ActivateAudioInterfaceAsync</c> with
    /// <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>, which does not exist before build
    /// <see cref="ProcessLoopbackSupport.MinimumBuild"/> — on Windows 10 22H2 (19045) there is no
    /// partial version of it to fall back to, and no shim that filters a device stream down to one
    /// process. The only two options are "capture everything" and "refuse to record".
    /// </para>
    /// <para>
    /// Refusing loses the user their meeting; capturing the whole device gets them a transcript that
    /// contains their meeting plus whatever else was audible. Recording wins, and
    /// <see cref="ProcessLoopbackSupport.UnsupportedReason"/> exists so the UI can say so plainly rather
    /// than letting the user discover it in the transcript.
    /// </para>
    /// </remarks>
    private IAudioCaptureSource CreateApplicationSource(AudioChannel channel)
    {
        // The id is the source of truth: ExecutableName can be absent on a channel rehydrated from an
        // older settings file, but "app:teams.exe" always carries the name.
        string? executable = channel.ExecutableName ?? ApplicationChannelId.ExecutableOf(channel.Id);

        if (!ProcessLoopbackSupport.IsSupported)
        {
            return CreateSystemAudioFallback(channel);
        }

        // Never trust a persisted pid: Windows recycles them, so a stale id does not just fail to
        // attach — it can attach to an unrelated process that happens to hold the number now.
        int? processId = AudioSessionCatalog.ResolveProcessId(executable);
        if (processId is null)
        {
            throw new AudioCaptureException(
                $"'{executable ?? channel.Name}' is not playing any audio right now, so there is no process to capture. Start it and try again, or pick a different input.");
        }

        return new ProcessLoopbackCaptureSource(
            channel with { ProcessId = processId.Value, ExecutableName = executable });
    }

    private IAudioCaptureSource CreateSystemAudioFallback(AudioChannel channel)
    {
        AudioChannel? loopback = DefaultLoopback();

        return loopback is null
            ? throw new AudioCaptureException(
                $"Cannot capture '{channel.Name}': {ProcessLoopbackSupport.UnsupportedReason} No render endpoint is available to record instead.")
            : new WasapiCaptureSource(loopback);
    }

    private AudioChannel? DefaultLoopback()
    {
        IReadOnlyList<AudioChannel> candidates;

        try
        {
            candidates = _channels.GetChannels();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }

        AudioChannel? first = null;

        foreach (AudioChannel candidate in candidates)
        {
            if (candidate.Kind != AudioChannelKind.Loopback)
            {
                continue;
            }

            if (candidate.IsDefault)
            {
                return candidate;
            }

            // A machine can have render endpoints without a multimedia default (headless, or mid
            // device switch). Any loopback beats failing the session outright.
            first ??= candidate;
        }

        return first;
    }
}
