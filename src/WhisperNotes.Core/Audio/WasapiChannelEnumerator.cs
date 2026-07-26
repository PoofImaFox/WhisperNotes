using NAudio.CoreAudioApi;

namespace WhisperNotes.Core.Audio;

/// <summary>
/// Lists the machine's active WASAPI endpoints, plus the applications currently playing audio.
/// Render endpoints are offered as loopback taps (that is how Teams and every other app's output
/// gets captured); capture endpoints are offered as microphones; and each application holding a
/// render session is offered on its own, so a transcript can exclude everything else on the box.
/// </summary>
public sealed class WasapiChannelEnumerator : IAudioChannelEnumerator
{
    private const string LoopbackSuffix = " — system audio";
    private const string DefaultSuffix = " (default)";

    /// <summary>Render endpoints pick the multimedia default; Windows uses it for app playback.</summary>
    private const Role LoopbackRole = Role.Multimedia;

    /// <summary>Capture endpoints pick the communications default; that is the headset users talk into.</summary>
    private const Role MicrophoneRole = Role.Communications;

    /// <summary>
    /// Endpoints first (loopback, then microphones, defaults first within each kind), applications
    /// last.
    /// </summary>
    /// <remarks>
    /// Applications trail the endpoints because they are the volatile part of the list: they come and
    /// go with whatever the user has open, and a stable prefix keeps the picker from reshuffling the
    /// rows that never change.
    /// </remarks>
    public IReadOnlyList<AudioChannel> GetChannels()
    {
        using var devices = new MMDeviceEnumerator();

        var channels = new List<AudioChannel>();
        channels.AddRange(Collect(devices, DataFlow.Render, AudioChannelKind.Loopback, LoopbackRole));
        channels.AddRange(Collect(devices, DataFlow.Capture, AudioChannelKind.Microphone, MicrophoneRole));
        channels.AddRange(CollectApplications(devices));
        return channels;
    }

    public AudioChannel? Find(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return null;
        }

        return ApplicationChannelId.IsApplicationId(channelId)
            ? FindApplication(channelId)
            : FindDevice(channelId);
    }

    private static AudioChannel? FindDevice(string channelId)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDevice(channelId);

            if (device.State != DeviceState.Active)
            {
                return null;
            }

            AudioChannelKind kind = device.DataFlow == DataFlow.Capture
                ? AudioChannelKind.Microphone
                : AudioChannelKind.Loopback;
            Role role = kind == AudioChannelKind.Loopback ? LoopbackRole : MicrophoneRole;
            string? defaultId = DefaultEndpointId(devices, device.DataFlow, role);

            return Describe(device, kind, IsSameEndpoint(device.ID, defaultId));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A persisted id can outlive its device: unplugged headset, uninstalled driver,
            // disabled endpoint. The contract is "return null", not "throw at the caller".
            return null;
        }
    }

    /// <summary>
    /// Resolves an <c>app:teams.exe</c> id back to a live channel, or null when the application is no
    /// longer playing audio.
    /// </summary>
    /// <remarks>
    /// A closed application is treated exactly like an unplugged headset — null, not an exception — so
    /// the caller's existing "the input you saved is missing" path handles both without knowing the
    /// difference.
    /// </remarks>
    private static AudioChannel? FindApplication(string channelId)
    {
        string? executable = ApplicationChannelId.ExecutableOf(channelId);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        try
        {
            AudioSessionApp? app = AudioSessionCatalog.GetApplications()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.ExecutableName, executable, StringComparison.OrdinalIgnoreCase));

            if (app is null)
            {
                return null;
            }

            using var devices = new MMDeviceEnumerator();
            (int sampleRate, int channels) = DefaultRenderMixFormat(devices);
            return Describe(app, sampleRate, channels);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static List<AudioChannel> CollectApplications(MMDeviceEnumerator devices)
    {
        // Applications have no endpoint of their own, so there is no per-app mix format to report.
        // The process-loopback stream is materialised against the default render endpoint, so that
        // endpoint's format is the honest answer for "what will this sound like".
        (int sampleRate, int channels) = DefaultRenderMixFormat(devices);

        return [.. AudioSessionCatalog.GetApplications().Select(app => Describe(app, sampleRate, channels))];
    }

    private static AudioChannel Describe(AudioSessionApp app, int sampleRate, int channels) =>
        new(
            ApplicationChannelId.ForExecutable(app.ExecutableName),
            app.DisplayName,
            AudioChannelKind.Application,
            IsDefault: false,
            sampleRate,
            channels,
            app.ProcessId,
            app.ExecutableName);

    private static (int SampleRate, int Channels) DefaultRenderMixFormat(MMDeviceEnumerator devices)
    {
        try
        {
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, LoopbackRole))
            {
                return (0, 0);
            }

            using MMDevice device = devices.GetDefaultAudioEndpoint(DataFlow.Render, LoopbackRole);
            return MixFormat(device);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Informational only — a machine with no render endpoint still gets a usable app list.
            return (0, 0);
        }
    }

    private static List<AudioChannel> Collect(
        MMDeviceEnumerator devices,
        DataFlow flow,
        AudioChannelKind kind,
        Role role)
    {
        string? defaultId = DefaultEndpointId(devices, flow, role);
        var found = new List<AudioChannel>();

        MMDeviceCollection endpoints;
        try
        {
            endpoints = devices.EnumerateAudioEndPoints(flow, DeviceState.Active);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return found;
        }

        foreach (MMDevice device in endpoints)
        {
            try
            {
                found.Add(Describe(device, kind, IsSameEndpoint(device.ID, defaultId)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One endpoint with a broken driver must not cost the user the whole picker.
            }
            finally
            {
                device.Dispose();
            }
        }

        found.Sort(static (left, right) =>
            left.IsDefault != right.IsDefault
                ? (left.IsDefault ? -1 : 1)
                : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));

        return found;
    }

    private static AudioChannel Describe(MMDevice device, AudioChannelKind kind, bool isDefault)
    {
        string id = device.ID;
        string name = FriendlyName(device, id);

        if (kind == AudioChannelKind.Loopback)
        {
            name += LoopbackSuffix;
        }

        if (isDefault)
        {
            name += DefaultSuffix;
        }

        (int sampleRate, int channels) = MixFormat(device);
        return new AudioChannel(id, name, kind, isDefault, sampleRate, channels);
    }

    private static string FriendlyName(MMDevice device, string fallback)
    {
        try
        {
            string name = device.FriendlyName;
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return fallback;
        }
    }

    private static (int SampleRate, int Channels) MixFormat(MMDevice device)
    {
        try
        {
            // Opening the audio client is the only way to learn the endpoint's mix format, and it
            // is the part most likely to fail on a half-installed driver — hence its own guard.
            using AudioClient client = device.AudioClient;
            var format = client.MixFormat;
            return (format.SampleRate, format.Channels);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (0, 0);
        }
    }

    private static string? DefaultEndpointId(MMDeviceEnumerator devices, DataFlow flow, Role role)
    {
        try
        {
            if (!devices.HasDefaultAudioEndpoint(flow, role))
            {
                return null;
            }

            using MMDevice device = devices.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static bool IsSameEndpoint(string id, string? other) =>
        other is not null && string.Equals(id, other, StringComparison.OrdinalIgnoreCase);
}
