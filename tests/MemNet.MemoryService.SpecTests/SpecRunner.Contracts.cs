using System.Text.Json.Nodes;
using MemNet.MemoryService.Core;
using MemNet.MemoryService.Infrastructure;

internal sealed partial class SpecRunner
{
    private static async Task FilesystemStoreContractsAreConsistentAsync()
    {
        using var scope = TestScope.Create();
        var options = scope.CreateStorageOptions();

        await RunDocumentStoreContractAsync(new FileDocumentStore(options));
        await RunEventStoreContractAsync(new FileEventStore(options), scope.Keys.Tenant, scope.Keys.User);
        await RunAuditStoreContractAsync(
            new FileAuditStore(options),
            scope.Keys.Tenant,
            scope.Keys.User,
            changeId =>
            {
                var expectedPath = Path.Combine(options.DataRoot, "tenants", scope.Keys.Tenant, "users", scope.Keys.User, "audit", $"{changeId}.json");
                return File.Exists(expectedPath);
            });
    }

    private static async Task RunDocumentStoreContractAsync(IDocumentStore documentStore)
    {
        var key = new DocumentKey("tenant-contract", "user-contract", "user/contract.json");
        var now = DateTimeOffset.UtcNow;
        var initial = new DocumentEnvelope(
            DocId: "doc-contract",
            SchemaId: "memory.contract",
            SchemaVersion: "1.0.0",
            CreatedAt: now,
            UpdatedAt: now,
            UpdatedBy: "spec-tests",
            Content: new JsonObject { ["value"] = "v1" });

        var created = await documentStore.UpsertAsync(key, initial, "*");
        Assert.True(!string.IsNullOrWhiteSpace(created.ETag), "Document contract expected non-empty ETag.");
        Assert.True(await documentStore.ExistsAsync(key), "Document contract expected ExistsAsync=true after create.");

        var fetched = await documentStore.GetAsync(key) ?? throw new Exception("Document contract expected fetched document.");
        Assert.Equal("v1", fetched.Envelope.Content["value"]?.GetValue<string>());

        await Assert.ThrowsAsync<ApiException>(
            async () =>
            {
                await documentStore.UpsertAsync(
                    key,
                    fetched.Envelope with { Content = new JsonObject { ["value"] = "v2-stale" } },
                    ifMatch: "\"stale\"");
            },
            ex => ex.StatusCode == 412 && ex.Code == "ETAG_MISMATCH");

        var updatedEnvelope = fetched.Envelope with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Content = new JsonObject { ["value"] = "v2" }
        };
        var updated = await documentStore.UpsertAsync(key, updatedEnvelope, fetched.ETag);
        Assert.True(!string.Equals(updated.ETag, fetched.ETag, StringComparison.Ordinal), "Document contract expected ETag change on update.");
    }

    private static async Task RunEventStoreContractAsync(IEventStore eventStore, string tenantId, string userId)
    {
        await eventStore.WriteAsync(new EventDigest(
            EventId: "evt-contract-1",
            TenantId: tenantId,
            UserId: userId,
            ServiceId: "contract-tests",
            Timestamp: DateTimeOffset.UtcNow.AddMinutes(-5),
            SourceType: "chat",
            Digest: "Event contract baseline for retrieval latency.",
            Keywords: ["retrieval", "latency"],
            ProjectIds: ["project-contract"],
            SnapshotUri: "blob://contract/1",
            Evidence: new EventEvidence(["m1"], 1, 1)));

        await eventStore.WriteAsync(new EventDigest(
            EventId: "evt-contract-2",
            TenantId: tenantId,
            UserId: userId,
            ServiceId: "contract-tests",
            Timestamp: DateTimeOffset.UtcNow,
            SourceType: "chat",
            Digest: "Unrelated event digest.",
            Keywords: ["general"],
            ProjectIds: ["project-other"],
            SnapshotUri: "blob://contract/2",
            Evidence: new EventEvidence(["m2"], 1, 2)));

        var filtered = await eventStore.QueryAsync(
            tenantId,
            userId,
            new EventSearchRequest(
                Query: "latency",
                ServiceId: "contract-tests",
                SourceType: "chat",
                ProjectId: "project-contract",
                From: null,
                To: null,
                TopK: 5));

        Assert.True(filtered.Count == 1, "Event contract expected exactly one filtered event.");
        Assert.Equal("evt-contract-1", filtered[0].EventId);
    }

    private static async Task RunAuditStoreContractAsync(
        IAuditStore auditStore,
        string tenantId,
        string userId,
        Func<string, bool> auditRecordExists)
    {
        const string changeId = "chg-contract-1";

        var record = new AuditRecord(
            ChangeId: changeId,
            Actor: "contract-tests",
            TenantId: tenantId,
            UserId: userId,
            Path: "user/long_term_memory.json",
            PreviousETag: "\"prev\"",
            NewETag: "\"new\"",
            Reason: "contract-check",
            Ops: [new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("contract"))],
            Timestamp: DateTimeOffset.UtcNow,
            EvidenceMessageIds: ["m-audit-1"]);

        await auditStore.WriteAsync(record);
        Assert.True(auditRecordExists(changeId), "Audit contract expected persisted audit record.");
    }
}
