using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

internal sealed class LearnMcpTools : IAsyncDisposable
{
    private readonly McpClient? _client;

    private LearnMcpTools(McpClient? client, IReadOnlyList<AITool> tools, Uri? endpoint)
    {
        _client = client;
        Tools = tools;
        Endpoint = endpoint;
    }

    public IReadOnlyList<AITool> Tools { get; }

    public Uri? Endpoint { get; }

    public bool IsEnabled => _client is not null;

    public int ToolCount => Tools.Count;

    public static async Task<LearnMcpTools> CreateAsync(
        SampleConfig config,
        CancellationToken cancellationToken = default)
    {
        if (!config.EnableLearnMcp)
        {
            return new LearnMcpTools(client: null, tools: Array.Empty<AITool>(), endpoint: null);
        }

        try
        {
            var endpoint = new Uri(config.LearnMcpEndpoint, UriKind.Absolute);
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = "microsoft-learn",
                    Endpoint = endpoint,
                    TransportMode = HttpTransportMode.AutoDetect
                },
                NullLoggerFactory.Instance);

            var client = await McpClient.CreateAsync(
                transport,
                loggerFactory: NullLoggerFactory.Instance,
                cancellationToken: cancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            return new LearnMcpTools(client, tools.Cast<AITool>().ToArray(), endpoint);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to connect to Microsoft Learn MCP at '{config.LearnMcpEndpoint}'. " +
                "Set MEMNET_ENABLE_LEARN_MCP=false to run without grounded docs.",
                ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        return _client is null ? ValueTask.CompletedTask : _client.DisposeAsync();
    }
}
