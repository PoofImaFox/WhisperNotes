using Avalonia.Controls;

namespace WhisperNotes.App.Views.Controls;

/// <summary>
/// LED level meter with a peak-hold marker and a fixed-width readout. Bind its
/// <see cref="Control.DataContext"/> to an <see cref="ViewModels.AudioMeterViewModel"/>; the
/// control adds no state of its own, so one binding path crosses the boundary.
/// </summary>
public partial class LevelMeter : UserControl
{
    public LevelMeter() => InitializeComponent();
}
