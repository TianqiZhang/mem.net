using System.Text.Json;

namespace MemNet.Client;

public sealed class MemNetClientOptions
{
    public Uri? BaseAddress { get; set; }

    public HttpClient? HttpClient { get; set; }

    public string? ServiceId { get; set; }

    public MemNetRetryOptions Retry { get; } = new();

    public JsonSerializerOptions JsonSerializerOptions { get; } = CreateDefaultJsonOptions();

    public Func<CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>>? HeaderProvider { get; set; }

    public Action<HttpRequestMessage>? OnRequest { get; set; }

    public Action<HttpResponseMessage>? OnResponse { get; set; }

    private static JsonSerializerOptions CreateDefaultJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        };
    }
}

public sealed class MemNetRetryOptions
{
    public int MaxRetries { get; set; } = 3;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(2);
}
