using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenVocare.Models;

namespace OpenVocare.Services;

public sealed class DirectCodexRewriteService : ITranscriptRewriteService, IDisposable
{
    internal const string Endpoint = "https://chatgpt.com/backend-api/codex/responses";
    internal const int MaximumRewriteOutputCharacters = 2 * 1024 * 1024;
    private const int MaximumSseEventCharacters = 4 * 1024 * 1024;
    private const int MaximumSseStreamCharacters = 16 * 1024 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(9);
    private readonly DirectCodexTranscriptionClient _authSource;
    private readonly HttpClient _httpClient;

    public DirectCodexRewriteService(
        DirectCodexTranscriptionClient authSource,
        HttpMessageHandler? handler = null)
    {
        _authSource = authSource;
        _httpClient = new HttpClient(
            handler ?? DirectCodexTranscriptionClient.CreatePooledHandler(),
            disposeHandler: handler is null);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _httpClient.DefaultRequestVersion = HttpVersion.Version20;
        _httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
    }

    public async Task<string> RewriteAsync(
        string transcript,
        RewriteSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (settings.Mode == RewriteMode.Verbatim)
        {
            return transcript;
        }
        if (string.IsNullOrWhiteSpace(transcript))
        {
            throw new InvalidOperationException("The transcript was empty.");
        }

        DirectCodexTranscriptionClient.CodexAuth auth = _authSource.ReadAuth();
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using HttpRequestMessage request = CreateRequest(transcript, settings, auth);
        Stopwatch timer = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The ChatGPT rewrite exceeded {RequestTimeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("The ChatGPT rewrite request could not connect.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw MapFailure(response.StatusCode);
            }

            string output;
            try
            {
                await using Stream stream = await response.Content
                    .ReadAsStreamAsync(timeout.Token)
                    .ConfigureAwait(false);
                output = await ReadSseOutputAsync(stream, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The ChatGPT rewrite exceeded {RequestTimeout.TotalSeconds:0} seconds.");
            }

            timer.Stop();
            AppLog.Write($"Rewrite timing: transport=direct-responses, total={timer.ElapsedMilliseconds}ms.");
            return TranscriptRewriteProtocol.ParseOutput(output);
        }
    }

    internal static HttpRequestMessage CreateRequest(
        string transcript,
        RewriteSettings settings,
        DirectCodexTranscriptionClient.CodexAuth auth)
    {
        string prompt = TranscriptRewriteProtocol.BuildPrompt(transcript, settings);
        var body = new
        {
            model = ProductIdentity.RewriteModel,
            instructions = "Act only as the text transformation component described by the user input.",
            input = new[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new[] { new { type = "input_text", text = prompt } }
                }
            },
            tools = Array.Empty<object>(),
            tool_choice = "none",
            parallel_tool_calls = false,
            reasoning = new { effort = "low" },
            store = false,
            stream = true,
            include = Array.Empty<string>(),
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    strict = true,
                    name = "openvocare_rewrite",
                    schema = new
                    {
                        type = "object",
                        properties = new { text = new { type = "string" } },
                        required = new[] { "text" },
                        additionalProperties = false
                    }
                }
            }
        };

        HttpRequestMessage request = new(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation(
            "originator",
            ProductIdentity.CodexCompatibilityOriginator);
        request.Headers.UserAgent.ParseAdd(ProductIdentity.CodexCompatibilityUserAgent);
        string requestId = Guid.NewGuid().ToString();
        request.Headers.TryAddWithoutValidation("x-client-request-id", requestId);
        request.Headers.TryAddWithoutValidation("session-id", requestId);
        request.Headers.TryAddWithoutValidation("thread-id", requestId);
        if (!string.IsNullOrWhiteSpace(auth.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.AccountId);
        }
        return request;
    }

    internal static async Task<string> ReadSseOutputAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(stream, Encoding.UTF8);
        StringBuilder eventData = new();
        StringBuilder deltas = new();
        string? completedItemText = null;
        bool completed = false;
        int streamCharacters = 0;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (streamCharacters > MaximumSseStreamCharacters - line.Length - 1)
            {
                throw new InvalidOperationException("The ChatGPT rewrite response was too large.");
            }
            streamCharacters += line.Length + 1;
            if (line.Length == 0)
            {
                if (eventData.Length > 0)
                {
                    ProcessEvent(eventData.ToString(), deltas, ref completedItemText, ref completed);
                    eventData.Clear();
                    if (completed)
                    {
                        break;
                    }
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                int separatorLength = eventData.Length > 0 ? 1 : 0;
                int dataLength = line.Length - 5;
                if (eventData.Length
                    > MaximumSseEventCharacters - separatorLength - dataLength)
                {
                    throw new InvalidOperationException(
                        "The ChatGPT rewrite event was too large.");
                }
                if (separatorLength != 0)
                {
                    eventData.Append('\n');
                }
                eventData.Append(line.AsSpan(5).TrimStart());
            }
        }

        if (!completed && eventData.Length > 0)
        {
            ProcessEvent(eventData.ToString(), deltas, ref completedItemText, ref completed);
        }
        if (!completed)
        {
            throw new InvalidOperationException("The ChatGPT response ended before completion.");
        }

        string output = deltas.Length > 0 ? deltas.ToString() : completedItemText ?? string.Empty;
        return string.IsNullOrWhiteSpace(output)
            ? throw new InvalidOperationException("ChatGPT returned an empty rewrite.")
            : output;
    }

    private static void ProcessEvent(
        string data,
        StringBuilder deltas,
        ref string? completedItemText,
        ref bool completed)
    {
        if (data == "[DONE]")
        {
            return;
        }

        using JsonDocument document = JsonDocument.Parse(data);
        JsonElement root = document.RootElement;
        string? type = root.TryGetProperty("type", out JsonElement typeElement)
            ? typeElement.GetString()
            : null;
        switch (type)
        {
            case "response.output_text.delta":
                if (root.TryGetProperty("delta", out JsonElement delta)
                    && delta.ValueKind == JsonValueKind.String)
                {
                    string value = delta.GetString() ?? string.Empty;
                    if (deltas.Length > MaximumRewriteOutputCharacters - value.Length)
                    {
                        throw new InvalidOperationException(
                            "The ChatGPT rewrite output was too large.");
                    }
                    deltas.Append(value);
                }
                break;
            case "response.output_item.done":
                string? completedText = ReadCompletedItemText(root);
                if (completedText?.Length > MaximumRewriteOutputCharacters)
                {
                    throw new InvalidOperationException(
                        "The ChatGPT rewrite output was too large.");
                }
                completedItemText = completedText ?? completedItemText;
                break;
            case "response.completed":
                completed = true;
                break;
            case "response.failed":
                throw new InvalidOperationException(ReadStreamFailure(root));
            case "response.incomplete":
                throw new InvalidOperationException("ChatGPT returned an incomplete rewrite.");
        }
    }

    private static string? ReadCompletedItemText(JsonElement root)
    {
        if (!root.TryGetProperty("item", out JsonElement item)
            || !item.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (JsonElement part in content.EnumerateArray())
        {
            if (part.TryGetProperty("type", out JsonElement type)
                && type.GetString() == "output_text"
                && part.TryGetProperty("text", out JsonElement text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }
        return null;
    }

    private static string ReadStreamFailure(JsonElement root)
    {
        if (root.TryGetProperty("response", out JsonElement response)
            && response.TryGetProperty("error", out JsonElement error)
            && error.TryGetProperty("code", out JsonElement code))
        {
            return $"ChatGPT rewrite failed ({code.GetString() ?? "unknown"}).";
        }
        return "ChatGPT rewrite failed.";
    }

    private static InvalidOperationException MapFailure(HttpStatusCode status)
    {
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new InvalidOperationException("The ChatGPT login expired."),
            HttpStatusCode.TooManyRequests =>
                new InvalidOperationException("ChatGPT rewrite is temporarily rate limited."),
            _ when (int)status >= 500 =>
                new InvalidOperationException("ChatGPT rewrite is temporarily unavailable."),
            _ => new InvalidOperationException($"ChatGPT rewrite failed with HTTP {(int)status}.")
        };
    }

    public void Dispose() => _httpClient.Dispose();
}
