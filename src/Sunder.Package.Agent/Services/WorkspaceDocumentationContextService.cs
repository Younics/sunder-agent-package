using System.Text;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class WorkspaceDocumentationContextService : IAgentPromptContextContributor
{
    private const int MaxDocumentChars = 12000;
    private const int MaxPromptChars = 60000;
    private static readonly string[] SupportedExtensions = [".md", ".mdx", ".txt"];

    public string ContributorId => "workspace-documentation-context";

    public string DisplayName => "Workspace Documentation";

    public async ValueTask<AgentPromptContextContribution?> ContributeContextAsync(
        AgentPromptContextRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Workspace is null)
        {
            return null;
        }

        var documents = ListWorkspaceDocuments(request.Workspace).ToArray();
        if (documents.Length == 0)
        {
            return null;
        }

        var content = new StringBuilder();
        content.AppendLine("Explicit workspace documents are global read-only reference context for the selected workspace.")
            .AppendLine("They do not grant additional file or shell access, change permissions, or expand configured workspace scope.")
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
            return null;
        }

        return new AgentPromptContextContribution(
        [
            new AgentPromptContextBlock(
                "Workspace Documentation",
                content.ToString().Trim(),
                Priority: 85,
                SourceId: "sunder.package.agent",
                Provenance: AgentContextProvenance.Tool,
                Trust: AgentContextTrust.Untrusted)
            {
                Usage = AgentPromptContextUsage.Reference,
            }
        ]);
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
                Path.GetFileName(filePath));
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
            .AppendLine("Kind: Explicit global workspace reference")
            .Append("Host path: ").AppendLine(document.FilePath)
            .AppendLine()
            .AppendLine("```text")
            .AppendLine(text)
            .AppendLine("```")
            .AppendLine();
    }

    private static bool IsSupportedDocument(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

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

    private static StringComparer GetPathStringComparer()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record WorkspaceDocumentationItem(
        string FilePath,
        string Title);
}
