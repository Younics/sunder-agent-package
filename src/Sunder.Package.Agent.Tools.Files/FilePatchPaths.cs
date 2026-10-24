namespace Sunder.Package.Agent.Tools.Files;

internal sealed record FilePatchPath(string Path, bool IsDelete);

internal static class FilePatchPaths
{
    internal const int MaximumPaths = 64;

    // One ordering for permission resource indexes, scoped instructions and
    // mutation planning. A move reads/removes its source and creates its target.
    internal static IReadOnlyList<FilePatchPath> Affected(IReadOnlyList<FilePatchOperation> operations)
    {
        var paths = new List<FilePatchPath>();
        foreach (var operation in operations)
        {
            paths.Add(new(operation.Path, operation.Kind == FilePatchOperationKind.Delete || operation.MoveTo is not null));
            if (operation.MoveTo is { } destination) paths.Add(new(destination, false));
        }
        if (paths.Select(path => path.Path).Distinct(StringComparer.Ordinal).Count() > MaximumPaths)
            throw new FilePatchPathLimitException();
        return paths;
    }
}

internal sealed class FilePatchPathLimitException()
    : InvalidOperationException($"Patches support at most {FilePatchPaths.MaximumPaths} affected paths, including both move paths.");
