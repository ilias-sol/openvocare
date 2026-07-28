using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OpenVocare.Services;

namespace OpenVocare.Tests;

public sealed class DirectCodexTranscriptionClientTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OpenVocareTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Probe_RejectsMissingAuthWithoutExposingAPath()
    {
        DirectCodexTranscriptionClient client = new(
            new FakeHandler(_ => Json(HttpStatusCode.OK, """{"text":"ok"}""")),
            Path.Combine(_directory, "missing.json"));

        DictationBridgeResult result = client.Probe();

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(_directory, result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveAuthPath_IgnoresAnEmptyCodexHome()
    {
        string? previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", string.Empty);

            string result = DirectCodexTranscriptionClient.ResolveAuthPath();

            Assert.Equal(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex",
                    "auth.json"),
                result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }

    [Fact]
    public async Task Transcribe_UsesCodexAuthAndReturnsText()
    {
        string authPath = WriteAuth();
        FakeHandler handler = new(request =>
        {
            Assert.Equal(DirectCodexTranscriptionClient.Endpoint, request.RequestUri?.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("secret-access-token", request.Headers.Authorization?.Parameter);
            Assert.Contains("account-123", request.Headers.GetValues("ChatGPT-Account-Id"));
            Assert.IsType<MultipartFormDataContent>(request.Content);
            return Json(HttpStatusCode.OK, """{"text":"  dictated text  "}""");
        });
        DirectCodexTranscriptionClient client = new(handler, authPath);

        string result = await client.TranscribeAsync(Encoding.UTF8.GetBytes("fake-wav"));

        Assert.Equal("dictated text", result);
    }

    [Fact]
    public async Task WarmUp_UsesHeadAndTreatsFailureAsOpportunistic()
    {
        string authPath = WriteAuth();
        bool called = false;
        DirectCodexTranscriptionClient client = new(
            new FakeHandler(request =>
            {
                called = true;
                Assert.Equal(HttpMethod.Head, request.Method);
                Assert.Equal(DirectCodexTranscriptionClient.Endpoint, request.RequestUri?.ToString());
                Assert.Equal(HttpVersion.Version20, request.Version);
                return Json(HttpStatusCode.MethodNotAllowed, "");
            }),
            authPath);

        await client.WarmUpAsync();

        Assert.True(called);
    }

    [Fact]
    public async Task CheckReadiness_VerifiesServiceWithCompatibilityIdentity()
    {
        string authPath = WriteAuth();
        DirectCodexTranscriptionClient client = new(
            new FakeHandler(request =>
            {
                Assert.Equal(HttpMethod.Head, request.Method);
                Assert.Contains(
                    ProductIdentity.CodexCompatibilityOriginator,
                    request.Headers.GetValues("originator"));
                Assert.Equal(
                    ProductIdentity.CodexCompatibilityUserAgent,
                    request.Headers.UserAgent.ToString());
                return Json(HttpStatusCode.MethodNotAllowed, "");
            }),
            authPath);

        DictationBridgeResult result = await client.CheckReadinessAsync();

        Assert.True(result.IsSuccess);
        Assert.Contains("reachable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckReadiness_TreatsHeadAuthResponseAsReachableButUnverified()
    {
        DirectCodexTranscriptionClient client = new(
            new FakeHandler(_ => Json(HttpStatusCode.Unauthorized, "sensitive")),
            WriteAuth());

        DictationBridgeResult result = await client.CheckReadinessAsync();

        Assert.True(result.IsSuccess);
        Assert.Contains("verified when dictation starts", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WarmUp_CoalescesRecentSuccessfulRequests()
    {
        string authPath = WriteAuth();
        int calls = 0;
        DirectCodexTranscriptionClient client = new(
            new FakeHandler(_ =>
            {
                calls++;
                return Json(HttpStatusCode.MethodNotAllowed, "");
            }),
            authPath);

        await client.WarmUpAsync();
        await client.WarmUpAsync();

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AuthCache_RefreshesWhenTheLoginFileChanges()
    {
        string authPath = WriteAuth();
        List<string?> tokens = [];
        DirectCodexTranscriptionClient client = new(
            new FakeHandler(request =>
            {
                tokens.Add(request.Headers.Authorization?.Parameter);
                return Json(HttpStatusCode.OK, """{"text":"ok"}""");
            }),
            authPath);

        await client.TranscribeAsync(Encoding.UTF8.GetBytes("first"));
        await Task.Delay(20);
        File.WriteAllText(
            authPath,
            """
            {
              "tokens": {
                "access_token": "replacement-token",
                "account_id": "account-123"
              }
            }
            """);
        File.SetLastWriteTimeUtc(authPath, DateTime.UtcNow.AddSeconds(1));
        await client.TranscribeAsync(Encoding.UTF8.GetBytes("second"));

        Assert.Equal(["secret-access-token", "replacement-token"], tokens);
    }

    [Fact]
    public void AuthValue_DoesNotExposeCredentialsThroughRecordFormatting()
    {
        var auth = new DirectCodexTranscriptionClient.CodexAuth(
            "secret-access-token",
            "account-123");

        string formatted = auth.ToString();

        Assert.Contains("redacted", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-access-token", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("account-123", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transcribe_MapsExpiredLoginWithoutReturningServerBody()
    {
        string authPath = WriteAuth();
        DirectCodexTranscriptionClient client = new(
            new FakeHandler(_ => Json(HttpStatusCode.Unauthorized, "sensitive upstream response")),
            authPath);

        CodexTranscriptionException error = await Assert.ThrowsAsync<CodexTranscriptionException>(
            () => client.TranscribeAsync(Encoding.UTF8.GetBytes("fake-wav")));

        Assert.Contains("expired", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transcribe_MapsNetworkFailureWithoutExposingExceptionDetails()
    {
        string authPath = WriteAuth();
        DirectCodexTranscriptionClient client = new(
            new ThrowingHandler(new HttpRequestException("sensitive proxy details")),
            authPath);

        CodexTranscriptionException error = await Assert.ThrowsAsync<CodexTranscriptionException>(
            () => client.TranscribeAsync(Encoding.UTF8.GetBytes("fake-wav")));

        Assert.Contains("reach", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proxy", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transcribe_RejectsAnUnboundedSuccessResponse()
    {
        string oversizedText = new(
            'x',
            DirectCodexTranscriptionClient.MaximumTranscriptionResponseCharacters + 1);
        DirectCodexTranscriptionClient client = new(
            new FakeHandler(_ => Json(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new { text = oversizedText }))),
            WriteAuth());

        CodexTranscriptionException error =
            await Assert.ThrowsAsync<CodexTranscriptionException>(
                () => client.TranscribeAsync(Encoding.UTF8.GetBytes("fake-wav")));

        Assert.Contains("large", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(oversizedText[..32], error.Message, StringComparison.Ordinal);
    }

    private string WriteAuth()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "auth.json");
        File.WriteAllText(
            path,
            """
            {
              "auth_mode": "chatgpt",
              "tokens": {
                "access_token": "secret-access-token",
                "account_id": "account-123"
              }
            }
            """);
        return path;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
