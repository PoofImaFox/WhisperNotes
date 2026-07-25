using NAudio.CoreAudioApi;

namespace NoteScribe.Core.Audio;

/// <summary>
/// Lists the machine's active WASAPI endpoints. Render endpoints are offered as loopback taps
/// (that is how Teams and every other app's output gets captured); capture endpoints are offered
/// as microphones.
/// </summary>
public sealed class WasapiChannelEnumerator : IAudioChannelEnumerator
{
    private const string LoopbackSuffix = " — system audio";
    private const string DefaultSuffix = " (default)";

    /// <summary>Render endpoints pick the multimedia default; Windows uses it for app playback.</summary>
    private const Role LoopbackRole = Role.Multimedia;

    /// <summary>Capture endpoints pick the communications default; that is the headset users talk into.</summary>
    private const Role MicrophoneRole = Role.Communications;

    public IReadOnlyList<AudioChannel> GetChannels()
    {
        using var devices = new MMDeviceEnumerator();

        var channels = new List<AudioChannel>();
        channels.AddRange(Collect(devices, DataFlow.Render, AudioChannelKind.Loopback, LoopbackRole));
        channels.AddRange(Collect(devices, DataFlow.Capture, AudioChannelKind.Microphone, MicrophoneRole));
        return channels;
    }

    public AudioChannel? Find(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return null;
        }

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
