using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileDeleteHandler
{
    public static ValueTask<AgentFileMutationResult> DeleteAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken,
        string? expectedContentHash = null)
        => target.Service.DeleteFileAsync(new()
        { Context = context, Request = new() { Path = path, Recursive = false, ExpectedContentHash = expectedContentHash } }, cancellationToken);
}
