using System.Runtime.CompilerServices;
using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Models.Models;
using WhisperNotes.Core.Configuration;

namespace WhisperNotes.Core.Ai;

/// <summary>
/// Anthropic-hosted models, via the official SDK.
/// </summary>
/// <remarks>
/// Three things about the current API drive the shape of this class and are easy to "fix" back into
/// HTTP 400s: sampling knobs (<c>temperature</c>, <c>top_p</c>, <c>top_k</c>) are gone on this model
/// family, <c>budget_tokens</c> is gone in favour of adaptive thinking plus
/// <see cref="OutputConfig.Effort"/>, and an assistant-turn prefill is rejected outright. Thinking is
/// on by default on Opus 5 and counts against <c>max_tokens</c>, which is why the default budget is
/// generous rather than tight.
/// </remarks>
public sealed class AnthropicAiAssistant : IAiAssistant
{
    internal const string MissingKeyHint =
        "Set an Anthropic API key in Settings, or set the ANTHROPIC_API_KEY environment variable.";

    private const string ApiKeyVariable = "ANTHROPIC_API_KEY";
    private const string RefusalStopReason = "refusal";
    private const string RefusalMessage = "The model declined this request.";

    private readonly AiSettings _settings;
    private readonly string? _apiKey;
    private readonly Lazy<AnthropicClient> _client;

    public AnthropicAiAssistant(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _apiKey = ResolveApiKey(settings);
        ModelId = string.IsNullOrWhiteSpace(settings.AnthropicModel)
            ? DefaultModel
            : settings.AnthropicModel.Trim();

        // Built on first use: constructing the graph must stay IO-free and allocation-cheap.
        _client = new Lazy<AnthropicClient>(CreateClient, isThreadSafe: true);
    }

    /// <summary>The model this app targets. Model ids are complete as written — never date-suffixed.</summary>
    public static string DefaultModel => "claude-opus-5";

    public AiProviderKind Provider => AiProviderKind.Anthropic;

    public string ModelId { get; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public string? ConfigurationHint => IsConfigured ? null : MissingKeyHint;

    public async Task<AiResult> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConfigured();

        MessageCreateParams parameters = BuildParameters(request);

        Message response;
        try
        {
            response = await _client.Value.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }

        // A refusal comes back as HTTP 200 with empty or partial content, so this has to happen
        // before anything touches Content.
        ThrowIfRefused(response.StopReason);

        var text = new StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.Value is TextBlock textBlock && !string.IsNullOrEmpty(textBlock.Text))
            {
                text.Append(textBlock.Text);
            }
        }

        var answer = text.ToString().Trim();
        if (answer.Length == 0)
        {
            throw new AiException(
                "Claude returned no text. It may have spent the whole token budget thinking — " +
                "raise the max output tokens in Settings and try again.");
        }

        return new AiResult(
            answer,
            DescribeModel(response),
            ToTokenCount(response.Usage?.InputTokens),
            ToTokenCount(response.Usage?.OutputTokens));
    }

    public async IAsyncEnumerable<string> StreamAsync(
        AiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConfigured();

        MessageCreateParams parameters = BuildParameters(request);

        IAsyncEnumerator<RawMessageStreamEvent> events;
        try
        {
            events = _client.Value.Messages
                .CreateStreaming(parameters, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }

        var produced = false;

        await using (events.ConfigureAwait(false))
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await events.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw Translate(ex);
                }

                if (!moved)
                {
                    break;
                }

                if (events.Current.TryPickContentBlockDelta(out var delta)
                    && delta.Delta.TryPickText(out var text)
                    && !string.IsNullOrEmpty(text.Text))
                {
                    produced = true;
                    yield return text.Text;
                }
            }
        }

        if (!produced)
        {
            // Empty stream is what a refusal or an exhausted thinking budget looks like from here.
            throw new AiException(
                RefusalMessage + " (No text was returned. If this was a long request, try raising " +
                "the max output tokens in Settings.)");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return [];
        }

        try
        {
            ModelListPage page = await _client.Value.Models
                .List(new ModelListParams(), cancellationToken)
                .ConfigureAwait(false);

            return [.. page.Items
                .Select(model => model.ID)
                .Where(id => !string.IsNullOrWhiteSpace(id))];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Contractually silent: the model picker falls back to whatever is typed in Settings.
            return [];
        }
    }

    private AnthropicClient CreateClient()
    {
        var seconds = _settings.TimeoutSeconds > 0 ? _settings.TimeoutSeconds : 300;

        return new AnthropicClient
        {
            ApiKey = _apiKey,
            Timeout = TimeSpan.FromSeconds(Math.Min(seconds, 3600)),
        };
    }

    private MessageCreateParams BuildParameters(AiRequest request)
    {
        var messages = BuildMessages(request.Messages);

        var maxTokens = request.MaxOutputTokens > 0 ? request.MaxOutputTokens : _settings.MaxOutputTokens;
        maxTokens = Math.Clamp(maxTokens <= 0 ? 8000 : maxTokens, 256, 200_000);

        MessageCreateParamsSystem? system = null;
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            system = new List<TextBlockParam> { new() { Text = request.SystemPrompt } };
        }

        // Deliberately no Temperature/TopP/TopK: they are removed on this model family and 400.
        return new MessageCreateParams
        {
            Model = ModelId,
            MaxTokens = maxTokens,
            // Adaptive thinking + effort replaces the removed budget_tokens knob.
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Effort = Effort.High },
            Messages = messages,
            System = system,
        };
    }

    private static List<MessageParam> BuildMessages(IReadOnlyList<AiMessage> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var turns = new List<(bool IsAssistant, string Text)>(source.Count);
        foreach (var message in source)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            var isAssistant = message.IsAssistant;

            // Consecutive same-role turns are merged rather than sent as-is.
            if (turns.Count > 0 && turns[^1].IsAssistant == isAssistant)
            {
                turns[^1] = (isAssistant, turns[^1].Text + "\n\n" + message.Text.Trim());
                continue;
            }

            turns.Add((isAssistant, message.Text.Trim()));
        }

        // The conversation must start on a user turn and must not end on an assistant one:
        // a trailing assistant entry is a prefill, which the API rejects with a 400.
        while (turns.Count > 0 && turns[0].IsAssistant)
        {
            turns.RemoveAt(0);
        }

        while (turns.Count > 0 && turns[^1].IsAssistant)
        {
            turns.RemoveAt(turns.Count - 1);
        }

        if (turns.Count == 0)
        {
            throw new AiException("There is nothing to send — the note or selection is empty.");
        }

        var result = new List<MessageParam>(turns.Count);
        foreach (var (isAssistant, text) in turns)
        {
            result.Add(new MessageParam
            {
                Role = isAssistant ? Role.Assistant : Role.User,
                Content = text,
            });
        }

        return result;
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new AiException(MissingKeyHint);
        }
    }

    private static void ThrowIfRefused(object? stopReason)
    {
        var value = stopReason?.ToString();
        if (string.Equals(value, RefusalStopReason, StringComparison.OrdinalIgnoreCase))
        {
            throw new AiException(RefusalMessage);
        }
    }

    private string DescribeModel(Message response)
    {
        var reported = response.Model.ToString();
        return string.IsNullOrWhiteSpace(reported) ? ModelId : reported;
    }

    private static string? ResolveApiKey(AiSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.AnthropicApiKey))
        {
            return settings.AnthropicApiKey.Trim();
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment.Trim();
    }

    private static int? ToTokenCount(long? value) =>
        value is null ? null : (int)Math.Clamp(value.Value, 0, int.MaxValue);

    /// <summary>Turns SDK faults into one-line, user-facing messages. Cancellation never gets here.</summary>
    private AiException Translate(Exception exception) => exception switch
    {
        AnthropicUnauthorizedException =>
            new AiException(
                "Anthropic rejected the API key (401). Check the key in Settings or the " +
                "ANTHROPIC_API_KEY environment variable.",
                exception),

        AnthropicForbiddenException =>
            new AiException(
                "Anthropic refused the request (403). This key may not have access to " + ModelId + ".",
                exception),

        AnthropicRateLimitException =>
            new AiException(
                "Anthropic rate limited the request (429). Wait a few seconds and try again.",
                exception),

        AnthropicNotFoundException =>
            new AiException(
                $"Anthropic does not recognise the model \"{ModelId}\" (404). Check the model id in Settings.",
                exception),

        AnthropicBadRequestException =>
            new AiException($"Anthropic rejected the request (400): {exception.Message}", exception),

        Anthropic5xxException =>
            new AiException(
                "The Anthropic API is having trouble (server error). Try again in a moment.",
                exception),

        TaskCanceledException or TimeoutException =>
            new AiException(
                $"The Anthropic request timed out after {_settings.TimeoutSeconds}s. " +
                "Raise the timeout in Settings, or send less text.",
                exception),

        HttpRequestException =>
            new AiException(
                "Could not reach the Anthropic API. Check your network connection.",
                exception),

        AnthropicException =>
            new AiException($"Anthropic request failed: {exception.Message}", exception),

        _ => new AiException($"Anthropic request failed: {exception.Message}", exception),
    };
}
