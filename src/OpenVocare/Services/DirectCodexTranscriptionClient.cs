using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OpenVocare.Services;

public interface ICodexTranscriptionClient
{
    DictationBridgeResult Probe();
    Task WarmUpAsync(CancellationToken cancellationToken = default);
    Task<string> TranscribeAsync(
        byte[] wavAudio,
        CancellationToken cancellationToken = default);
}

public sealed class DirectCodexTranscriptionClient : ICodexTranscriptionClient, IDisposable
{
    internal const string Endpoint = "https://chatgpt.com/backend-api/transcribe";
    internal const int MaximumTranscriptionResponseCharacters = 2 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan WarmConnectionLifetime = TimeSpan.FromMinutes(8);
    private readonly HttpClient _httpClient;
    private readonly string _authPath;
    private readonly object _authSync = new();
    private readonly object _warmUpSync = new();
    private CodexAuth? _cachedAuth;
    private DateTime _cachedAuthWriteTimeUtc;
    private long _cachedAuthLength;
    private Task? _warmUpTask;
    private DateTimeOffset _lastWarmUp;

    public DirectCodexTranscriptionClient(HttpMessageHandler? handler = null, string? authPath = null)
    {
        _authPath = authPath ?? ResolveAuthPath();
        _httpClient = new HttpClient(
            handler ?? CreatePooledHandler(),
            disposeHandler: handler is null);
        _httpClient.Timeout = RequestTimeout;
        _httpClient.DefaultRequestVersion = HttpVersion.Version20;
        _httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
    }

    public DictationBridgeResult Probe()
    {
        try
        {
            _ = ReadAuth();
            return new DictationBridgeResult(true, "ChatGPT transcription is ready.");
        }
        catch (CodexTranscriptionException exception)
        {
            return new DictationBridgeResult(false, exception.Message);
        }
    }

    public async Task<DictationBridgeResult> CheckReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            CodexAuth auth = ReadAuth();
            using HttpRequestMessage request = new(HttpMethod.Head, Endpoint);
            ApplyHeaders(request, auth);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // HEAD is useful for reachability but this undocumented endpoint
                // does not apply the same authorization behavior as POST.
                return new DictationBridgeResult(
                    true,
                    "ChatGPT sign-in found and the transcription service is reachable. Authentication is verified when dictation starts.");
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new DictationBridgeResult(
                    false,
                    "ChatGPT transcription is reachable but temporarily rate limited.");
            }
            if ((int)response.StatusCode >= 500)
            {
                return new DictationBridgeResult(
                    false,
                    "ChatGPT transcription is reachable but temporarily unavailable.");
            }

            lock (_warmUpSync)
            {
                _lastWarmUp = DateTimeOffset.UtcNow;
            }
            return new DictationBridgeResult(
                true,
                "ChatGPT sign-in found and the transcription service is reachable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DictationBridgeResult(
                false,
                "The ChatGPT connection check timed out.");
        }
        catch (HttpRequestException)
        {
            return new DictationBridgeResult(
                false,
                "ChatGPT sign-in was found, but the transcription service is offline.");
        }
        catch (CodexTranscriptionException exception)
        {
            return new DictationBridgeResult(false, exception.Message);
        }
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        lock (_warmUpSync)
        {
            if (_warmUpTask is { IsCompleted: false })
            {
                return _warmUpTask;
            }
            if (_lastWarmUp != default
                && DateTimeOffset.UtcNow - _lastWarmUp < WarmConnectionLifetime)
            {
                return Task.CompletedTask;
            }
            _warmUpTask = WarmUpCoreAsync(cancellationToken);
            return _warmUpTask;
        }
    }

    private async Task WarmUpCoreAsync(CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            CodexAuth auth = ReadAuth();
            using HttpRequestMessage request = new(HttpMethod.Head, Endpoint);
            ApplyHeaders(request, auth);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            lock (_warmUpSync)
            {
                _lastWarmUp = DateTimeOffset.UtcNow;
            }
            AppLog.WriteDeferred(
                $"Transcription connection warm-up: total={timer.Elapsed.TotalMilliseconds:F0}ms, "
                + $"protocol=HTTP/{response.Version}.");
        }
        catch
        {
            // Warm-up is opportunistic. The real request reports actionable failures.
        }
    }

    public async Task<string> TranscribeAsync(
        byte[] wavAudio,
        CancellationToken cancellationToken = default)
    {
        return await TranscribeAsync(
            wavAudio,
            "dictation.wav",
            "audio/wav",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> TranscribeAsync(
        byte[] audioBytes,
        string fileName,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        if (audioBytes.Length == 0)
        {
            throw new CodexTranscriptionException("The recording was empty.");
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        CancellationToken operationToken = timeout.Token;
        Stopwatch totalTimer = Stopwatch.StartNew();
        Stopwatch stageTimer = Stopwatch.StartNew();
        CodexAuth auth = ReadAuth();
        double authMilliseconds = stageTimer.Elapsed.TotalMilliseconds;
        using MultipartFormDataContent form = new();
        using ByteArrayContent audio = new(audioBytes);
        audio.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(audio, "file", fileName);

        using HttpRequestMessage request = new(HttpMethod.Post, Endpoint);
        ApplyHeaders(request, auth);
        request.Content = form;

        HttpResponseMessage response;
        try
        {
            stageTimer.Restart();
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexTranscriptionException("ChatGPT transcription timed out. Try again.");
        }
        catch (HttpRequestException)
        {
            throw new CodexTranscriptionException(
                "Could not reach ChatGPT transcription. Check your connection and try again.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw MapFailure(response.StatusCode);
            }

            double headersMilliseconds = stageTimer.Elapsed.TotalMilliseconds;
            stageTimer.Restart();
            string body;
            try
            {
                body = await ReadBoundedUtf8Async(
                        response.Content,
                        MaximumTranscriptionResponseCharacters,
                        operationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CodexTranscriptionException("ChatGPT transcription timed out. Try again.");
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException)
            {
                throw new CodexTranscriptionException(
                    "Could not finish reading the ChatGPT transcription response.");
            }
            catch (InvalidDataException)
            {
                throw new CodexTranscriptionException(
                    "ChatGPT returned an unexpectedly large transcription response.");
            }
            double bodyMilliseconds = stageTimer.Elapsed.TotalMilliseconds;

            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("text", out JsonElement text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    string transcript = DeliveredTextSanitizer.Normalize(text.GetString());
                    AppLog.WriteDeferred(
                        $"Transcription timing: total={totalTimer.Elapsed.TotalMilliseconds:F0}ms, "
                        + $"auth={authMilliseconds:F1}ms, headers={headersMilliseconds:F0}ms, "
                        + $"body={bodyMilliseconds:F1}ms, bytes={audioBytes.Length}, "
                        + $"protocol=HTTP/{response.Version}, status={(int)response.StatusCode}.");
                    return transcript;
                }
            }
            catch (JsonException)
            {
                // The sanitized error below deliberately excludes the response body.
            }
        }

        throw new CodexTranscriptionException("ChatGPT returned an invalid transcription response.");
    }

    internal static async Task<string> ReadBoundedUtf8Async(
        HttpContent content,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);

        await using Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        StringBuilder result = new(Math.Min(maximumCharacters, 16 * 1024));
        char[] buffer = new char[4096];
        while (true)
        {
            int read = await reader
                .ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToString();
            }
            if (result.Length > maximumCharacters - read)
            {
                throw new InvalidDataException("The response exceeded the allowed size.");
            }
            result.Append(buffer, 0, read);
        }
    }

    internal CodexAuth ReadAuth()
    {
        try
        {
            lock (_authSync)
            {
                FileInfo file = new(_authPath);
                if (_cachedAuth is not null
                    && file.Exists
                    && file.LastWriteTimeUtc == _cachedAuthWriteTimeUtc
                    && file.Length == _cachedAuthLength)
                {
                    return _cachedAuth;
                }

                using FileStream stream = File.OpenRead(_authPath);
                using JsonDocument document = JsonDocument.Parse(stream);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("tokens", out JsonElement tokens)
                    || tokens.ValueKind != JsonValueKind.Object
                    || !tokens.TryGetProperty("access_token", out JsonElement accessTokenElement))
                {
                    throw new CodexTranscriptionException("Sign in to the ChatGPT desktop app before using dictation.");
                }

                string? accessToken = accessTokenElement.GetString();
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    throw new CodexTranscriptionException("The ChatGPT login does not contain a usable access token.");
                }

                string? accountId = tokens.TryGetProperty("account_id", out JsonElement accountIdElement)
                    ? accountIdElement.GetString()
                    : null;
                _cachedAuth = new CodexAuth(accessToken, accountId);
                file.Refresh();
                _cachedAuthWriteTimeUtc = file.LastWriteTimeUtc;
                _cachedAuthLength = file.Length;
                return _cachedAuth;
            }
        }
        catch (CodexTranscriptionException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw new CodexTranscriptionException("Sign in to the ChatGPT desktop app before using transcription.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CodexTranscriptionException("The local ChatGPT login could not be read.");
        }
    }

    internal static string ResolveAuthPath()
    {
        string? configuredRoot = Environment.GetEnvironmentVariable("CODEX_HOME");
        string root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex")
            : configuredRoot;
        return Path.Combine(root, "auth.json");
    }

    internal static SocketsHttpHandler CreatePooledHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(30),
        KeepAlivePingDelay = TimeSpan.FromMinutes(2),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always
    };

    private static void ApplyHeaders(HttpRequestMessage request, CodexAuth auth)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.TryAddWithoutValidation(
            "originator",
            ProductIdentity.CodexCompatibilityOriginator);
        request.Headers.UserAgent.ParseAdd(ProductIdentity.CodexCompatibilityUserAgent);
        if (!string.IsNullOrWhiteSpace(auth.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.AccountId);
        }
    }

    private static CodexTranscriptionException MapFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new CodexTranscriptionException("The ChatGPT login expired. Open the ChatGPT desktop app and sign in again."),
        HttpStatusCode.TooManyRequests =>
            new CodexTranscriptionException("ChatGPT transcription is temporarily rate limited."),
        HttpStatusCode.RequestEntityTooLarge =>
            new CodexTranscriptionException("The recording is too large for ChatGPT transcription."),
        _ when (int)status >= 500 =>
            new CodexTranscriptionException("ChatGPT transcription is temporarily unavailable."),
        _ => new CodexTranscriptionException($"ChatGPT transcription failed with HTTP {(int)status}.")
    };

    internal sealed record CodexAuth(string AccessToken, string? AccountId)
    {
        public override string ToString() => "CodexAuth { credentials = [redacted] }";
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed class CodexTranscriptionException(string message) : Exception(message);
