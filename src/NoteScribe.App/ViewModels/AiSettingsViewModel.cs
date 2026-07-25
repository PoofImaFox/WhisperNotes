using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoteScribe.Core.Ai;
using NoteScribe.Core.Configuration;

namespace NoteScribe.App.ViewModels;

/// <summary>
/// The inline assistant-configuration panel: provider, endpoint, model, key and token budget.
/// </summary>
/// <remarks>
/// <para>
/// Every edit rebuilds the <see cref="IAiAssistant"/> immediately (the factory contract promises the
/// call is cheap and does no IO) and queues a debounced write to <see cref="ISettingsStore"/>.
/// </para>
/// <para>
/// The API key is held here and written to settings; it is never put into a notification, a status
/// string, a tooltip or a log line. <see cref="ProviderSummary"/> deliberately reports only whether
/// a key is present.
/// </para>
/// <para>
/// Saving re-reads settings from disk first and patches only <see cref="AppSettings.Ai"/>, because
/// the shell owns its own <see cref="AppSettings"/> instance and writes the same file; a blind
/// write-back of a stale snapshot would silently revert the user's channel or model choice.
/// </para>
/// </remarks>
public sealed partial class AiSettingsViewModel : ObservableObject
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(700);

    private readonly IAiAssistantFactory _factory;
    private readonly ISettingsStore _settings;
    private readonly Action<NotificationSeverity, string, string> _notify;

    private AiSettings _model = new();
    private CancellationTokenSource? _saveDebounce;
    private bool _loading;

    public AiSettingsViewModel(
        IAiAssistantFactory factory,
        ISettingsStore settings,
        Action<NotificationSeverity, string, string> notify)
    {
        _factory = factory;
        _settings = settings;
        _notify = notify;
        Assistant = factory.Create(_model);
        Apply(_model, rebuild: false);
    }

    /// <summary>Raised whenever the assistant is rebuilt, so open editors pick up the new backend.</summary>
    public event EventHandler<IAiAssistant>? AssistantChanged;

    public IAiAssistant Assistant { get; private set; }

    public ObservableCollection<string> AvailableModels { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnthropic))]
    public partial bool IsOllama { get; set; } = true;

    [ObservableProperty] public partial string OllamaEndpoint { get; set; } = "http://localhost:11434";

    [ObservableProperty] public partial string OllamaModel { get; set; } = "llama3.1";

    [ObservableProperty] public partial string AnthropicModel { get; set; } = "claude-opus-5";

    [ObservableProperty] public partial string AnthropicApiKey { get; set; } = string.Empty;

    [ObservableProperty] public partial string MaxOutputTokensText { get; set; } = "8000";

    [ObservableProperty] public partial bool IsRefreshingModels { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelsHint))]
    public partial string? ModelsHint { get; set; }

    /// <summary>
    /// Picked out of the discovered list. Kept separate from the model text boxes so that a refresh
    /// that finds nothing matching cannot blank out what the user typed.
    /// </summary>
    [ObservableProperty] public partial string? SelectedDiscoveredModel { get; set; }

    public bool HasModels => AvailableModels.Count > 0;

    public bool HasModelsHint => !string.IsNullOrWhiteSpace(ModelsHint);

    public bool IsAnthropic
    {
        get => !IsOllama;
        set => IsOllama = !value;
    }

    public bool IsConfigured => Assistant.IsConfigured;

    public string? ConfigurationHint => Assistant.ConfigurationHint;

    public bool HasConfigurationHint => !IsConfigured && !string.IsNullOrWhiteSpace(ConfigurationHint);

    /// <summary>Status line for the assistant header. Never contains the key itself.</summary>
    public string ProviderSummary => IsOllama
        ? $"Ollama · {Trimmed(OllamaModel, "llama3.1")} · {Trimmed(OllamaEndpoint, "localhost:11434")}"
        : $"Anthropic · {Trimmed(AnthropicModel, "claude-opus-5")} · {(HasKey ? "key set" : "no key")}";

    public bool HasKey =>
        !string.IsNullOrWhiteSpace(AnthropicApiKey) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    /// <summary>Adopts persisted settings without treating the load as a user edit.</summary>
    public void Load(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _loading = true;
        try
        {
            IsOllama = settings.Provider == AiProviderKind.Ollama;
            OllamaEndpoint = settings.OllamaEndpoint;
            OllamaModel = settings.OllamaModel;
            AnthropicModel = settings.AnthropicModel;
            AnthropicApiKey = settings.AnthropicApiKey ?? string.Empty;
            MaxOutputTokensText = settings.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
            _model = Clone(settings);
        }
        finally
        {
            _loading = false;
        }

        Apply(_model, rebuild: true, persist: false);
    }

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        IsRefreshingModels = true;
        ModelsHint = null;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await Assistant.ListModelsAsync(timeout.Token).ConfigureAwait(true);

            AvailableModels.Clear();
            foreach (var model in models)
            {
                AvailableModels.Add(model);
            }

            OnPropertyChanged(nameof(HasModels));

            ModelsHint = AvailableModels.Count == 0
                ? "No models reported. Is the provider running and reachable?"
                : string.Create(CultureInfo.CurrentCulture, $"{AvailableModels.Count} model(s) available.");
        }
        catch (OperationCanceledException)
        {
            ModelsHint = "Timed out asking the provider for its model list.";
        }
        catch (Exception ex)
        {
            // ListModelsAsync promises not to throw, but a provider is still a network call.
            ModelsHint = ex.Message;
        }
        finally
        {
            IsRefreshingModels = false;
        }
    }

    partial void OnSelectedDiscoveredModelChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (IsOllama)
        {
            OllamaModel = value;
        }
        else
        {
            AnthropicModel = value;
        }
    }

    partial void OnIsOllamaChanged(bool value)
    {
        AvailableModels.Clear();
        SelectedDiscoveredModel = null;
        ModelsHint = null;
        OnPropertyChanged(nameof(HasModels));
        Push();
    }

    partial void OnOllamaEndpointChanged(string value) => Push();

    partial void OnOllamaModelChanged(string value) => Push();

    partial void OnAnthropicModelChanged(string value) => Push();

    partial void OnAnthropicApiKeyChanged(string value) => Push();

    partial void OnMaxOutputTokensTextChanged(string value) => Push();

    /// <summary>Folds the editable fields back into an <see cref="AiSettings"/> and republishes.</summary>
    private void Push()
    {
        if (_loading)
        {
            return;
        }

        var tokens = int.TryParse(MaxOutputTokensText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 256, 200_000)
            : _model.MaxOutputTokens;

        var updated = new AiSettings
        {
            Provider = IsOllama ? AiProviderKind.Ollama : AiProviderKind.Anthropic,
            OllamaEndpoint = string.IsNullOrWhiteSpace(OllamaEndpoint) ? "http://localhost:11434" : OllamaEndpoint.Trim(),
            OllamaModel = string.IsNullOrWhiteSpace(OllamaModel) ? "llama3.1" : OllamaModel.Trim(),
            AnthropicModel = string.IsNullOrWhiteSpace(AnthropicModel) ? "claude-opus-5" : AnthropicModel.Trim(),
            AnthropicApiKey = string.IsNullOrWhiteSpace(AnthropicApiKey) ? null : AnthropicApiKey.Trim(),
            MaxOutputTokens = tokens,
            TimeoutSeconds = _model.TimeoutSeconds,
        };

        Apply(updated, rebuild: true);
    }

    private void Apply(AiSettings settings, bool rebuild, bool persist = true)
    {
        _model = settings;

        if (rebuild)
        {
            try
            {
                Assistant = _factory.Create(settings);
            }
            catch (Exception ex)
            {
                // Create() is contractually IO-free, so this is a configuration fault, not an outage.
                _notify(NotificationSeverity.Warning, "Assistant unavailable", ex.Message);
            }

            AssistantChanged?.Invoke(this, Assistant);
        }

        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(ConfigurationHint));
        OnPropertyChanged(nameof(HasConfigurationHint));
        OnPropertyChanged(nameof(ProviderSummary));
        OnPropertyChanged(nameof(HasKey));

        if (persist)
        {
            QueueSave();
        }
    }

    private void QueueSave()
    {
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();

        var cts = new CancellationTokenSource();
        _saveDebounce = cts;
        _ = SaveAfterDelayAsync(Clone(_model), cts.Token);
    }

    private async Task SaveAfterDelayAsync(AiSettings snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SaveDebounce, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveAsync(snapshot, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Writes the pending change immediately. Called on shutdown.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        _saveDebounce?.Cancel();
        await SaveAsync(Clone(_model), cancellationToken).ConfigureAwait(true);
    }

    private async Task SaveAsync(AiSettings snapshot, CancellationToken cancellationToken)
    {
        try
        {
            // Re-read so a concurrent shell write of the same file is not clobbered.
            var current = _settings.Load();
            current.Ai = snapshot;
            await _settings.SaveAsync(current, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Warning, "Could not save assistant settings", ex.Message);
        }
    }

    private static AiSettings Clone(AiSettings source) => new()
    {
        Provider = source.Provider,
        OllamaEndpoint = source.OllamaEndpoint,
        OllamaModel = source.OllamaModel,
        AnthropicModel = source.AnthropicModel,
        AnthropicApiKey = source.AnthropicApiKey,
        MaxOutputTokens = source.MaxOutputTokens,
        TimeoutSeconds = source.TimeoutSeconds,
    };

    private static string Trimmed(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>The token budget the editor should ask for.</summary>
    public int MaxOutputTokens => _model.MaxOutputTokens;
}
