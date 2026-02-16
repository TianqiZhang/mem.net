using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Storage.Blobs;

namespace MemNet.Bootstrap;

public static class CliEntryPoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!CliOptions.TryParse(args, out var options, out var parseError))
        {
            if (!string.IsNullOrWhiteSpace(parseError))
            {
                Console.Error.WriteLine($"ERROR: {parseError}");
            }

            PrintUsage(parseError is null ? Console.Out : Console.Error);
            return parseError is null ? 0 : 1;
        }

        if (!string.Equals(options.Provider, "azure", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"ERROR: Unsupported provider '{options.Provider}'. Only 'azure' is supported.");
            return 1;
        }

        try
        {
            var bootstrapOptions = AzureBootstrapOptions.FromEnvironment(options.SearchSchemaPath, Directory.GetCurrentDirectory());
            var bootstrapper = new AzureBootstrapper(bootstrapOptions);
            var success = options.Mode switch
            {
                BootstrapMode.Check => await bootstrapper.CheckAsync(),
                BootstrapMode.Apply => await bootstrapper.ApplyAsync(),
                _ => throw new InvalidOperationException($"Unsupported mode '{options.Mode}'.")
            };

            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("mem.net bootstrap tool");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  dotnet run --project tools/MemNet.Bootstrap -- azure --check");
        writer.WriteLine("  dotnet run --project tools/MemNet.Bootstrap -- azure --apply");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --check            Validate Azure containers and search index (default mode)");
        writer.WriteLine("  --apply            Create/update Azure containers and search index");
        writer.WriteLine("  --schema <path>    Override search schema file path");
        writer.WriteLine("  --help             Show this help");
        writer.WriteLine();
        writer.WriteLine("Environment variables:");
        writer.WriteLine("  MEMNET_AZURE_STORAGE_SERVICE_URI          (required)");
        writer.WriteLine("  MEMNET_AZURE_DOCUMENTS_CONTAINER          (default: memnet-documents)");
        writer.WriteLine("  MEMNET_AZURE_EVENTS_CONTAINER             (default: memnet-events)");
        writer.WriteLine("  MEMNET_AZURE_AUDIT_CONTAINER              (default: memnet-audit)");
        writer.WriteLine("  MEMNET_AZURE_SEARCH_ENDPOINT              (optional)");
        writer.WriteLine("  MEMNET_AZURE_SEARCH_INDEX                 (optional)");
        writer.WriteLine("  MEMNET_AZURE_SEARCH_API_KEY               (optional)");
        writer.WriteLine("  MEMNET_AZURE_MANAGED_IDENTITY_CLIENT_ID   (optional)");
        writer.WriteLine("  MEMNET_AZURE_USE_MANAGED_IDENTITY_ONLY    (optional, true/false)");
        writer.WriteLine("  MEMNET_AZURE_SEARCH_SCHEMA_PATH           (optional)");
    }
}

public enum BootstrapMode
{
    Check,
    Apply
}

public sealed record CliOptions(string Provider, BootstrapMode Mode, string? SearchSchemaPath)
{
    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        var provider = "azure";
        var mode = BootstrapMode.Check;
        var sawCheck = false;
        var sawApply = false;
        string? schemaPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    options = new CliOptions(provider, mode, schemaPath);
                    error = null;
                    return false;
                case "--check":
                    sawCheck = true;
                    mode = BootstrapMode.Check;
                    break;
                case "--apply":
                    sawApply = true;
                    mode = BootstrapMode.Apply;
                    break;
                case "--schema":
                    if (i + 1 >= args.Length)
                    {
                        options = default!;
                        error = "--schema requires a file path argument.";
                        return false;
                    }

                    schemaPath = args[++i];
                    break;
                default:
                    if (!arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        provider = arg;
                        break;
                    }

                    options = default!;
                    error = $"Unknown option '{arg}'.";
                    return false;
            }
        }

        if (sawCheck && sawApply)
        {
            options = default!;
            error = "--check and --apply are mutually exclusive.";
            return false;
        }

        options = new CliOptions(provider, mode, schemaPath);
        error = null;
        return true;
    }
}

public sealed record AzureBootstrapOptions(
    string StorageServiceUri,
    string DocumentsContainerName,
    string EventsContainerName,
    string AuditContainerName,
    string? SearchEndpoint,
    string? SearchIndexName,
    string? SearchApiKey,
    string? ManagedIdentityClientId,
    bool UseManagedIdentityOnly,
    string SearchSchemaPath,
    int RetryMaxRetries,
    TimeSpan RetryDelay,
    TimeSpan RetryMaxDelay,
    TimeSpan NetworkTimeout)
{
    public bool SearchConfigured => !string.IsNullOrWhiteSpace(SearchEndpoint) && !string.IsNullOrWhiteSpace(SearchIndexName);

    public static AzureBootstrapOptions FromEnvironment(string? searchSchemaOverride, string currentDirectory)
    {
        var storageServiceUri = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MEMNET_AZURE_STORAGE_SERVICE_URI"),
            Environment.GetEnvironmentVariable("MemNet__Azure__StorageServiceUri"));

        if (string.IsNullOrWhiteSpace(storageServiceUri))
        {
            throw new InvalidOperationException("Bootstrap requires MEMNET_AZURE_STORAGE_SERVICE_URI.");
        }

        var documentsContainerName = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MEMNET_AZURE_DOCUMENTS_CONTAINER"),
            Environment.GetEnvironmentVariable("MemNet__Azure__DocumentsContainerName"),
            "memnet-documents") ?? "memnet-documents";

        var eventsContainerName = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MEMNET_AZURE_EVENTS_CONTAINER"),
            Environment.GetEnvironmentVariable("MemNet__Azure__EventsContainerName"),
            "memnet-events") ?? "memnet-events";

        var auditContainerName = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MEMNET_AZURE_AUDIT_CONTAINER"),
            Environment.GetEnvironmentVariable("MemNet__Azure__AuditContainerName"),
            "memnet-audit") ?? "memnet-audit";

        var searchEndpoint = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_ENDPOINT"),
            Environment.GetEnvironmentVariable("MemNet__Azure__SearchEndpoint"));

        var searchIndexName = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_INDEX"),
            Environment.GetEnvironmentVariable("MemNet__Azure__SearchIndexName"));

        var searchApiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_API_KEY"),
            Environment.GetEnvironmentVariable("MemNet__Azure__SearchApiKey"));

        var managedIdentityClientId = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MEMNET_AZURE_MANAGED_IDENTITY_CLIENT_ID"),
            Environment.GetEnvironmentVariable("MemNet__Azure__ManagedIdentityClientId"));

        var useManagedIdentityOnly = bool.TryParse(
                FirstNonEmpty(
                    Environment.GetEnvironmentVariable("MEMNET_AZURE_USE_MANAGED_IDENTITY_ONLY"),
                    Environment.GetEnvironmentVariable("MemNet__Azure__UseManagedIdentityOnly")),
                out var parsed)
            && parsed;

        if (!Uri.TryCreate(storageServiceUri, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("MEMNET_AZURE_STORAGE_SERVICE_URI must be an absolute URI.");
        }

        var hasSearchEndpoint = !string.IsNullOrWhiteSpace(searchEndpoint);
        var hasSearchIndex = !string.IsNullOrWhiteSpace(searchIndexName);
        if (hasSearchEndpoint ^ hasSearchIndex)
        {
            throw new InvalidOperationException("MEMNET_AZURE_SEARCH_ENDPOINT and MEMNET_AZURE_SEARCH_INDEX must be configured together.");
        }

        if (hasSearchEndpoint && !Uri.TryCreate(searchEndpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("MEMNET_AZURE_SEARCH_ENDPOINT must be an absolute URI.");
        }

        var retryMaxRetries = ParseBoundedInt(
            FirstNonEmpty(Environment.GetEnvironmentVariable("MEMNET_AZURE_RETRY_MAX_RETRIES")),
            defaultValue: 3,
            min: 0,
            max: 10,
            settingName: "MEMNET_AZURE_RETRY_MAX_RETRIES");

        var retryDelayMs = ParseBoundedInt(
            FirstNonEmpty(Environment.GetEnvironmentVariable("MEMNET_AZURE_RETRY_DELAY_MS")),
            defaultValue: 200,
            min: 50,
            max: 5000,
            settingName: "MEMNET_AZURE_RETRY_DELAY_MS");

        var retryMaxDelayMs = ParseBoundedInt(
            FirstNonEmpty(Environment.GetEnvironmentVariable("MEMNET_AZURE_RETRY_MAX_DELAY_MS")),
            defaultValue: 2000,
            min: 100,
            max: 30000,
            settingName: "MEMNET_AZURE_RETRY_MAX_DELAY_MS");

        var networkTimeoutSeconds = ParseBoundedInt(
            FirstNonEmpty(Environment.GetEnvironmentVariable("MEMNET_AZURE_NETWORK_TIMEOUT_SECONDS")),
            defaultValue: 30,
            min: 5,
            max: 300,
            settingName: "MEMNET_AZURE_NETWORK_TIMEOUT_SECONDS");

        var schemaPath = FirstNonEmpty(
                             searchSchemaOverride,
                             Environment.GetEnvironmentVariable("MEMNET_AZURE_SEARCH_SCHEMA_PATH"))
                         ?? Path.Combine(currentDirectory, "infra", "search", "events-index.schema.json");

        return new AzureBootstrapOptions(
            StorageServiceUri: storageServiceUri,
            DocumentsContainerName: documentsContainerName,
            EventsContainerName: eventsContainerName,
            AuditContainerName: auditContainerName,
            SearchEndpoint: searchEndpoint,
            SearchIndexName: searchIndexName,
            SearchApiKey: searchApiKey,
            ManagedIdentityClientId: managedIdentityClientId,
            UseManagedIdentityOnly: useManagedIdentityOnly,
            SearchSchemaPath: Path.GetFullPath(schemaPath),
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

public sealed class AzureBootstrapper
{
    private readonly AzureBootstrapOptions _options;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly SearchIndexClient? _searchIndexClient;

    public AzureBootstrapper(AzureBootstrapOptions options)
    {
        _options = options;

        var credential = CreateCredential(options);

        var blobClientOptions = new BlobClientOptions();
        ApplyRetryOptions(blobClientOptions.Retry, options);
        _blobServiceClient = new BlobServiceClient(new Uri(options.StorageServiceUri), credential, blobClientOptions);

        if (options.SearchConfigured)
        {
            var searchOptions = new SearchClientOptions();
            ApplyRetryOptions(searchOptions.Retry, options);
            _searchIndexClient = string.IsNullOrWhiteSpace(options.SearchApiKey)
                ? new SearchIndexClient(new Uri(options.SearchEndpoint!), credential, searchOptions)
                : new SearchIndexClient(new Uri(options.SearchEndpoint!), new AzureKeyCredential(options.SearchApiKey), searchOptions);
        }
    }

    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        await CheckContainerAsync("documents", _options.DocumentsContainerName, errors, cancellationToken);
        await CheckContainerAsync("events", _options.EventsContainerName, errors, cancellationToken);
        await CheckContainerAsync("audit", _options.AuditContainerName, errors, cancellationToken);

        if (_searchIndexClient is not null)
        {
            await CheckSearchIndexAsync(errors, cancellationToken);
        }

        if (errors.Count == 0)
        {
            Console.WriteLine("CHECK_OK: Azure bootstrap dependencies are ready.");
            return true;
        }

        foreach (var error in errors)
        {
            Console.Error.WriteLine($"CHECK_FAIL: {error}");
        }

        return false;
    }

    public async Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureContainerAsync(_options.DocumentsContainerName, cancellationToken);
            await EnsureContainerAsync(_options.EventsContainerName, cancellationToken);
            await EnsureContainerAsync(_options.AuditContainerName, cancellationToken);

            if (_searchIndexClient is not null)
            {
                var schema = SearchIndexSchemaDocument.Load(_options.SearchSchemaPath);
                var index = schema.ToSearchIndex(_options.SearchIndexName!);
                await _searchIndexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken);
                Console.WriteLine($"APPLY_OK: Search index '{_options.SearchIndexName}' is created/updated.");
            }
            else
            {
                Console.WriteLine("APPLY_INFO: Search endpoint/index not configured; skipping index provisioning.");
            }

            Console.WriteLine("APPLY_OK: Azure bootstrap completed.");
            return true;
        }
        catch (RequestFailedException ex)
        {
            Console.Error.WriteLine($"APPLY_FAIL: Azure request failed ({ex.Status}): {ex.Message}");
            return false;
        }
    }

    private async Task CheckContainerAsync(
        string logicalName,
        string containerName,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            var container = _blobServiceClient.GetBlobContainerClient(containerName);
            var exists = await container.ExistsAsync(cancellationToken);
            if (!exists.Value)
            {
                errors.Add($"Blob container '{containerName}' ({logicalName}) does not exist.");
            }
            else
            {
                Console.WriteLine($"CHECK_OK: Blob container '{containerName}' exists.");
            }
        }
        catch (RequestFailedException ex)
        {
            errors.Add($"Failed to check blob container '{containerName}': ({ex.Status}) {ex.Message}");
        }
    }

    private async Task CheckSearchIndexAsync(List<string> errors, CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.SearchSchemaPath))
        {
            errors.Add($"Search schema file not found: {_options.SearchSchemaPath}");
            return;
        }

        SearchIndexSchemaDocument schema;
        try
        {
            schema = SearchIndexSchemaDocument.Load(_options.SearchSchemaPath);
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to load search schema file '{_options.SearchSchemaPath}': {ex.Message}");
            return;
        }

        try
        {
            var response = await _searchIndexClient!.GetIndexAsync(_options.SearchIndexName!, cancellationToken);
            var indexErrors = schema.ValidateExistingIndex(response.Value);
            if (indexErrors.Count == 0)
            {
                Console.WriteLine($"CHECK_OK: Search index '{_options.SearchIndexName}' exists and matches schema.");
            }
            else
            {
                foreach (var indexError in indexErrors)
                {
                    errors.Add($"Search index '{_options.SearchIndexName}': {indexError}");
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            errors.Add($"Search index '{_options.SearchIndexName}' does not exist.");
        }
        catch (RequestFailedException ex)
        {
            errors.Add($"Failed to check search index '{_options.SearchIndexName}': ({ex.Status}) {ex.Message}");
        }
    }

    private async Task EnsureContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        var container = _blobServiceClient.GetBlobContainerClient(containerName);
        var response = await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        if (response is null)
        {
            Console.WriteLine($"APPLY_INFO: Blob container '{containerName}' already exists.");
            return;
        }

        Console.WriteLine($"APPLY_OK: Blob container '{containerName}' created.");
    }

    private static TokenCredential CreateCredential(AzureBootstrapOptions options)
    {
        if (options.UseManagedIdentityOnly)
        {
            return string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
                ? new ManagedIdentityCredential()
                : new ManagedIdentityCredential(options.ManagedIdentityClientId);
        }

        var defaultOptions = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
        {
            defaultOptions.ManagedIdentityClientId = options.ManagedIdentityClientId;
        }

        return new DefaultAzureCredential(defaultOptions);
    }

    private static void ApplyRetryOptions(RetryOptions retryOptions, AzureBootstrapOptions options)
    {
        retryOptions.Mode = RetryMode.Exponential;
        retryOptions.MaxRetries = options.RetryMaxRetries;
        retryOptions.Delay = options.RetryDelay;
        retryOptions.MaxDelay = options.RetryMaxDelay;
        retryOptions.NetworkTimeout = options.NetworkTimeout;
    }
}

public sealed class SearchIndexSchemaDocument
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("fields")]
    public List<SearchIndexFieldDefinition> Fields { get; init; } = [];

    public static SearchIndexSchemaDocument Load(string schemaPath)
    {
        var json = File.ReadAllText(schemaPath);
        var schema = JsonSerializer.Deserialize<SearchIndexSchemaDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Schema file is empty or invalid JSON.");

        if (schema.Fields.Count == 0)
        {
            throw new InvalidOperationException("Schema must define at least one field.");
        }

        var duplicate = schema.Fields
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Schema contains duplicate field '{duplicate.Key}'.");
        }

        return schema;
    }

    public SearchIndex ToSearchIndex(string indexName)
    {
        var fields = Fields.Select(ToSearchField).ToList();
        var index = new SearchIndex(indexName, fields)
        {
            Description = Description
        };

        return index;
    }

    public IReadOnlyList<string> ValidateExistingIndex(SearchIndex actual)
    {
        var errors = new List<string>();
        var actualByName = actual.Fields.ToDictionary(f => f.Name, f => f, StringComparer.Ordinal);

        foreach (var expected in Fields)
        {
            if (!actualByName.TryGetValue(expected.Name, out var actualField))
            {
                errors.Add($"Missing required field '{expected.Name}'.");
                continue;
            }

            var expectedType = ParseDataType(expected.Type);
            if (!Equals(actualField.Type, expectedType))
            {
                errors.Add($"Field '{expected.Name}' has type '{actualField.Type}', expected '{expectedType}'.");
            }

            CompareIfSet("is_key", expected.IsKey, actualField.IsKey, expected.Name, errors);
            CompareIfSet("is_filterable", expected.IsFilterable, actualField.IsFilterable, expected.Name, errors);
            CompareIfSet("is_searchable", expected.IsSearchable, actualField.IsSearchable, expected.Name, errors);
            CompareIfSet("is_sortable", expected.IsSortable, actualField.IsSortable, expected.Name, errors);
            CompareIfSet("is_facetable", expected.IsFacetable, actualField.IsFacetable, expected.Name, errors);
            CompareIfSet("is_hidden", expected.IsHidden, actualField.IsHidden, expected.Name, errors);
        }

        return errors;
    }

    private static void CompareIfSet(string propertyName, bool? expected, bool? actual, string fieldName, List<string> errors)
    {
        if (!expected.HasValue)
        {
            return;
        }

        if (expected.Value != actual)
        {
            errors.Add($"Field '{fieldName}' has {propertyName}={actual}, expected {expected.Value}.");
        }
    }

    private static SearchField ToSearchField(SearchIndexFieldDefinition field)
    {
        if (string.IsNullOrWhiteSpace(field.Name))
        {
            throw new InvalidOperationException("Schema contains a field with an empty name.");
        }

        var searchField = new SearchField(field.Name, ParseDataType(field.Type));

        if (field.IsKey.HasValue)
        {
            searchField.IsKey = field.IsKey.Value;
        }

        if (field.IsFilterable.HasValue)
        {
            searchField.IsFilterable = field.IsFilterable.Value;
        }

        if (field.IsSearchable.HasValue)
        {
            searchField.IsSearchable = field.IsSearchable.Value;
        }

        if (field.IsSortable.HasValue)
        {
            searchField.IsSortable = field.IsSortable.Value;
        }

        if (field.IsFacetable.HasValue)
        {
            searchField.IsFacetable = field.IsFacetable.Value;
        }

        if (field.IsHidden.HasValue)
        {
            searchField.IsHidden = field.IsHidden.Value;
        }

        return searchField;
    }

    private static SearchFieldDataType ParseDataType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Field type must be non-empty.");
        }

        if (raw.StartsWith("Collection(", StringComparison.Ordinal) && raw.EndsWith(')'))
        {
            var inner = raw["Collection(".Length..^1];
            return SearchFieldDataType.Collection(ParseDataType(inner));
        }

        return raw switch
        {
            "Edm.String" => SearchFieldDataType.String,
            "Edm.Int32" => SearchFieldDataType.Int32,
            "Edm.Int64" => SearchFieldDataType.Int64,
            "Edm.Double" => SearchFieldDataType.Double,
            "Edm.Boolean" => SearchFieldDataType.Boolean,
            "Edm.DateTimeOffset" => SearchFieldDataType.DateTimeOffset,
            _ => throw new InvalidOperationException($"Unsupported field data type '{raw}'.")
        };
    }
}

public sealed class SearchIndexFieldDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("is_key")]
    public bool? IsKey { get; init; }

    [JsonPropertyName("is_filterable")]
    public bool? IsFilterable { get; init; }

    [JsonPropertyName("is_searchable")]
    public bool? IsSearchable { get; init; }

    [JsonPropertyName("is_sortable")]
    public bool? IsSortable { get; init; }

    [JsonPropertyName("is_facetable")]
    public bool? IsFacetable { get; init; }

    [JsonPropertyName("is_hidden")]
    public bool? IsHidden { get; init; }
}
