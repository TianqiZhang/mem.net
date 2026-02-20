using System.ComponentModel;
using System.Text;
using MemNet.AgentMemory;
using MemNet.Client;

internal sealed class MemoryTools
{
    private readonly AgentMemory _memory;
    private readonly MemNetScope _scope;
    private readonly MemorySessionContext _sessionContext;

    public MemoryTools(AgentMemory memory, MemNetScope scope, MemorySessionContext sessionContext)
    {
        _memory = memory;
        _scope = scope;
        _sessionContext = sessionContext;
    }

    [Description("Search event digests in long-term memory and return top results.")]
    public async Task<string> MemoryRecallAsync(
        [Description("Natural-language query for event recall.")] string query,
        [Description("Maximum number of hits. Recommended range: 1-20.")] int topK = 8,
        CancellationToken cancellationToken = default)
    {
        topK = Math.Clamp(topK, 1, 20);
        LogToolCall("memory_recall", $"query=\"{ClampForLog(query, 80)}\", topK={topK}");
        var results = await _memory.MemoryRecallAsync(_scope, query, topK, cancellationToken);
        if (results.Count == 0)
        {
            LogToolResult("memory_recall", "0 results");
            return "No event memories matched that query.";
        }

        LogToolResult("memory_recall", $"{results.Count} result(s)");
        var sb = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var item = results[i];
            sb.Append('[').Append(i + 1).Append("] ")
                .Append(item.Timestamp.ToString("O"))
                .Append(" | ")
                .Append(item.SourceType)
                .Append(" | ")
                .AppendLine(item.Digest);
        }

        return sb.ToString();
    }

    [Description("List existing memory files. Optionally filter by path prefix.")]
    public async Task<string> MemoryListFilesAsync(
        [Description("Optional path prefix. Example: projects/.")] string? prefix = null,
        [Description("Optional maximum files to return. Recommended range: 1-200.")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var normalizedPrefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix;
        LogToolCall("memory_list_files", $"prefix=\"{normalizedPrefix ?? ""}\", limit={limit}");

        var files = await _memory.MemoryListFilesAsync(_scope, normalizedPrefix, limit, cancellationToken);
        if (!string.IsNullOrWhiteSpace(normalizedPrefix)
            && normalizedPrefix.StartsWith("projects/", StringComparison.OrdinalIgnoreCase))
        {
            _sessionContext.UpdateProjectCatalog(files);
        }

        if (files.Count == 0)
        {
            LogToolResult("memory_list_files", "0 results");
            return "No files matched that prefix.";
        }

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            sb.Append("- ")
                .Append(file.Path)
                .Append(" (last_modified_utc=")
                .Append(file.LastModifiedUtc.ToString("O"))
                .AppendLine(")");
        }

        var result = sb.ToString().TrimEnd();
        LogToolResult("memory_list_files", result);
        return result;
    }

    [Description("Load a memory file by path and return its full text content.")]
    public async Task<string> MemoryLoadFileAsync(
        [Description("File path, for example profile.md.")] string path,
        CancellationToken cancellationToken = default)
    {
        LogToolCall("memory_load_file", $"path=\"{path}\"");
        try
        {
            var file = await _memory.MemoryLoadFileAsync(_scope, path, cancellationToken);
            var result = BuildFileContentResult(path, file.Content, "Loaded");
            LogToolResult("memory_load_file", result);
            return file.Content;
        }
        catch (MemNetApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            LogToolResult("memory_load_file", "not_found");
            return $"File not found: {path}";
        }
    }

    [Description("Patch text in a memory file by replacing old_text with new_text.")]
    public async Task<string> MemoryPatchFileAsync(
        [Description("File path, for example profile.md, long_term_memory.md, projects/{project_name}.md.")] string path,
        [Description("Exact existing text to match.")] string old_text,
        [Description("Replacement text.")] string new_text,
        [Description("1-based occurrence index when old_text appears multiple times. Optional.")] int? occurrence = null,
        CancellationToken cancellationToken = default)
    {
        LogToolCall(
            "memory_patch_file",
            $"path=\"{path}\", old=\"{ClampForLog(old_text, 60)}\", new=\"{ClampForLog(new_text, 60)}\", occurrence={occurrence?.ToString() ?? "null"}");
        try
        {
            var file = await _memory.MemoryPatchFileAsync(
                _scope,
                path,
                [new MemoryPatchEdit(old_text, new_text, occurrence)],
                cancellationToken: cancellationToken);

            var result = BuildFileContentResult(path, file.Content, "Patched");
            LogToolResult("memory_patch_file", result);
            _sessionContext.MarkFileUpdated(path, file.Content);
            return result;
        }
        catch (MemNetApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            LogToolResult("memory_patch_file", "not_found");
            return $"File not found: {path}";
        }
    }

    [Description("Write full text content to a memory file (create or replace).")]
    public async Task<string> MemoryWriteFileAsync(
        [Description("File path, for example profile.md, long_term_memory.md, projects/{project_name}.md.")] string path,
        [Description("Full markdown content to write.")] string content,
        CancellationToken cancellationToken = default)
    {
        LogToolCall("memory_write_file", $"path=\"{path}\", chars={content.Length}");
        var file = await _memory.MemoryWriteFileAsync(_scope, path, content, cancellationToken: cancellationToken);
        var result = BuildFileContentResult(path, file.Content, "Wrote");
        LogToolResult("memory_write_file", result);
        _sessionContext.MarkFileUpdated(path, file.Content);
        return result;
    }

    private static void LogToolCall(string toolName, string args)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"\n[tool-call] {toolName}({args})");
        Console.ResetColor();
    }

    private static void LogToolResult(string toolName, string result)
    {
        Console.ForegroundColor = ConsoleColor.DarkGreen;
        Console.WriteLine($"[tool-result] {toolName}: {result}");
        Console.ResetColor();
    }

    private static string ClampForLog(string value, int maxChars)
    {
        var oneLine = value.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= maxChars ? oneLine : oneLine[..maxChars] + "...";
    }

    private static string BuildFileContentResult(string path, string content, string operation)
    {
        return $"{operation} {path}.\n\nCurrent content:\n{content}";
    }
}
