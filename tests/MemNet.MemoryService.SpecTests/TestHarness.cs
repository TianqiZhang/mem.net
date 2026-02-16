using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using MemNet.MemoryService.Core;
using MemNet.MemoryService.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
        {
            throw new Exception($"Expected '{expected}' but got '{actual}'.");
        }
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action, Func<TException, bool> predicate)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException ex)
        {
            if (!predicate(ex))
            {
                throw new Exception($"Exception predicate failed for '{typeof(TException).Name}'.");
            }

            return;
        }

        throw new Exception($"Expected exception '{typeof(TException).Name}' was not thrown.");
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception($"Expected exception '{typeof(TException).Name}' was not thrown.");
    }
}

internal sealed class TestScope : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _dataRoot;

    public TestScope(
        string repoRoot,
        string dataRoot,
        IDocumentStore documentStore,
        MemoryCoordinator coordinator,
        TestKeys keys)
    {
        _repoRoot = repoRoot;
        _dataRoot = dataRoot;
        DocumentStore = documentStore;
        Coordinator = coordinator;
        Keys = keys;
    }

    public IDocumentStore DocumentStore { get; }

    public MemoryCoordinator Coordinator { get; }

    public TestKeys Keys { get; }

    public string RepoRoot => _repoRoot;

    public string DataRoot => _dataRoot;

    public static TestScope Create()
    {
        var repoRoot = Directory.GetCurrentDirectory();
        var dataRoot = Path.Combine(Path.GetTempPath(), "memnet-spec-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        var options = new StorageOptions
        {
            DataRoot = dataRoot
        };

        var documentStore = new FileDocumentStore(options);
        var eventStore = new FileEventStore(options);
        var auditStore = new FileAuditStore(options);
        var coordinator = new MemoryCoordinator(
            documentStore,
            eventStore,
            auditStore,
            NullLogger<MemoryCoordinator>.Instance);

        var keys = new TestKeys("tenant-1", "user-1");
        SeedDocuments(documentStore, keys).GetAwaiter().GetResult();

        return new TestScope(repoRoot, dataRoot, documentStore, coordinator, keys);
    }

    public StorageOptions CreateStorageOptions()
    {
        return new StorageOptions
        {
            DataRoot = _dataRoot
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static async Task SeedDocuments(IDocumentStore documentStore, TestKeys keys)
    {
        var now = DateTimeOffset.UtcNow;

        var userProfile = new DocumentEnvelope(
            DocId: Guid.NewGuid().ToString("N"),
            SchemaId: "memory.user.profile",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "seed",
            Content: new JsonObject
            {
                ["profile"] = new JsonObject
                {
                    ["display_name"] = "Test User",
                    ["locale"] = "en-US"
                },
                ["projects_index"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["project_id"] = "project-alpha",
                        ["aliases"] = new JsonArray("alpha"),
                        ["keywords"] = new JsonArray("retrieval", "latency")
                    }
                }
            });

        var longTermMemory = new DocumentEnvelope(
            DocId: Guid.NewGuid().ToString("N"),
            SchemaId: "memory.user.long_term_memory",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "seed",
            Content: new JsonObject
            {
                ["preferences"] = new JsonArray("Keep responses concise."),
                ["durable_facts"] = new JsonArray(),
                ["pending_confirmations"] = new JsonArray()
            });

        var projectDoc = new DocumentEnvelope(
            DocId: Guid.NewGuid().ToString("N"),
            SchemaId: "memory.project",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "seed",
            Content: new JsonObject
            {
                ["summary"] = new JsonArray("Project alpha focuses on retrieval quality."),
                ["facets"] = new JsonObject
                {
                    ["architecture"] = new JsonArray("api", "search")
                },
                ["recent_notes"] = new JsonArray("Tune topK for latency")
            });

        await documentStore.UpsertAsync(keys.UserProfile, userProfile, "*", default);
        await documentStore.UpsertAsync(keys.LongTermMemory, longTermMemory, "*", default);
        await documentStore.UpsertAsync(keys.ProjectAlpha, projectDoc, "*", default);
    }
}

internal sealed class ServiceHost : IDisposable
{
    private readonly Process _process;

    private ServiceHost(Process process, Uri baseAddress)
    {
        _process = process;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public static async Task<ServiceHost> StartAsync(
        string repoRoot,
        string dataRoot,
        string provider = "filesystem",
        IReadOnlyDictionary<string, string?>? additionalEnvironment = null,
        string? serviceDllPath = null)
    {
        var port = ReserveFreePort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}");
        var baseAddressForHosting = $"http://127.0.0.1:{port}";
        var serviceDll = serviceDllPath;
        if (string.IsNullOrWhiteSpace(serviceDll))
        {
            var candidates = new[]
            {
                Path.Combine(repoRoot, "src", "MemNet.MemoryService", "bin", "Debug", "net8.0", "MemNet.MemoryService.dll"),
                Path.Combine(repoRoot, "src", "MemNet.MemoryService", "bin", "Debug", "azure", "net8.0", "MemNet.MemoryService.dll")
            };
            serviceDll = candidates.FirstOrDefault(File.Exists);
        }

        if (!File.Exists(serviceDll))
        {
            throw new Exception($"Service DLL not found for integration host: {serviceDll}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{serviceDll}\"",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.Environment["ASPNETCORE_URLS"] = baseAddressForHosting;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["MEMNET_DATA_ROOT"] = dataRoot;
        startInfo.Environment["MEMNET_PROVIDER"] = provider;

        if (additionalEnvironment is not null)
        {
            foreach (var pair in additionalEnvironment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();

        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                var stdout = await process.StandardOutput.ReadToEndAsync();
                throw new Exception($"Service process exited early. stdout: {stdout} stderr: {stderr}");
            }

            try
            {
                var response = await client.GetAsync(new Uri(baseAddress, "/"));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return new ServiceHost(process, baseAddress);
                }
            }
            catch
            {
                // retry until deadline
            }

            await Task.Delay(200);
        }

        process.Kill(true);
        var timeoutStdErr = await process.StandardError.ReadToEndAsync();
        var timeoutStdOut = await process.StandardOutput.ReadToEndAsync();
        throw new Exception($"Service host did not become healthy before timeout. stdout: {timeoutStdOut} stderr: {timeoutStdErr}");
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
            // best-effort cleanup
        }

        _process.Dispose();
    }

    private static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed record TestKeys(string Tenant, string User)
{
    public DocumentKey UserProfile => new(Tenant, User, "user/profile.json");

    public DocumentKey LongTermMemory => new(Tenant, User, "user/long_term_memory.json");

    public DocumentKey ProjectAlpha => new(Tenant, User, "projects/project-alpha.json");
}
