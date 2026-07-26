using WhisperNotes.Core.Configuration;

namespace WhisperNotes.Core.Ai;

/// <summary>Which backend answers a request. Ollama is the default so the app stays offline-first.</summary>
public enum AiProviderKind
{
    /// <summary>Local models over the Ollama HTTP API. No data leaves the machine.</summary>
    Ollama,

    /// <summary>Anthropic's hosted API. Requires a key and sends note text off the machine.</summary>
    Anthropic,
}

/// <summary>One conversational turn. Role is "user" or "assistant".</summary>
public sealed record AiMessage(string Role, string Text)
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";

    public static AiMessage User(string text) => new(UserRole, text);

    public static AiMessage Assistant(string text) => new(AssistantRole, text);

    /// <summary>True when this turn came from the model rather than the user.</summary>
    public bool IsAssistant =>
        string.Equals(Role, AssistantRole, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A single completion request. The system prompt carries the action's instructions; the messages
/// carry the note text and any follow-up chat.
/// </summary>
public sealed record AiRequest(
    string SystemPrompt,
    IReadOnlyList<AiMessage> Messages,
    int MaxOutputTokens = 8000)
{
    /// <summary>Convenience for the common one-shot "here is the note, do the thing" call.</summary>
    public static AiRequest Single(string systemPrompt, string userText, int maxOutputTokens = 8000) =>
        new(systemPrompt, [AiMessage.User(userText)], maxOutputTokens);
}

/// <summary>The finished answer plus whatever accounting the provider reported.</summary>
public sealed record AiResult(
    string Text,
    string ModelUsed,
    int? InputTokens = null,
    int? OutputTokens = null);

/// <summary>
/// Thrown for provider faults the UI should surface verbatim (bad key, server down, model not
/// pulled). Never wrap a cancellation in this.
/// </summary>
public sealed class AiException : Exception
{
    public AiException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// A single model backend. Implementations are cheap value-like objects rebuilt whenever
/// <see cref="AiSettings"/> change, so they must not hold per-request state.
/// </summary>
public interface IAiAssistant
{
    AiProviderKind Provider { get; }

    string ModelId { get; }

    /// <summary>False when the provider cannot be reached/authenticated with current settings.</summary>
    bool IsConfigured { get; }

    /// <summary>One-line, user-facing reason when <see cref="IsConfigured"/> is false; null otherwise.</summary>
    string? ConfigurationHint { get; }

    /// <summary>Streams the answer in display-ready fragments. Fragments concatenate to the full text.</summary>
    IAsyncEnumerable<string> StreamAsync(AiRequest request, CancellationToken cancellationToken);

    Task<AiResult> CompleteAsync(AiRequest request, CancellationToken cancellationToken);

    /// <summary>Model ids available from the provider. Empty list on failure — must not throw.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken);
}

/// <summary>Builds an <see cref="IAiAssistant"/> for the current settings.</summary>
public interface IAiAssistantFactory
{
    /// <summary>Must be cheap and must not perform IO. Re-created whenever <see cref="AiSettings"/> change.</summary>
    IAiAssistant Create(AiSettings settings);
}
