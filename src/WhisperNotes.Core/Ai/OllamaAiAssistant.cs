using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperNotes.Core.Configuration;

namespace WhisperNotes.Core.Ai;

/// <summary>
/// Local models over the Ollama HTTP API. This is the default provider: it keeps the offline
/// promise, because nothing leaves the machine.
/// </summary>
/// <remarks>
/// There is no SDK and none is wanted — the surface we need is two endpoints. The only subtlety is
/// that <c>/api/chat</c> streams newline-delimited JSON objects rather than one document, so the
/// response body is read a line at a time off the network stream.
/// </remarks>
public sealed class OllamaAiAssistant : IAiAssistant
{
    public const string DefaultEndpoint = "http://localhost:11434";

    private const string ChatPath = "/api/chat";
    private const string TagsPath = "/api/tags";

    /// <summary>
    /// One handler for the whole process. A new assistant is built every time settings change, and
    /// a fresh <see cref="HttpClient"/> per instance would burn a socket per edit; sharing the
    /// handler keeps the connection pool intact while still allowing a per-instance timeout.
    /// </summary>
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AiSettings _settings;
    private readonly Uri? _baseUri;
    private readonly Lazy<HttpClient> _http;

    public OllamaAiAssistant(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        Endpoint = Normalise(settings.OllamaEndpoint);
        _baseUri = TryParse(Endpoint);
        ModelId = string.IsNullOrWhiteSpace(settings.OllamaModel) ? "llama3.1" : settings.OllamaModel.Trim();

        _http = new Lazy<HttpClient>(CreateClient, isThreadSafe: true);
    }

    /// <summary>The normalised base address, without a trailing slash.</summary>
    public string Endpoint { get; }

    public AiProviderKind Provider => AiProviderKind.Ollama;

    public string ModelId { get; }

    public bool IsConfigured => _baseUri is not null;

    public string? ConfigurationHint => IsConfigured
        ? null
        : $"\"{Endpoint}\" is not a valid Ollama endpoint. Use something like {DefaultEndpoint}.";

    /// <summary>What to tell the user when the server does not answer.</summary>
    public string UnreachableHint =>
        $"Could not reach Ollama at {Endpoint}. Is `ollama serve` running? " +
        $"If it is, check the model with `ollama pull {ModelId}`.";

    public async Task<AiResult> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConfigured();

        using HttpResponseMessage response = await SendChatAsync(request, stream: false, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ChatResponse? payload = Deserialize(body);

        if (!string.IsNullOrWhiteSpace(payload?.Error))
        {
            throw new AiException(DescribeServerError(payload!.Error!));
        }

        var text = payload?.Message?.Content?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new AiException($"Ollama returned an empty response from {ModelId}.");
        }

        return new AiResult(
            text,
            string.IsNullOrWhiteSpace(payload?.Model) ? ModelId : payload!.Model!,
            payload?.PromptEvalCount,
            payload?.EvalCount);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        AiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConfigured();

        using HttpResponseMessage response = await SendChatAsync(request, stream: true, cancellationToken)
            .ConfigureAwait(false);

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(body, Encoding.UTF8);

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Translate(ex);
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            // Newline-delimited JSON: one complete object per line, never one document.
            ChatResponse? chunk = Deserialize(line);
            if (chunk is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(chunk.Error))
            {
                throw new AiException(DescribeServerError(chunk.Error!));
            }

            var fragment = chunk.Message?.Content;
            if (!string.IsNullOrEmpty(fragment))
            {
                yield return fragment;
            }

            if (chunk.Done)
            {
                break;
            }
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
            using HttpResponseMessage response = await _http.Value
                .GetAsync(new Uri(_baseUri!, TagsPath), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize<TagsResponse>(body, JsonOptions);

            if (payload?.Models is null)
            {
                return [];
            }

            return [.. payload.Models
                .Select(model => model.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Contractually silent: a stopped server just means an empty model picker.
            return [];
        }
    }

    private async Task<HttpResponseMessage> SendChatAsync(
        AiRequest request,
        bool stream,
        CancellationToken cancellationToken)
    {
        var payload = new ChatRequest
        {
            Model = ModelId,
            Stream = stream,
            Messages = BuildMessages(request),
            Options = new ChatOptions { NumPredict = ResolveMaxTokens(request) },
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri!, ChatPath))
        {
            Content = content,
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.Value
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
        response.Dispose();

        throw new AiException(DescribeHttpError(status, body));
    }

    private List<ChatMessage> BuildMessages(AiRequest request)
    {
        var messages = new List<ChatMessage>(request.Messages.Count + 1);

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new ChatMessage { Role = "system", Content = request.SystemPrompt });
        }

        foreach (AiMessage message in request.Messages)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            messages.Add(new ChatMessage
            {
                Role = message.IsAssistant ? "assistant" : "user",
                Content = message.Text,
            });
        }

        if (messages.Count == 0 || messages.All(m => m.Role == "system"))
        {
            throw new AiException("There is nothing to send — the note or selection is empty.");
        }

        return messages;
    }

    private int ResolveMaxTokens(AiRequest request)
    {
        var tokens = request.MaxOutputTokens > 0 ? request.MaxOutputTokens : _settings.MaxOutputTokens;
        return tokens > 0 ? tokens : 8000;
    }

    private HttpClient CreateClient()
    {
        var seconds = _settings.TimeoutSeconds > 0 ? _settings.TimeoutSeconds : 300;

        return new HttpClient(SharedHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(Math.Min(seconds, 3600)),
        };
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new AiException(ConfigurationHint!);
        }
    }

    private string DescribeHttpError(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.NotFound)
        {
            return $"Ollama does not have the model \"{ModelId}\". Run `ollama pull {ModelId}` and try again.";
        }

        var detail = ExtractError(body);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            return DescribeServerError(detail);
        }

        return $"Ollama at {Endpoint} returned {(int)status} {status}.";
    }

    private string DescribeServerError(string error)
    {
        // The "pull it first" text is the single most common failure and deserves the exact command.
        if (error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("try pulling", StringComparison.OrdinalIgnoreCase))
        {
            return $"Ollama does not have the model \"{ModelId}\". Run `ollama pull {ModelId}` and try again.";
        }

        return $"Ollama reported an error: {error.Trim()}";
    }

    private AiException Translate(Exception exception) => exception switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } =>
            new AiException(UnreachableHint, exception),

        HttpRequestException { InnerException: SocketException } =>
            new AiException(UnreachableHint, exception),

        HttpRequestException =>
            new AiException(UnreachableHint, exception),

        SocketException => new AiException(UnreachableHint, exception),

        TaskCanceledException or TimeoutException =>
            new AiException(
                $"Ollama did not respond within {_settings.TimeoutSeconds}s. Local models can be slow " +
                "to start — raise the timeout in Settings, or try a smaller model.",
                exception),

        IOException => new AiException($"The connection to Ollama at {Endpoint} was interrupted.", exception),

        JsonException => new AiException($"Ollama at {Endpoint} returned a response this app could not read.", exception),

        _ => new AiException($"Ollama request failed: {exception.Message}", exception),
    };

    private static ChatResponse? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ChatResponse>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Fall through: some failures are plain text.
        }

        return body.Length > 300 ? body[..300] : body;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string Normalise(string? endpoint)
    {
        var value = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
        return value.TrimEnd('/');
    }

    private static Uri? TryParse(string endpoint) =>
        Uri.TryCreate(endpoint + "/", UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    private sealed class ChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = [];

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("options")]
        public ChatOptions? Options { get; set; }
    }

    private sealed class ChatOptions
    {
        [JsonPropertyName("num_predict")]
        public int NumPredict { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public int? PromptEvalCount { get; set; }

        [JsonPropertyName("eval_count")]
        public int? EvalCount { get; set; }
    }

    private sealed class TagsResponse
    {
        [JsonPropertyName("models")]
        public List<TagEntry>? Models { get; set; }
    }

    private sealed class TagEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
