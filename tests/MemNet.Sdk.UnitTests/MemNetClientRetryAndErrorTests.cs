using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MemNet.Client;

namespace MemNet.Sdk.UnitTests;

public class MemNetClientRetryAndErrorTests
{
    [Fact]
    public async Task GetFileAsync_MapsApiErrorEnvelope()
    {
        using var httpClient = new HttpClient(
            new SequenceHttpMessageHandler(
                _ => JsonResponse(
                    HttpStatusCode.PreconditionFailed,
                    """
                    {
                      "error": {
                        "code": "ETAG_MISMATCH",
                        "message": "stale",
                        "request_id": "req-1",
                        "details": { "latest_etag": "\"B\"" }
                      }
                    }
                    """)))
        {
            BaseAddress = new Uri("http://localhost")
        };

        using var client = new MemNetClient(new MemNetClientOptions
        {
            HttpClient = httpClient
        });

        var ex = await Assert.ThrowsAsync<MemNetApiException>(
            () => client.GetFileAsync(new MemNetScope("tenant", "user"), new FileRef("user/profile.json")));

        Assert.Equal(HttpStatusCode.PreconditionFailed, ex.StatusCode);
        Assert.Equal("ETAG_MISMATCH", ex.Code);
        Assert.Equal("req-1", ex.RequestId);
        Assert.Equal("\"B\"", ex.Details?["latest_etag"]);
    }

    [Fact]
    public async Task UpdateWithRetryAsync_RetriesOnEtagMismatch()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => JsonResponse(HttpStatusCode.OK, FileReadJson("\"A\"", "initial")),
            request =>
            {
                Assert.Equal(HttpMethod.Patch, request.Method);
                Assert.Equal("\"A\"", request.Headers.IfMatch.Single().ToString());
                return JsonResponse(
                    HttpStatusCode.PreconditionFailed,
                    """
                    {
                      "error": {
                        "code": "ETAG_MISMATCH",
                        "message": "stale",
                        "request_id": "req-conflict"
                      }
                    }
                    """);
            },
            _ => JsonResponse(HttpStatusCode.OK, FileReadJson("\"B\"", "intermediate")),
            request =>
            {
                Assert.Equal(HttpMethod.Patch, request.Method);
                Assert.Equal("\"B\"", request.Headers.IfMatch.Single().ToString());
                return JsonResponse(HttpStatusCode.OK, FileMutationJson("\"C\"", "final"));
            });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost")
        };

        using var client = new MemNetClient(new MemNetClientOptions
        {
            HttpClient = httpClient
        });

        var calls = 0;
        var result = await client.UpdateWithRetryAsync(
            new MemNetScope("tenant", "user"),
            new FileRef("user/long_term_memory.json"),
            current =>
            {
                calls++;
                return FileUpdate.FromPatch(
                    new PatchDocumentRequest(
                        Ops:
                        [
                            new PatchOperation("replace", "/content/text", JsonValue.Create("final"))
                        ],
                        Reason: "retry"));
            },
            maxConflictRetries: 3);

        Assert.Equal(2, calls);
        Assert.Equal("\"C\"", result.ETag);
        Assert.Equal("final", result.Document.Content["text"]?.GetValue<string>());
        Assert.Equal(4, handler.CallCount);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string FileReadJson(string etag, string text) =>
        $$"""
          {
            "etag": {{JsonString(etag)}},
            "document": {
              "doc_id": "doc-1",
              "schema_id": "memnet.file",
              "schema_version": "1.0.0",
              "created_at": "2026-01-01T00:00:00+00:00",
              "updated_at": "2026-01-01T00:00:00+00:00",
              "updated_by": "seed",
              "content": { "text": {{JsonString(text)}} }
            }
          }
          """;

    private static string FileMutationJson(string etag, string text) =>
        $$"""
          {
            "etag": {{JsonString(etag)}},
            "document": {
              "doc_id": "doc-1",
              "schema_id": "memnet.file",
              "schema_version": "1.0.0",
              "created_at": "2026-01-01T00:00:00+00:00",
              "updated_at": "2026-01-01T00:00:00+00:00",
              "updated_by": "seed",
              "content": { "text": {{JsonString(text)}} }
            }
          }
          """;

    private static string JsonString(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private sealed class SequenceHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _steps = new(steps);

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_steps.Count == 0)
            {
                throw new Xunit.Sdk.XunitException($"No response configured for request #{CallCount + 1}: {request.Method} {request.RequestUri}");
            }

            CallCount++;
            return Task.FromResult(_steps.Dequeue()(request));
        }
    }
}
