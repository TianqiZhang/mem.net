using MemNet.AgentMemory;
using MemNet.Client;

internal static class TurnDigestWriter
{
    public static async Task WriteAsync(AgentMemory memory, MemNetScope scope, string serviceId, string userText, string assistantText)
    {
        var digest = $"User: {Clamp(userText.Trim(), 280)} | Assistant: {Clamp(assistantText.Trim(), 320)}";
        var keywords = BuildKeywords(userText).ToArray();

        var evt = new EventDigest(
            EventId: Guid.NewGuid().ToString("N"),
            TenantId: scope.TenantId,
            UserId: scope.UserId,
            ServiceId: serviceId,
            Timestamp: DateTimeOffset.UtcNow,
            SourceType: "chat.turn",
            Digest: digest,
            Keywords: keywords,
            ProjectIds: Array.Empty<string>(),
            Evidence: null);

        await memory.RememberAsync(scope, new RememberRequest(evt));
    }

    private static IEnumerable<string> BuildKeywords(string input)
    {
        return input
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ':', ';', '!', '?', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => x.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(8);
    }

    private static string Clamp(string value, int maxChars)
    {
        return value.Length <= maxChars ? value : value[..maxChars];
    }
}
