using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MemNet.MemoryService.Infrastructure;

public interface IMemoryBackend
{
    string Name { get; }

    void RegisterServices(IServiceCollection services, IConfiguration configuration);
}

public sealed class FilesystemMemoryBackend : IMemoryBackend
{
    public string Name => "filesystem";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration;
        services.AddSingleton<IDocumentStore, FileDocumentStore>();
        services.AddSingleton<IEventStore, FileEventStore>();
        services.AddSingleton<IAuditStore, FileAuditStore>();
        services.AddSingleton<IUserDataMaintenanceStore, FileUserDataMaintenanceStore>();
    }
}

public sealed class AzureMemoryBackend : IMemoryBackend
{
    public string Name => "azure";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var azureOptions = AzureProviderOptions.FromConfiguration(configuration);
        services.AddSingleton(azureOptions);
        services.AddSingleton<AzureClients>();
        services.AddSingleton<IDocumentStore, AzureBlobDocumentStore>();
        services.AddSingleton<IEventStore, AzureBlobEventStore>();
        services.AddSingleton<IAuditStore, AzureBlobAuditStore>();
        services.AddSingleton<IUserDataMaintenanceStore, AzureBlobUserDataMaintenanceStore>();
    }
}

public static class MemoryBackendFactory
{
    public static IMemoryBackend Create(string provider)
    {
        return provider switch
        {
            "filesystem" => new FilesystemMemoryBackend(),
            "azure" => new AzureMemoryBackend(),
            _ => throw new InvalidOperationException($"Unsupported provider '{provider}'.")
        };
    }
}
