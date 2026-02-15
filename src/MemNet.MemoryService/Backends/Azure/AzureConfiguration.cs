#if MEMNET_ENABLE_AZURE_SDK
using System.Globalization;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using MemNet.MemoryService.Core;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Infrastructure;

public sealed record AzureProviderOptions(
    string StorageServiceUri,
    string DocumentsContainerName,
    string EventsContainerName,
    string AuditContainerName,
    string? SearchEndpoint,
    string? SearchIndexName,
    string? SearchApiKey,
    string? ManagedIdentityClientId,
    bool UseManagedIdentityOnly,
    int RetryMaxRetries,
    TimeSpan RetryDelay,
    TimeSpan RetryMaxDelay,
    TimeSpan NetworkTimeout)
{
    public static AzureProviderOptions FromConfiguration(IConfiguration configuration)
    {
        var storageServiceUri = FirstNonEmpty(
            configuration["MemNet:Azure:StorageServiceUri"],
            Environment.GetEnvironmentVariable("MEMNET_AZURE_STORAGE_SERVICE_URI"));

        if (string.IsNullOrWhiteSpace(storageServiceUri))
        {
            throw new InvalidOperationException(
                "Azure provider requires MemNet:Azure:StorageServiceUri or MEMNET_AZURE_STORAGE_SERVICE_URI.");
        }

        var documentsContainerName = FirstNonEmpty(
            configuration["MemNet:Azure:DocumentsContainerName"],
            Environment.GetEnvironmentVariable("MEMNET_AZURE_DOCUMENTS_CONTAINER"),
            "memnet-documents") ?? "memnet-documents";

        var eventsContainerName = FirstNonEmpty(
            configuration["MemNet:Azure:EventsContainerName"],
            Environment.GetEnvironmentVariable("MEMNET_AZURE_EVENTS_CONTAINER"),
            "memnet-events") ?? "memnet-events";

        var auditContainerName = FirstNonEmpty(
            configuration["MemNet:Azure:AuditContainerName"],
            Environment.GetEnvironmentVariable("MEMNET_AZURE_AUDIT_CONTAINER"),
            "memnet-audit") ?? "memnet-audit";

        var searchEndpoint = FirstNonEmpty(
            configuration["MemNet:Azure:SearchEndpoint"],
            Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_ENDPOINT"));

        var searchIndexName = FirstNonEmpty(
            configuration["MemNet:Azure:SearchIndexName"],
            Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_INDEX"));

        var searchApiKey = FirstNonEmpty(
            configuration["MemNet:Azure:SearchApiKey"],
            Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_API_KEY"));

        var managedIdentityClientId = FirstNonEmpty(
            configuration["MemNet:Azure:ManagedIdentityClientId"],
            Environment.GetEnvironmentVariable("MEMNET_AZURE_MANAGED_IDENTITY_CLIENT_ID"));

        var useManagedIdentityOnly = bool.TryParse(
                FirstNonEmpty(
                    configuration["MemNet:Azure:UseManagedIdentityOnly"],
                    Environment.GetEnvironmentVariable("MEMNET_AZURE_USE_MANAGED_IDENTITY_ONLY")),
                out var parsed)
            && parsed;

        if (!Uri.TryCreate(storageServiceUri, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("MemNet:Azure:StorageServiceUri must be an absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(documentsContainerName)
            || string.IsNullOrWhiteSpace(eventsContainerName)
            || string.IsNullOrWhiteSpace(auditContainerName))
        {
            throw new InvalidOperationException("Azure container names must be non-empty.");
        }

        var hasSearchEndpoint = !string.IsNullOrWhiteSpace(searchEndpoint);
        var hasSearchIndex = !string.IsNullOrWhiteSpace(searchIndexName);
        if (hasSearchEndpoint ^ hasSearchIndex)
        {
            throw new InvalidOperationException(
                "MemNet:Azure:SearchEndpoint and MemNet:Azure:SearchIndexName must be configured together.");
        }

        if (hasSearchEndpoint && !Uri.TryCreate(searchEndpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("MemNet:Azure:SearchEndpoint must be an absolute URI.");
        }

        var retryMaxRetries = ParseBoundedInt(
            FirstNonEmpty(
                configuration["MemNet:Azure:RetryMaxRetries"],
                Environment.GetEnvironmentVariable("MEMNET_AZURE_RETRY_MAX_RETRIES")),
            defaultValue: 3,
            min: 0,
            max: 10,
            settingName: "MemNet:Azure:RetryMaxRetries");

        var retryDelayMs = ParseBoundedInt(
            FirstNonEmpty(
                configuration["MemNet:Azure:RetryDelayMs"],
                Environment.GetEnvironmentVariable("MEMNET_AZURE_RETRY_DELAY_MS")),
            defaultValue: 200,
            min: 50,
            max: 5000,
            settingName: "MemNet:Azure:RetryDelayMs");

        var retryMaxDelayMs = ParseBoundedInt(
            FirstNonEmpty(
                configuration["MemNet:Azure:RetryMaxDelayMs"],
                Environment.GetEnvironmentVariable("MEMNET_AZURE_RETRY_MAX_DELAY_MS")),
            defaultValue: 2000,
            min: 100,
            max: 30000,
            settingName: "MemNet:Azure:RetryMaxDelayMs");

        var networkTimeoutSeconds = ParseBoundedInt(
            FirstNonEmpty(
                configuration["MemNet:Azure:NetworkTimeoutSeconds"],
                Environment.GetEnvironmentVariable("MEMNET_AZURE_NETWORK_TIMEOUT_SECONDS")),
            defaultValue: 30,
            min: 5,
            max: 300,
            settingName: "MemNet:Azure:NetworkTimeoutSeconds");

        return new AzureProviderOptions(
            StorageServiceUri: storageServiceUri,
            DocumentsContainerName: documentsContainerName,
            EventsContainerName: eventsContainerName,
            AuditContainerName: auditContainerName,
            SearchEndpoint: searchEndpoint,
            SearchIndexName: searchIndexName,
            SearchApiKey: searchApiKey,
            ManagedIdentityClientId: managedIdentityClientId,
            UseManagedIdentityOnly: useManagedIdentityOnly,
            RetryMaxRetries: retryMaxRetries,
            RetryDelay: TimeSpan.FromMilliseconds(retryDelayMs),
            RetryMaxDelay: TimeSpan.FromMilliseconds(retryMaxDelayMs),
            NetworkTimeout: TimeSpan.FromSeconds(networkTimeoutSeconds));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static int ParseBoundedInt(string? rawValue, int defaultValue, int min, int max, string settingName)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return defaultValue;
        }

        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < min
            || parsed > max)
        {
            throw new InvalidOperationException($"{settingName} must be an integer between {min} and {max}.");
        }

        return parsed;
    }
}

public sealed class AzureClients
{
    public AzureClients(AzureProviderOptions options)
    {
        var credential = CreateCredential(options);
        var blobOptions = new BlobClientOptions();
        ApplyRetryOptions(blobOptions.Retry, options);
        var blobServiceClient = new BlobServiceClient(new Uri(options.StorageServiceUri), credential, blobOptions);

        DocumentsContainer = blobServiceClient.GetBlobContainerClient(options.DocumentsContainerName);
        EventsContainer = blobServiceClient.GetBlobContainerClient(options.EventsContainerName);
        AuditContainer = blobServiceClient.GetBlobContainerClient(options.AuditContainerName);

        if (!string.IsNullOrWhiteSpace(options.SearchEndpoint)
            && !string.IsNullOrWhiteSpace(options.SearchIndexName))
        {
            var searchOptions = new SearchClientOptions();
            ApplyRetryOptions(searchOptions.Retry, options);

            Search = string.IsNullOrWhiteSpace(options.SearchApiKey)
                ? new SearchClient(new Uri(options.SearchEndpoint), options.SearchIndexName, credential, searchOptions)
                : new SearchClient(new Uri(options.SearchEndpoint), options.SearchIndexName, new AzureKeyCredential(options.SearchApiKey), searchOptions);
        }
    }

    public BlobContainerClient DocumentsContainer { get; }

    public BlobContainerClient EventsContainer { get; }

    public BlobContainerClient AuditContainer { get; }

    public SearchClient? Search { get; }

    private static TokenCredential CreateCredential(AzureProviderOptions options)
    {
        if (options.UseManagedIdentityOnly)
        {
            if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
            {
                return new ManagedIdentityCredential(options.ManagedIdentityClientId);
            }

            return new ManagedIdentityCredential();
        }

        var defaultOptions = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
        {
            defaultOptions.ManagedIdentityClientId = options.ManagedIdentityClientId;
        }

        return new DefaultAzureCredential(defaultOptions);
    }

    private static void ApplyRetryOptions(RetryOptions retryOptions, AzureProviderOptions options)
    {
        retryOptions.Mode = RetryMode.Exponential;
        retryOptions.MaxRetries = options.RetryMaxRetries;
        retryOptions.Delay = options.RetryDelay;
        retryOptions.MaxDelay = options.RetryMaxDelay;
        retryOptions.NetworkTimeout = options.NetworkTimeout;
    }
}

internal static class AzureErrorMapper
{
    public static ApiException ToApiException(RequestFailedException ex, string defaultCode, string defaultMessage)
    {
        var status = ex.Status switch
        {
            >= 400 and <= 599 => ex.Status,
            _ => StatusCodes.Status503ServiceUnavailable
        };

        var code = status switch
        {
            StatusCodes.Status412PreconditionFailed => "ETAG_MISMATCH",
            StatusCodes.Status404NotFound => "NOT_FOUND",
            StatusCodes.Status429TooManyRequests => "AZURE_RATE_LIMITED",
            >= 500 => "AZURE_DEPENDENCY_FAILURE",
            _ => defaultCode
        };

        return new ApiException(status, code, string.IsNullOrWhiteSpace(ex.Message) ? defaultMessage : ex.Message);
    }
}
#else
using MemNet.MemoryService.Core;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Infrastructure;

public sealed record AzureProviderOptions(
    string StorageServiceUri,
    string DocumentsContainerName,
    string EventsContainerName,
    string AuditContainerName,
    string? SearchEndpoint,
    string? SearchIndexName,
    string? SearchApiKey,
    string? ManagedIdentityClientId,
    bool UseManagedIdentityOnly,
    int RetryMaxRetries,
    TimeSpan RetryDelay,
    TimeSpan RetryMaxDelay,
    TimeSpan NetworkTimeout)
{
    public static AzureProviderOptions FromConfiguration(IConfiguration configuration)
    {
        return new AzureProviderOptions(
            StorageServiceUri: configuration["MemNet:Azure:StorageServiceUri"] ?? string.Empty,
            DocumentsContainerName: "memnet-documents",
            EventsContainerName: "memnet-events",
            AuditContainerName: "memnet-audit",
            SearchEndpoint: null,
            SearchIndexName: null,
            SearchApiKey: null,
            ManagedIdentityClientId: null,
            UseManagedIdentityOnly: false,
            RetryMaxRetries: 3,
            RetryDelay: TimeSpan.FromMilliseconds(200),
            RetryMaxDelay: TimeSpan.FromMilliseconds(2000),
            NetworkTimeout: TimeSpan.FromSeconds(30));
    }
}

public sealed class AzureClients
{
    public AzureClients(AzureProviderOptions options)
    {
        _ = options;
        throw new ApiException(
            StatusCodes.Status501NotImplemented,
            "AZURE_PROVIDER_NOT_ENABLED",
            "Azure provider build flag is disabled. Rebuild with /p:MemNetEnableAzureSdk=true to enable Azure SDK providers.");
    }
}

internal static class AzureErrorMapper
{
    public static ApiException ToApiException(Exception ex, string defaultCode, string defaultMessage)
    {
        return new ApiException(StatusCodes.Status503ServiceUnavailable, defaultCode, string.IsNullOrWhiteSpace(ex.Message) ? defaultMessage : ex.Message);
    }
}
#endif
