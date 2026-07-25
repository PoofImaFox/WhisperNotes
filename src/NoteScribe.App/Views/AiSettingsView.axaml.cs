using Avalonia.Controls;

namespace NoteScribe.App.Views;

/// <summary>
/// Inline assistant configuration. Bound to <see cref="ViewModels.AiSettingsViewModel"/>, which
/// rebuilds the assistant on every edit and persists behind a debounce.
/// </summary>
public partial class AiSettingsView : UserControl
{
    public AiSettingsView()
    {
        InitializeComponent();
    }
}
