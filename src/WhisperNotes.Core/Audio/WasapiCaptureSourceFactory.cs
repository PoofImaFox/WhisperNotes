namespace WhisperNotes.Core.Audio;

/// <summary>
/// Creates WASAPI-backed capture sources. Stateless and safe to share; each call hands back a
/// fresh single-use source.
/// </summary>
public sealed class WasapiCaptureSourceFactory : IAudioCaptureSourceFactory
{
    public IAudioCaptureSource Create(AudioChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new WasapiCaptureSource(channel);
    }
}
