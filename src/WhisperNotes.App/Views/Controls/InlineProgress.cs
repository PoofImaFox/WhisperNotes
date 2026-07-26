using Avalonia;
using Avalonia.Controls.Primitives;

namespace WhisperNotes.App.Views.Controls;

/// <summary>
/// A thin bar and a caption: the app's one way of showing a background operation in flight.
/// <para>
/// Two sites render this — the model download in the capture bar and the video import in the
/// status bar — with bars of 36px and 88px respectively, for no reason anyone recorded. One
/// default, overridable per site via <see cref="BarWidth"/>.
/// </para>
/// <para>
/// The <c>MinWidth</c>-as-well-as-<c>Width</c> trick is the point of extracting this. Fluent's
/// ProgressBar theme carries a MinWidth of its own and MinWidth beats Width in Avalonia's measure
/// clamp, so setting Width alone lets the bar swell to the theme's width and swallow the caption
/// column whole. That was learned once, in a comment, in one view; now it is in one place.
/// </para>
/// </summary>
public sealed class InlineProgress : TemplatedControl
{
    /// <summary>Completed share of the work, 0..1.</summary>
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<InlineProgress, double>(nameof(Fraction));

    /// <summary>True while the total is unknown — the bar sweeps instead of filling.</summary>
    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<InlineProgress, bool>(nameof(IsIndeterminate));

    /// <summary>Caption beside the bar. Also becomes its own tooltip, since it can outgrow the row.</summary>
    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<InlineProgress, string?>(nameof(StatusText));

    /// <summary>Track width. Fixed, so a growing byte counter cannot widen the bar.</summary>
    public static readonly StyledProperty<double> BarWidthProperty =
        AvaloniaProperty.Register<InlineProgress, double>(nameof(BarWidth), 88d);

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public double BarWidth
    {
        get => GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }
}
