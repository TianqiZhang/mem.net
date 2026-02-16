using System.Net;
using System.Net.Http.Json;

internal sealed partial class SpecRunner
{
    private static async Task AzureLiveSmokeTestIfConfiguredAsync()
    {
        var runLive = Environment.GetEnvironmentVariable("MEMNET_RUN_AZURE_LIVE_TESTS");
        if (!string.Equals(runLive, "1", StringComparison.Ordinal))
        {
            return;
        }

        var requiredEnv = new[]
        {
            "MEMNET_AZURE_STORAGE_SERVICE_URI",
            "MEMNET_AZURE_DOCUMENTS_CONTAINER",
            "MEMNET_AZURE_EVENTS_CONTAINER",
            "MEMNET_AZURE_AUDIT_CONTAINER"
        };

        var missing = requiredEnv
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToList();
        if (missing.Count > 0)
        {
            throw new Exception($"Azure live test requested, but required env vars are missing: {string.Join(", ", missing)}");
        }

        using var scope = TestScope.Create();
        var liveDll = Path.Combine(scope.RepoRoot, "src", "MemNet.MemoryService", "bin", "Debug", "azure", "net8.0", "MemNet.MemoryService.dll");
        if (!File.Exists(liveDll))
        {
            throw new Exception($"Azure-enabled service DLL not found: {liveDll}");
        }

        var azureEnv = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["MEMNET_AZURE_STORAGE_SERVICE_URI"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_STORAGE_SERVICE_URI"),
            ["MEMNET_AZURE_DOCUMENTS_CONTAINER"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_DOCUMENTS_CONTAINER"),
            ["MEMNET_AZURE_EVENTS_CONTAINER"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_EVENTS_CONTAINER"),
            ["MEMNET_AZURE_AUDIT_CONTAINER"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_AUDIT_CONTAINER"),
            ["MEMNET_AZURE_SEARCH_ENDPOINT"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_ENDPOINT"),
            ["MEMNET_AZURE_SEARCH_INDEX"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_INDEX"),
            ["MEMNET_AZURE_SEARCH_API_KEY"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_API_KEY"),
            ["MEMNET_AZURE_MANAGED_IDENTITY_CLIENT_ID"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_MANAGED_IDENTITY_CLIENT_ID"),
            ["MEMNET_AZURE_USE_MANAGED_IDENTITY_ONLY"] = Environment.GetEnvironmentVariable("MEMNET_AZURE_USE_MANAGED_IDENTITY_ONLY")
        };

        using var host = await ServiceHost.StartAsync(
            scope.RepoRoot,
            scope.DataRoot,
            scope.ConfigRoot,
            provider: "azure",
            additionalEnvironment: azureEnv,
            serviceDllPath: liveDll);

        using var client = CreateHttpClient(host.BaseAddress);

        var health = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var retentionResponse = await client.PostAsJsonAsync(
            $"/v1/tenants/{scope.Keys.Tenant}/users/{scope.Keys.User}/retention:apply",
            new { policy_id = "project-copilot-v1", as_of_utc = (DateTimeOffset?)null });
        Assert.Equal(HttpStatusCode.OK, retentionResponse.StatusCode);
    }
}
