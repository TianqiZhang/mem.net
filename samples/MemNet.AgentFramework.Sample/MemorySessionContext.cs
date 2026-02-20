using System.Text;
using System.Text.Json;
using MemNet.AgentMemory;
using MemNet.Client;
using Microsoft.Extensions.AI;

internal sealed class MemorySessionContext
{
    private const string ProfilePath = "profile.md";
    private const string LongTermPath = "long_term_memory.md";
    private const string ProjectsPrefix = "projects/";

    private readonly MemNetClient _client;
    private readonly AgentMemory _memory;
    private readonly MemNetScope _scope;
    private string? _profileContent;
    private string? _longTermContent;
    private List<string> _projectFiles = new();
    private bool _includeSnapshotOnNextTurn = true;

    public MemorySessionContext(MemNetClient client, AgentMemory memory, MemNetScope scope)
    {
        _client = client;
        _memory = memory;
        _scope = scope;
    }

    public async Task PrimeAsync(CancellationToken cancellationToken = default)
    {
        var assembled = await _client.AssembleContextAsync(
            _scope,
            new AssembleContextRequest(
                Files:
                [
                    new AssembleFileRef(ProfilePath),
                    new AssembleFileRef(LongTermPath)
                ],
                MaxDocs: 4,
                MaxCharsTotal: 40_000),
            cancellationToken);

        _profileContent = FindAssembledFileText(assembled, ProfilePath);
        _longTermContent = FindAssembledFileText(assembled, LongTermPath);

        var projects = await _memory.MemoryListFilesAsync(_scope, ProjectsPrefix, 200, cancellationToken);
        _projectFiles = projects
            .Select(x => x.Path)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _includeSnapshotOnNextTurn = true;
    }

    public IReadOnlyList<ChatMessage> BuildTurnMessages(string userInput)
    {
        if (!_includeSnapshotOnNextTurn)
        {
            return [new ChatMessage(ChatRole.User, userInput)];
        }

        _includeSnapshotOnNextTurn = false;
        return
        [
            new ChatMessage(
                ChatRole.System,
                "Use this preloaded memory snapshot as default context. " +
                "Avoid redundant memory_load_file calls for these files unless explicitly asked to verify.\n\n" +
                BuildSnapshotText()),
            new ChatMessage(ChatRole.User, userInput)
        ];
    }

    public void MarkFileUpdated(string path, string content)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath.Equals(ProfilePath, StringComparison.OrdinalIgnoreCase))
        {
            _profileContent = content;
            _includeSnapshotOnNextTurn = true;
            return;
        }

        if (normalizedPath.Equals(LongTermPath, StringComparison.OrdinalIgnoreCase))
        {
            _longTermContent = content;
            _includeSnapshotOnNextTurn = true;
            return;
        }

        if (normalizedPath.StartsWith(ProjectsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            AddProjectFile(normalizedPath);
            _includeSnapshotOnNextTurn = true;
        }
    }

    public void UpdateProjectCatalog(IReadOnlyList<MemoryFileListItem> files)
    {
        _projectFiles = files
            .Select(x => NormalizePath(x.Path))
            .Where(x => x.StartsWith(ProjectsPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _includeSnapshotOnNextTurn = true;
    }

    private static string? FindAssembledFileText(AssembleContextResponse assembled, string path)
    {
        var match = assembled.Files.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return null;
        }

        var text = match.Document.Content["text"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return JsonSerializer.Serialize(match.Document.Content, new JsonSerializerOptions { WriteIndented = true });
    }

    private string BuildSnapshotText()
    {
        var sb = new StringBuilder();
        AppendSnapshotSection(sb, ProfilePath, _profileContent);
        sb.AppendLine();
        AppendSnapshotSection(sb, LongTermPath, _longTermContent);
        sb.AppendLine();
        AppendProjectCatalogSection(sb, _projectFiles);
        return sb.ToString().TrimEnd();
    }

    private static void AppendSnapshotSection(StringBuilder sb, string path, string? content)
    {
        sb.AppendLine($"[{path}]");
        if (content is null)
        {
            sb.AppendLine("(not found)");
        }
        else if (string.IsNullOrWhiteSpace(content))
        {
            sb.AppendLine("(empty)");
        }
        else
        {
            sb.AppendLine(content.TrimEnd());
        }
    }

    private static void AppendProjectCatalogSection(StringBuilder sb, IReadOnlyList<string> projectFiles)
    {
        sb.AppendLine("[projects/catalog]");
        if (projectFiles.Count == 0)
        {
            sb.AppendLine("(none)");
            return;
        }

        foreach (var projectPath in projectFiles)
        {
            sb.Append("- ").AppendLine(projectPath);
        }
    }

    private void AddProjectFile(string path)
    {
        if (_projectFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _projectFiles.Add(path);
        _projectFiles = _projectFiles
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim().TrimStart('/');
    }
}
