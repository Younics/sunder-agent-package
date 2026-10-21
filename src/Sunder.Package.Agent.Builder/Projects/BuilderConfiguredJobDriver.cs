namespace Sunder.Package.Agent.Builder;

/// <summary>Resolves local developer packages once per accepted job, outside backend construction.</summary>
internal sealed class BuilderConfiguredJobDriver(IBuilderProjectDriverToolchains toolchains,
    IBuilderLocalProcessRunner processes, BuilderProjectDriverOptions options) : IBuilderJobDriver
{
    public async Task ExecuteAsync(BuilderJobRequest request, IBuilderJobReporter reporter, CancellationToken cancellationToken)
    {
        request = BuilderJobValidation.Normalize(request);
        var selected = request.Kind == BuilderProjectKind.Dotnet && request.Operation is
            "ensure-sdk" or "create" or "setup" or "build" or "archive" or "watch"
            ? await BuilderLocalNuGet.LoadOptionsAsync(options, cancellationToken).ConfigureAwait(false)
            : options;
        // JobManager executes a job only once (including exact-ID retries). The driver and
        // retained source selection live for the whole operation, including a long watch.
        await new BuilderProjectDriver(toolchains, processes, selected)
            .ExecuteAsync(request, reporter, cancellationToken).ConfigureAwait(false);
    }
}
