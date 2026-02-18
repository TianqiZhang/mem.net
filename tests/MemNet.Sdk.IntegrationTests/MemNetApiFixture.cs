using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MemNet.Sdk.IntegrationTests;

public static class MemNetApiTestCollection
{
    public const string Name = "memnet-sdk-api";
}

[CollectionDefinition(MemNetApiTestCollection.Name)]
public sealed class MemNetApiTestCollectionDefinition : ICollectionFixture<MemNetApiFixture>
{
}

public sealed class MemNetApiFixture : IAsyncLifetime
{
    private string _dataRoot = string.Empty;
    private string? _previousDataRootEnv;
    private string? _previousProviderEnv;

    public string TenantId => "tenant-sdk";

    public string UserId => "user-sdk";

    public string DataRoot => _dataRoot;

    public MemNetApiFactory Factory { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "memnet-sdk-integration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        _previousDataRootEnv = Environment.GetEnvironmentVariable("MEMNET_DATA_ROOT");
        _previousProviderEnv = Environment.GetEnvironmentVariable("MEMNET_PROVIDER");
        Environment.SetEnvironmentVariable("MEMNET_DATA_ROOT", _dataRoot);
        Environment.SetEnvironmentVariable("MEMNET_PROVIDER", "filesystem");

        Factory = new MemNetApiFactory(_dataRoot);
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();

        if (!string.IsNullOrWhiteSpace(_dataRoot) && Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }

        Environment.SetEnvironmentVariable("MEMNET_DATA_ROOT", _previousDataRootEnv);
        Environment.SetEnvironmentVariable("MEMNET_PROVIDER", _previousProviderEnv);
    }

    public void ResetDataRoot()
    {
        if (!string.IsNullOrWhiteSpace(_dataRoot) && Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }

        Directory.CreateDirectory(_dataRoot);
    }
}

public sealed class MemNetApiFactory(string dataRoot) : WebApplicationFactory<Program>
{
    private readonly string _dataRoot = dataRoot;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MemNet:Provider"] = "filesystem",
                ["MemNet:DataRoot"] = _dataRoot
            });
        });
    }
}
