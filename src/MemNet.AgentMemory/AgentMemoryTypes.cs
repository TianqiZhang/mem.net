using System.Text.Json.Nodes;

namespace MemNet.AgentMemory;

public sealed class AgentMemoryOptions
{
    public int DefaultAssembleMaxDocs { get; set; } = 8;

    public int DefaultAssembleMaxCharsTotal { get; set; } = 40_000;

    public int DefaultRecallTopK { get; set; } = 8;
}

public sealed record PrepareTurnRequest(
    string? RecallQuery,
    int? RecallTopK = null,
    IReadOnlyList<string>? AdditionalSlotIds = null,
    IReadOnlyDictionary<string, string>? TemplateVariables = null,
    string? ServiceId = null,
    string? SourceType = null,
    string? ProjectId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int? MaxDocs = null,
    int? MaxCharsTotal = null);

public sealed record PreparedMemory(
    IReadOnlyList<PreparedSlotDocument> Documents,
    IReadOnlyList<MemNet.Client.EventDigest> Events,
    IReadOnlyList<MemNet.Client.DroppedFile> DroppedFiles);

public sealed record PreparedSlotDocument(
    string? SlotId,
    MemNet.Client.FileRef File,
    string ETag,
    MemNet.Client.DocumentEnvelope Envelope);

public sealed record SlotPatchRequest(
    IReadOnlyList<MemNet.Client.PatchOperation> Ops,
    string Reason,
    JsonNode? Evidence = null,
    IReadOnlyDictionary<string, string>? TemplateVariables = null);

public sealed record SlotReplaceRequest(
    MemNet.Client.DocumentEnvelope Document,
    string Reason,
    JsonNode? Evidence = null,
    IReadOnlyDictionary<string, string>? TemplateVariables = null);

public sealed record RecallRequest(
    string? Query,
    int? TopK = null,
    string? ServiceId = null,
    string? SourceType = null,
    string? ProjectId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

public sealed record RememberRequest(MemNet.Client.EventDigest Event);

public sealed record MemoryFile(
    string Path,
    string ContentType,
    string Content,
    string ETag);

public sealed record MemoryFileListItem(
    string Path,
    DateTimeOffset LastModifiedUtc);

public sealed record MemoryPatchEdit(
    string OldText,
    string NewText,
    int? Occurrence = null);
