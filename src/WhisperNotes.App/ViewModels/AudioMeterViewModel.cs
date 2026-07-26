using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WhisperNotes.App.ViewModels;

/// <summary>One LED of the level meter.</summary>
public sealed partial class MeterSegmentViewModel(int index, int count) : ObservableObject
{
    // The strip maps -60..0 dBFS, so the colour bands are a share of it rather than a count of
    // segments: amber still lights at ~-13 dBFS and red at ~-6 whatever SegmentCount is. Stated
    // as counts ("the last two are red") they slide up and down the scale with the LED budget.
    private const double MidBand = 0.75;
    private const double HighBand = 0.9167;

    public bool IsHigh { get; } = (index + 1) / (double)count > HighBand;

    public bool IsMid { get; } = (index + 1) / (double)count is > MidBand and <= HighBand;

    /// <summary>Lit by the current level.</summary>
    [ObservableProperty] public partial bool IsLit { get; set; }

    /// <summary>The decaying peak marker is resting on this segment.</summary>
    [ObservableProperty] public partial bool IsPeak { get; set; }
}

/// <summary>
/// Ballistic level meter. Capture threads call <see cref="Report"/> at whatever rate the device
/// produces frames; a UI-thread timer does all the property churn at a fixed, cheap rate.
/// </summary>
public sealed partial class AudioMeterViewModel : ObservableObject, IDisposable
{
    // 16 LEDs at 3.75 dB apiece. It was 24, which made the strip 204px wide on a toolbar that
    // only has ~1000px to spend — see the width budget in CaptureBarView.axaml.
    private const int SegmentCount = 16;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(60);

    private readonly DispatcherTimer _timer;
    private float _rawPeak;
    private double _smoothed;
    private double _hold;
    private int _silentTicks = int.MaxValue;

    public AudioMeterViewModel()
    {
        Segments = [.. Enumerable.Range(0, SegmentCount).Select(i => new MeterSegmentViewModel(i, SegmentCount))];
        _timer = new DispatcherTimer(Tick, DispatcherPriority.Background, OnTick);
        _timer.Start();
    }

    public IReadOnlyList<MeterSegmentViewModel> Segments { get; }

    [ObservableProperty] public partial string LevelText { get; set; } = "no signal";

    /// <summary>True while audio is actually arriving above the noise floor.</summary>
    [ObservableProperty] public partial bool HasSignal { get; set; }

    /// <summary>True when a channel is being listened to but nothing has been heard for a while —
    /// the tell-tale of a wrongly chosen endpoint.</summary>
    [ObservableProperty] public partial bool IsSilent { get; set; }

    /// <summary>True while any tap (monitor or recording) is feeding the meter.</summary>
    [ObservableProperty] public partial bool IsActive { get; set; }

    /// <summary>Safe to call from a capture thread.</summary>
    public void Report(float peak)
    {
        // Max-hold between UI ticks. Benign race: worst case one frame's peak is dropped.
        if (peak > Volatile.Read(ref _rawPeak))
        {
            Volatile.Write(ref _rawPeak, peak);
        }
    }

    public void Reset()
    {
        Volatile.Write(ref _rawPeak, 0f);
        _smoothed = 0;
        _hold = 0;
        _silentTicks = int.MaxValue;
        HasSignal = false;
        IsSilent = false;
        LevelText = "no signal";

        foreach (var segment in Segments)
        {
            segment.IsLit = false;
            segment.IsPeak = false;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var raw = Volatile.Read(ref _rawPeak);
        Volatile.Write(ref _rawPeak, 0f);

        // Fast attack so a spoken word registers immediately, slow release so the bar stays readable.
        var target = ToDisplayScale(raw);
        _smoothed = target > _smoothed ? target : _smoothed * 0.72 + target * 0.28;
        _hold = Math.Max(target, _hold - 0.012);

        // The hold is drawn as a marker LED rather than as a number: the bar already says how
        // loud it is now, and what a level check needs is how loud it just was.
        var lit = (int)Math.Round(_smoothed * SegmentCount);
        var peak = (int)Math.Round(_hold * SegmentCount) - 1;
        for (var i = 0; i < Segments.Count; i++)
        {
            Segments[i].IsLit = i < lit;
            Segments[i].IsPeak = i == peak;
        }

        if (raw > 0.002f)
        {
            _silentTicks = 0;
        }
        else if (_silentTicks < int.MaxValue)
        {
            _silentTicks++;
        }

        HasSignal = IsActive && _silentTicks < 12;
        IsSilent = IsActive && _silentTicks > 50;

        // Kept to nine monospace characters or fewer: the readout is a fixed box, and the
        // longest of these ("-100 dBFS") is what sizes it.
        LevelText = !IsActive
            ? "idle"
            : raw <= 0.0001f && _smoothed < 0.01
                ? "silent"
                : string.Create(CultureInfo.CurrentCulture, $"{ToDecibels(Math.Max(raw, 1e-5f)):0} dBFS");
    }

    /// <summary>Maps linear amplitude onto a -60..0 dBFS bar, which is how audio meters are read.</summary>
    private static double ToDisplayScale(float peak)
    {
        if (peak <= 0.0005f)
        {
            return 0;
        }

        var db = ToDecibels(peak);
        return Math.Clamp((db + 60.0) / 60.0, 0, 1);
    }

    private static double ToDecibels(float amplitude) => 20.0 * Math.Log10(amplitude);

    public void Dispose() => _timer.Stop();
}
