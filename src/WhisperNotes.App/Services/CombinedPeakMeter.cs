namespace WhisperNotes.App.Services;

/// <summary>
/// Folds several concurrent taps into the single level the toolbar meter shows: the loudest
/// enabled input wins.
/// </summary>
/// <remarks>
/// Every tap gets its own slot instead of reporting straight to the meter. Endpoints deliver
/// frames on their own clocks, so funnelling them all into one max-hold lets a chatty silent
/// input erase the level a slower, louder one had just published. Slots expire too: an input that
/// stops delivering entirely — device yanked, stream stalled — must not pin the meter to whatever
/// it was last doing.
/// </remarks>
internal sealed class CombinedPeakMeter(int sourceCount, Action<float> onPeak)
{
    /// <summary>Comfortably longer than any endpoint's buffer period, shorter than a held note.</summary>
    private const long SlotLifetimeMs = 400;

    private readonly float[] _peaks = new float[sourceCount];
    private readonly long[] _stamps = new long[sourceCount];

    /// <summary>The callback for one tap. Safe to call from a capture thread.</summary>
    public Action<float> SinkFor(int index) => peak => Report(index, peak);

    private void Report(int index, float peak)
    {
        Volatile.Write(ref _peaks[index], peak);
        Volatile.Write(ref _stamps[index], Environment.TickCount64);
        onPeak(Combine());
    }

    private float Combine()
    {
        var cutoff = Environment.TickCount64 - SlotLifetimeMs;
        var combined = 0f;

        for (var i = 0; i < _peaks.Length; i++)
        {
            // A slot that has never reported carries stamp 0, so it is stale by construction.
            if (Volatile.Read(ref _stamps[i]) <= cutoff)
            {
                continue;
            }

            var peak = Volatile.Read(ref _peaks[i]);
            if (peak > combined)
            {
                combined = peak;
            }
        }

        return combined;
    }
}
