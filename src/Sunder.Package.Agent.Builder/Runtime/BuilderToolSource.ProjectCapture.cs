using System.Text.Json;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderToolSource
{
    private async Task<(Captured? Capture, string? Error)> CaptureProjectAsync(AgentToolExecutionContext context, string entryPoint, CancellationToken token)
    {
        var reference = context.ExecutionTarget!;
        if (await ValidateTargetAsync(context, reference, token).ConfigureAwait(false) is { } error) return (null, error);
        var roots = context.Workspace!.Paths.Select(path => path.HostPath).Where(Path.IsPathFullyQualified).Distinct(StringComparer.Ordinal).Take(33).ToArray();
        if (roots.Length is < 1 or > 32) return (null, "Select between one and 32 absolute local workspace roots.");
        var source = JsonSerializer.Serialize(new { entryPoint, roots, expectedSourceRevision = (string?)null }, Json);
        var response = await runtime.InvokeDevelopmentAsync("info", "", source, null, token).ConfigureAwait(false);
        using var info = JsonDocument.Parse(response.PayloadJson);
        var project = info.RootElement.GetProperty("project");
        source = JsonSerializer.Serialize(new { entryPoint = project.GetProperty("entryPoint").GetString(), roots,
            expectedSourceRevision = project.GetProperty("sourceRevision").GetString() }, Json);
        return (new(response.AppId, source, context.SessionId, context.Workspace.WorkspaceId, entryPoint, context.ProfileId,
            context.ExecutionBinding!, context.ExecutionTargetConfigurationGeneration, context.Workspace.UpdatedAtUtc, reference), null);
    }
}
