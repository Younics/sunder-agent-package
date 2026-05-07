using System.Text;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentSystemPromptComposer(IPackageExtensionCatalog extensionCatalog)
{
    public async ValueTask<string?> ComposeAsync(
        AgentSystemPromptRequest request,
        string? baseInstructions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blocks = new List<AgentSystemPromptBlock>();
        blocks.AddRange(BuildToolPriorityBlocks(request.AvailableTools));
        blocks.AddRange(BuildToolRuntimeInstructionBlocks(request.AvailableTools));

        foreach (var contributor in extensionCatalog.GetExtensions(PackageExtensionPoints.SystemPromptContributors)
                     .OrderBy(contributor => contributor.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var contribution = await contributor.ContributeAsync(request, cancellationToken);
                blocks.AddRange(contribution ?? []);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Optional prompt contributors must not block the base chat flow.
            }
        }

        var renderedBlocks = blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.BlockId)
                            && !string.IsNullOrWhiteSpace(block.Title)
                            && !string.IsNullOrWhiteSpace(block.Content))
            .GroupBy(block => BuildBlockKey(block), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(block => block.Required)
                .ThenByDescending(block => block.Priority)
                .ThenBy(block => block.Title, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(block => block.Required)
            .ThenByDescending(block => block.Priority)
            .ThenBy(block => block.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(block => block.SourceId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(block => block.BlockId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(baseInstructions))
        {
            builder.AppendLine(baseInstructions.Trim());
        }

        foreach (var block in renderedBlocks)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append("## ").AppendLine(block.Title.Trim());
            builder.AppendLine(ApplyMaxChars(block.Content.Trim(), block.MaxChars));
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static IReadOnlyList<AgentSystemPromptBlock> BuildToolRuntimeInstructionBlocks(IReadOnlyList<AgentToolDescriptor> availableTools)
    {
        var tools = availableTools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.RuntimeInstructions))
            .OrderBy(tool => tool.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tools.Length == 0)
        {
            return [];
        }

        var builder = new StringBuilder();
        foreach (var tool in tools)
        {
            builder.Append("### ").Append(tool.DisplayName.Trim()).Append(" (`").Append(tool.ToolId.Trim()).AppendLine("`)");
            builder.AppendLine(tool.RuntimeInstructions!.Trim()).AppendLine();
        }

        return
        [
            new AgentSystemPromptBlock(
                "tool-runtime-instructions",
                "Tool Runtime Context",
                builder.ToString().Trim(),
                Priority: 100,
                Required: true,
                SourceId: "sunder.package.agent")
        ];
    }

    private static IReadOnlyList<AgentSystemPromptBlock> BuildToolPriorityBlocks(IReadOnlyList<AgentToolDescriptor> availableTools)
    {
        if (availableTools.Select(tool => tool.Priority).Distinct().Take(2).Count() < 2)
        {
            return [];
        }

        return
        [
            new AgentSystemPromptBlock(
                "tool-priority",
                "Tool Priority",
                "When multiple tools can satisfy the same need, prefer higher-priority tools first. Use lower-priority tools only when higher-priority tools do not fit the task or cannot complete it.",
                Priority: 110,
                Required: true,
                SourceId: "sunder.package.agent")
        ];
    }

    private static string BuildBlockKey(AgentSystemPromptBlock block)
        => string.Concat(block.SourceId ?? string.Empty, ":", block.BlockId);

    private static string ApplyMaxChars(string content, int? maxChars)
    {
        if (maxChars is not > 0 || content.Length <= maxChars.Value)
        {
            return content;
        }

        return content[..maxChars.Value].TrimEnd() + "\n\n[truncated]";
    }
}
