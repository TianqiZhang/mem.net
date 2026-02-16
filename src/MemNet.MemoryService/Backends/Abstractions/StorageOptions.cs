using System.Text.Json;

namespace MemNet.MemoryService.Infrastructure;

public sealed class StorageOptions
{
    public required string DataRoot { get; init; }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}
