using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OpenVocare.Models;
using OpenVocare.Services;

namespace OpenVocare.Tests;

public sealed class DirectCodexRewriteServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OpenVocareRewriteTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Rewrite_UsesSubscriptionAuthAndCurrentResponsesShape()
    {
        string authPath = WriteAuth();
        DirectCodexTranscriptionClient authSource = new(
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            authPath);
        FakeHandler rewriteHandler = new(request =>
        {
            Assert.Equal(DirectCodexRewriteService.Endpoint, request.RequestUri?.ToString());
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(HttpVersion.Version20, request.Version);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("secret-access-token", request.Headers.Authorization?.Parameter);
            Assert.Contains("account-123", request.Headers.GetValues("ChatGPT-Account-Id"));
            Assert.Contains(
                ProductIdentity.CodexCompatibilityOriginator,
                request.Headers.GetValues("originator"));
            Assert.Equal(
                ProductIdentity.CodexCompatibilityUserAgent,
                request.Headers.UserAgent.ToString());
            Assert.Contains(
                request.Headers.Accept,
                value => value.MediaType == "text/event-stream");

            string json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            Assert.Equal(ProductIdentity.RewriteModel, root.GetProperty("model").GetString());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            Assert.Empty(root.GetProperty("tools").EnumerateArray());
            Assert.Equal("none", root.GetProperty("tool_choice").GetString());

            return Sse(
                """
                data: {"type":"response.output_text.delta","delta":"{\"text\":\"Clean"}

                data: {"type":"response.output_text.delta","delta":" result.\"}"}

                data: {"type":"response.completed","response":{"id":"resp_test"}}

                """);
        });
        using DirectCodexRewriteService service = new(authSource, rewriteHandler);

        string result = await service.RewriteAsync(
            "um clean this",
            new RewriteSettings { Mode = RewriteMode.Minimal });

        Assert.Equal("Clean result.", result);
    }

    [Fact]
    public async Task Rewrite_UsesCompletedItemWhenDeltasAreAbsent()
    {
        DirectCodexTranscriptionClient authSource = new(
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            WriteAuth());
        using DirectCodexRewriteService service = new(
            authSource,
            new FakeHandler(_ => Sse(
                """
                data: {"type":"response.output_item.done","item":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"{\"text\":\"Completed item text.\"}"}]}}

                data: {"type":"response.completed","response":{"id":"resp_test"}}

                """)));

        string result = await service.RewriteAsync(
            "test",
            new RewriteSettings { Mode = RewriteMode.Professional });

        Assert.Equal("Completed item text.", result);
    }

    [Fact]
    public async Task Rewrite_DoesNotReturnUpstreamFailureBody()
    {
        DirectCodexTranscriptionClient authSource = new(
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            WriteAuth());
        using DirectCodexRewriteService service = new(
            authSource,
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("sensitive upstream body")
            }));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RewriteAsync(
                "test",
                new RewriteSettings { Mode = RewriteMode.Minimal }));

        Assert.Contains("expired", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rewrite_DoesNotReadAnUpstreamFailureBody()
    {
        DirectCodexTranscriptionClient authSource = new(
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            WriteAuth());
        using DirectCodexRewriteService service = new(
            authSource,
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new ThrowOnReadContent()
            }));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RewriteAsync(
                "test",
                new RewriteSettings { Mode = RewriteMode.Minimal }));

        Assert.Contains("400", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("body was read", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rewrite_RejectsAnUnboundedOutput()
    {
        DirectCodexTranscriptionClient authSource = new(
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            WriteAuth());
        string oversized = new(
            'x',
            DirectCodexRewriteService.MaximumRewriteOutputCharacters + 1);
        using DirectCodexRewriteService service = new(
            authSource,
            new FakeHandler(_ => Sse(
                $"data: {JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = oversized })}\n\n")));

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RewriteAsync(
                    "test",
                    new RewriteSettings { Mode = RewriteMode.Minimal }));

        Assert.Contains("large", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(oversized[..32], error.Message, StringComparison.Ordinal);
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

    private static HttpResponseMessage Sse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "text/event-stream")
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

    private sealed class ThrowOnReadContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            Task.FromException(new InvalidOperationException("body was read"));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

}
