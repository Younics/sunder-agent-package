using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileDeleteHandler
{
    public static ValueTask<AgentFileMutationResult> DeleteAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken,
        string? expectedContentHash = null)
        => target.DeleteFileAsync(
            context,
            new AgentFileDeleteRequest(path) { ExpectedContentHash = expectedContentHash },
            cancellationToken);
}
