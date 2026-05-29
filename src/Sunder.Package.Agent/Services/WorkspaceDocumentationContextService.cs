using System.Text;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class WorkspaceDocumentationContextService : IAgentSystemPromptContributor
{
    private const int MaxDocumentChars = 12000;
    private const int MaxPromptChars = 60000;
    private const int MaxAutoDocuments = 24;
    private static readonly string[] SupportedExtensions = [".md", ".mdx", ".txt"];

    public string ContributorId => "workspace-documentation-context";

    public string DisplayName => "Workspace Documentation";

    public async ValueTask<IReadOnlyList<AgentSystemPromptBlock>> ContributeAsync(
        AgentSystemPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Workspace is null)
        {
            return [];
        }

        var documents = ListWorkspaceDocuments(request.Workspace).ToArray();
        if (documents.Length == 0)
        {
            return [];
        }

        var content = new StringBuilder();
        content.AppendLine("Workspace docs are read-only context for the selected workspace.")
            .AppendLine("Explicit docs apply globally to this workspace. Auto docs are scoped to the workspace path they were discovered under.")
            .AppendLine("These docs do not grant additional file or shell access; tool access remains limited to configured workspace paths.")
            .AppendLine();

        var appendedDocuments = 0;
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadDocumentAsync(document.FilePath, cancellationToken);
            if (text is null)
            {
                continue;
            }

            if (content.Length > MaxPromptChars)
            {
                break;
            }

            AppendDocument(content, document, text);
            appendedDocuments++;
        }

        if (appendedDocuments == 0)
        {
            return [];
        }

        return
        [
            new AgentSystemPromptBlock(
                "workspace-documentation",
                "Workspace Documentation",
                content.ToString().Trim(),
                Priority: 85,
                Required: true,
                MaxChars: MaxPromptChars,
                SourceId: "sunder.package.agent")
        ];
    }

    private static IEnumerable<WorkspaceDocumentationItem> ListWorkspaceDocuments(AgentWorkspaceRecord workspace)
    {
        var seen = new HashSet<string>(GetPathStringComparer());

        foreach (var document in workspace.Documents.OrderBy(document => document.SortOrder))
        {
            if (string.IsNullOrWhiteSpace(document.FilePath))
            {
                continue;
            }

            var filePath = SafeFullPath(document.FilePath);
            if (filePath is null || !seen.Add(filePath) || !File.Exists(filePath) || !IsSupportedDocument(filePath))
            {
                continue;
            }

            yield return new WorkspaceDocumentationItem(
                filePath,
                Path.GetFileName(filePath),
                "Explicit workspace doc",
                null);
        }

        foreach (var workspacePath in workspace.Paths.OrderBy(path => path.SortOrder))
        {
            if (string.IsNullOrWhiteSpace(workspacePath.HostPath))
            {
                continue;
            }

            var hostPath = SafeFullPath(workspacePath.HostPath);
            if (hostPath is null)
            {
                continue;
            }

            var docsRoot = SafeFullPath(Path.Combine(hostPath, ".sunder", "docs"));
            if (docsRoot is null || !Directory.Exists(docsRoot))
            {
                continue;
            }

            var workspaceLabel = FormatPathForDisplay(hostPath);
            foreach (var filePath in EnumerateAutoDocs(docsRoot).Take(MaxAutoDocuments))
            {
                if (!seen.Add(filePath))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(docsRoot, filePath).Replace(Path.DirectorySeparatorChar, '/');
                yield return new WorkspaceDocumentationItem(
                    filePath,
                    relativePath,
                    "Auto workspace doc",
                    workspaceLabel);
            }
        }
    }

    private static IEnumerable<string> EnumerateAutoDocs(string docsRoot)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        try
        {
            return Directory.EnumerateFiles(docsRoot, "*", options)
                .Select(SafeFullPath)
                .OfType<string>()
                .Where(path => IsSupportedDocument(path) && IsSameOrChildPath(path, docsRoot))
                .OrderBy(path => Path.GetRelativePath(docsRoot, path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static async Task<string?> ReadDocumentAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxDocumentChars + 1];
            var read = await reader.ReadBlockAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return null;
            }

            var text = new string(buffer, 0, Math.Min(read, MaxDocumentChars)).Trim();
            return read > MaxDocumentChars
                ? text + Environment.NewLine + Environment.NewLine + "[truncated]"
                : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static void AppendDocument(StringBuilder builder, WorkspaceDocumentationItem document, string text)
    {
        builder.Append("### ").AppendLine(document.Title)
            .Append("Kind: ").AppendLine(document.Kind);
        if (!string.IsNullOrWhiteSpace(document.WorkspacePathLabel))
        {
            builder.Append("Workspace path scope: ").AppendLine(document.WorkspacePathLabel);
        }

        builder.Append("Host path: ").AppendLine(document.FilePath)
            .AppendLine()
            .AppendLine("```text")
            .AppendLine(text)
            .AppendLine("```")
            .AppendLine();
    }

    private static bool IsSupportedDocument(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string FormatPathForDisplay(string path)
        => Path.GetFullPath(path).Replace(Path.DirectorySeparatorChar, '/');

    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(candidate, root, comparison)
               || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static StringComparer GetPathStringComparer()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record WorkspaceDocumentationItem(
        string FilePath,
        string Title,
        string Kind,
        string? WorkspacePathLabel);
}
