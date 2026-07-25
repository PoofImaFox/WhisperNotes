using NoteScribe.Core.Configuration;

namespace NoteScribe.Core.Ai;

/// <summary>
/// Picks the provider named in settings. Deliberately trivial and IO-free: the UI rebuilds an
/// assistant on every settings change, including on every keystroke in the endpoint box.
/// </summary>
public sealed class AiAssistantFactory : IAiAssistantFactory
{
    public IAiAssistant Create(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.Provider switch
        {
            AiProviderKind.Anthropic => new AnthropicAiAssistant(settings),
            _ => new OllamaAiAssistant(settings),
        };
    }
}
