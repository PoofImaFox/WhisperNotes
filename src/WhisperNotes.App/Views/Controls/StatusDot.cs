using Avalonia;
using Avalonia.Controls.Primitives;

namespace WhisperNotes.App.Views.Controls;

/// <summary>
/// The six things a status dot is allowed to mean. Anything outside this list is a colour
/// somebody invented at a call site, which is how the app ended up with four dot sizes.
/// </summary>
public enum StatusKind
{
    /// <summary>Nothing to report — <c>TextMuted</c>.</summary>
    Muted,

    /// <summary>Ready, connected, on disk — <c>SuccessBase</c>.</summary>
    Ok,

    /// <summary>Degraded but usable, or unsaved — <c>WarningBase</c>.</summary>
    Warn,

    /// <summary>Recording, destructive, failed — <c>DangerBase</c>.</summary>
    Danger,

    /// <summary>Neutral activity — <c>InfoBase</c>.</summary>
    Info,

    /// <summary>Touched by the assistant — <c>AiAccent</c>.</summary>
    Ai,
}

/// <summary>
/// A coloured dot. Thirteen of these were written out longhand across seven views at four
/// different sizes (6, 7, 8 and 9px), and five of the thirteen were a <em>pair</em> of Ellipses
/// with opposite <c>IsVisible</c> bindings expressing "a dot whose colour depends on a bool" in
/// sixteen lines. <see cref="IsOn"/> plus <see cref="OffKind"/> is that bool, once.
/// <para>
/// A <c>TemplatedControl</c> rather than a UserControl: it is a leaf with no children of its own
/// and no DataContext of its own, and it needs typed properties a Style cannot introduce.
/// Alignment, Margin and ToolTip stay the caller's business — the theme only ever sets them as
/// defaults, so a local value at the call site always wins.
/// </para>
/// </summary>
public sealed class StatusDot : TemplatedControl
{
    /// <summary>Colour while <see cref="IsOn"/>.</summary>
    public static readonly StyledProperty<StatusKind> KindProperty =
        AvaloniaProperty.Register<StatusDot, StatusKind>(nameof(Kind), StatusKind.Muted);

    /// <summary>Colour while <see cref="IsOn"/> is false. Defaults to a grey "off" dot.</summary>
    public static readonly StyledProperty<StatusKind> OffKindProperty =
        AvaloniaProperty.Register<StatusDot, StatusKind>(nameof(OffKind), StatusKind.Muted);

    /// <summary>
    /// Which of the two kinds is showing. Bind a bool here instead of shipping two Ellipses with
    /// opposite visibilities — that idiom drifts by a pixel the moment one half is edited.
    /// </summary>
    public static readonly StyledProperty<bool> IsOnProperty =
        AvaloniaProperty.Register<StatusDot, bool>(nameof(IsOn), true);

    /// <summary>Diameter. The theme seeds this from the <c>DotSize</c> token.</summary>
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<StatusDot, double>(nameof(Size), 6d);

    /// <summary>Breathes at 1.3s. Reserved for "this is happening right now".</summary>
    public static readonly StyledProperty<bool> PulseProperty =
        AvaloniaProperty.Register<StatusDot, bool>(nameof(Pulse));

    public StatusKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public StatusKind OffKind
    {
        get => GetValue(OffKindProperty);
        set => SetValue(OffKindProperty, value);
    }

    public bool IsOn
    {
        get => GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public bool Pulse
    {
        get => GetValue(PulseProperty);
        set => SetValue(PulseProperty, value);
    }
}
