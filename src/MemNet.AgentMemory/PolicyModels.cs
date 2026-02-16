using System.Text.Json;

namespace MemNet.AgentMemory;

public sealed record AgentMemoryPolicy(
    string PolicyId,
    IReadOnlyList<MemorySlotPolicy> Slots)
{
    public static AgentMemoryPolicy LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var policy = JsonSerializer.Deserialize<AgentMemoryPolicy>(json, PolicyJson.Options);
        return policy ?? throw new MemNet.Client.MemNetException($"Failed to deserialize policy file '{path}'.");
    }
}

public sealed record MemorySlotPolicy(
    string SlotId,
    string? Path,
    string? PathTemplate,
    bool LoadByDefault = false,
    SlotPatchRules? PatchRules = null);

public sealed record SlotPatchRules(
    IReadOnlyList<string>? AllowedPaths = null,
    IReadOnlyList<string>? RequiredContentPaths = null,
    int? MaxContentChars = null,
    int? MaxArrayItems = null);

internal static class PolicyJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
}
