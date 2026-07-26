using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace WhisperNotes.App.Views.Controls;

/// <summary>
/// A stroked vector glyph at a fixed box size.
/// <para>
/// The app had two icon idioms. The nav rail draws <c>Path</c> geometry with a shared stroke
/// style; everywhere else drew a text character — U+21BB, U+27F3, U+2193, U+2715, U+2699 — none
/// of which is in Inter. Each fell back to a different system font with different ascent and
/// descent metrics, and <c>Button.icon</c> centres the content box rather than the glyph's
/// optical centre, so no two of them landed at the same height inside their own buttons. The
/// same action was even written with two different code points: refresh was U+21BB in the
/// toolbar and U+27F3 in the revision header.
/// </para>
/// <para>
/// The vector idiom is canonical. <c>Stretch="Uniform"</c> centres the geometry deterministically
/// at any <see cref="Size"/> regardless of what fonts are installed, and the stroke stays a
/// constant pixel width because Avalonia applies the pen after the stretch transform. Glyphs live
/// in <c>Views/Controls/Icons.axaml</c> on a 16x16 grid; add one there, never here.
/// </para>
/// </summary>
public sealed class Icon : TemplatedControl
{
    /// <summary>The glyph, e.g. <c>{DynamicResource IconRefresh}</c>.</summary>
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    /// <summary>Edge of the square box the glyph is fitted into. Theme default is 16.</summary>
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 16d);

    /// <summary>
    /// Line colour. A <c>Path</c> does not inherit <c>Foreground</c>, so the button variants that
    /// host icons set this — and their hover states — in <c>ComponentStyles.axaml</c>.
    /// </summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Icon, IBrush?>(nameof(Stroke));

    /// <summary>Line weight, in device pixels, independent of <see cref="Size"/>.</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.3d);

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }
}
