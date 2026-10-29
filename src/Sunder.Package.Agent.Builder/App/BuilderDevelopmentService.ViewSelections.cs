namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderDevelopmentService
{
    private readonly Dictionary<string, (string EntryPoint, string ViewId)> _viewSelections = new(StringComparer.Ordinal);

    private void RequireViewSelection(BuilderProjectInspection project, string viewId, string snapshotId)
    {
        if (!_viewSelections.TryGetValue(snapshotId, out var selected)
            || !BuilderPathService.PathComparer.Equals(selected.EntryPoint, project.EntryPoint) || selected.ViewId != viewId)
            throw Error("maker.development.invalid-request", "Inspect this exact package view before requesting element-scoped input. Old references never follow replacement views.");
    }
}
