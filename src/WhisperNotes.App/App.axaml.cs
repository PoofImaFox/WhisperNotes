using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WhisperNotes.App.Composition;
using WhisperNotes.App.ViewModels;
using WhisperNotes.App.Views;

namespace WhisperNotes.App;

public partial class App : Application
{
    private MainWindowViewModel? _viewModel;
    private AppServices? _services;
    private bool _shutdownComplete;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppServices services = AppServices.CreateDefault();
            _services = services;

            var viewModel = new MainWindowViewModel(services);
            _viewModel = viewModel;

            var window = new MainWindow { DataContext = viewModel };
            window.Closing += OnMainWindowClosing;
            desktop.MainWindow = window;

            // Devices, settings and the notes tree all involve I/O; none of it belongs in the ctor.
            Dispatcher.UIThread.Post(() => _ = viewModel.InitializeAsync(), DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Closing mid-meeting must finalise the session rather than abandon it, so the first close
    /// request is deferred until the shutdown work has run.
    /// </summary>
    private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownComplete || _viewModel is null)
        {
            return;
        }

        e.Cancel = true;

        try
        {
            await _viewModel.ShutdownAsync();
            await _viewModel.DisposeAsync();

            // Released after the view model has finalised the session — disposing Core first would
            // close the transcript handle out from under the final write.
            if (_services?.Core is { } core)
            {
                await core.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Shutdown failed: {ex}");
        }

        _shutdownComplete = true;

        if (sender is Window window)
        {
            window.Close();
        }
    }
}
