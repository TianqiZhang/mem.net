using System.Text.Json.Nodes;
using MemNet.Client;

namespace MemNet.Sdk.IntegrationTests;

internal static class SdkTestData
{
    public static MemNetClient CreateClient(HttpClient httpClient, string serviceId = "sdk-tests")
    {
        return new MemNetClient(new MemNetClientOptions
        {
            HttpClient = httpClient,
            ServiceId = serviceId
        });
    }

    public static async Task SeedDefaultUserFilesAsync(MemNetClient client, MemNetScope scope)
    {
        await SeedFileAsync(
            client,
            scope,
            "user/profile.json",
            new JsonObject
            {
                ["profile"] = new JsonObject
                {
                    ["display_name"] = "Test User",
                    ["locale"] = "en-US"
                },
                ["projects"] = new JsonArray("project-a")
            });

        await SeedFileAsync(
            client,
            scope,
            "user/long_term_memory.json",
            new JsonObject
            {
                ["preferences"] = new JsonArray("concise"),
                ["durable_facts"] = new JsonArray(),
                ["pending_confirmations"] = new JsonArray()
            });
    }

    public static Task SeedFileAsync(
        MemNetClient client,
        MemNetScope scope,
        string path,
        JsonObject content)
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = new DocumentEnvelope(
            DocId: $"doc-{Guid.NewGuid():N}",
            SchemaId: "memnet.file",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "seed",
            Content: content);

        return client.WriteFileAsync(
            scope,
            new FileRef(path),
            new ReplaceDocumentRequest(envelope, "seed"),
            ifMatch: "*");
    }
}
