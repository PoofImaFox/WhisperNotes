using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NoteScribe.App.ViewModels;

/// <summary>One LED of the level meter.</summary>
public sealed partial class MeterSegmentViewModel(int index, int count) : ObservableObject
{
    public bool IsHigh { get; } = index >= count - 2;

    public bool IsMid { get; } = index >= count - 6 && index < count - 2;

    public bool IsLow { get; } = index < count - 6;

    [ObservableProperty] public partial bool IsLit { get; set; }
}

/// <summary>
/// Ballistic level meter. Capture threads call <see cref="Report"/> at whatever rate the device
/// produces frames; a UI-thread timer does all the property churn at a fixed, cheap rate.
/// </summary>
public sealed partial class AudioMeterViewModel : ObservableObject, IDisposable
{
    private const int SegmentCount = 24;
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

    /// <summary>Smoothed level, 0..1.</summary>
    [ObservableProperty] public partial double Level { get; set; }

    /// <summary>Slowly decaying peak marker, 0..1.</summary>
    [ObservableProperty] public partial double PeakHold { get; set; }

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
        Level = 0;
        PeakHold = 0;
        HasSignal = false;
        IsSilent = false;
        LevelText = "no signal";

        foreach (var segment in Segments)
        {
            segment.IsLit = false;
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

        Level = _smoothed;
        PeakHold = _hold;

        var lit = (int)Math.Round(_smoothed * SegmentCount);
        for (var i = 0; i < Segments.Count; i++)
        {
            Segments[i].IsLit = i < lit;
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

        LevelText = !IsActive
            ? "not listening"
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
