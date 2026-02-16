#if MEMNET_ENABLE_AZURE_SDK
using MemNet.MemoryService.Infrastructure;
using Microsoft.Extensions.Configuration;

internal sealed partial class SpecRunner
{
    private static Task AzureProviderOptionsMappingAndValidationAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MemNet:Azure:StorageServiceUri"] = "https://example.blob.core.windows.net",
                ["MemNet:Azure:DocumentsContainerName"] = "docs",
                ["MemNet:Azure:EventsContainerName"] = "events",
                ["MemNet:Azure:AuditContainerName"] = "audit",
                ["MemNet:Azure:SearchEndpoint"] = "https://example.search.windows.net",
                ["MemNet:Azure:SearchIndexName"] = "events-index",
                ["MemNet:Azure:RetryMaxRetries"] = "5",
                ["MemNet:Azure:RetryDelayMs"] = "250",
                ["MemNet:Azure:RetryMaxDelayMs"] = "3000",
                ["MemNet:Azure:NetworkTimeoutSeconds"] = "45"
            })
            .Build();

        var options = AzureProviderOptions.FromConfiguration(config);
        Assert.Equal("https://example.blob.core.windows.net", options.StorageServiceUri);
        Assert.Equal("docs", options.DocumentsContainerName);
        Assert.Equal("events", options.EventsContainerName);
        Assert.Equal("audit", options.AuditContainerName);
        Assert.Equal("https://example.search.windows.net", options.SearchEndpoint);
        Assert.Equal("events-index", options.SearchIndexName);
        Assert.Equal(5, options.RetryMaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.RetryDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(3000), options.RetryMaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(45), options.NetworkTimeout);

        Assert.Throws<InvalidOperationException>(() =>
        {
            var invalidConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MemNet:Azure:StorageServiceUri"] = "https://example.blob.core.windows.net",
                    ["MemNet:Azure:SearchEndpoint"] = "https://example.search.windows.net"
                })
                .Build();

            _ = AzureProviderOptions.FromConfiguration(invalidConfig);
        });

        return Task.CompletedTask;
    }

    private static Task AzureSearchFilterRequestShapingAsync()
    {
        var from = new DateTimeOffset(2025, 01, 01, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var request = new EventSearchRequest(
            Query: "latency",
            ServiceId: "assistant'o",
            SourceType: "chat",
            ProjectId: "project'o",
            From: from,
            To: to,
            TopK: 10);

        var filter = AzureBlobEventStore.BuildFilter("tenant'o", "user'o", request);
        Assert.True(!string.IsNullOrWhiteSpace(filter), "Expected non-empty Azure filter.");
        var shapedFilter = filter!;

        Assert.True(shapedFilter.Contains("tenant_id eq 'tenant''o'", StringComparison.Ordinal), "Expected tenant filter escape.");
        Assert.True(shapedFilter.Contains("user_id eq 'user''o'", StringComparison.Ordinal), "Expected user filter escape.");
        Assert.True(shapedFilter.Contains("service_id eq 'assistant''o'", StringComparison.Ordinal), "Expected service filter escape.");
        Assert.True(shapedFilter.Contains("source_type eq 'chat'", StringComparison.Ordinal), "Expected source type filter.");
        Assert.True(shapedFilter.Contains("project_ids/any(p: p eq 'project''o')", StringComparison.Ordinal), "Expected project filter escape.");
        Assert.True(shapedFilter.Contains($"timestamp ge {from.UtcDateTime:O}", StringComparison.Ordinal), "Expected from timestamp filter.");
        Assert.True(shapedFilter.Contains($"timestamp le {to.UtcDateTime:O}", StringComparison.Ordinal), "Expected to timestamp filter.");

        return Task.CompletedTask;
    }
}
#endif
