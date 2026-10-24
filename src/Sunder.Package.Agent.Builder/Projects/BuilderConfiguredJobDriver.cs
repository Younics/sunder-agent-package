namespace Sunder.Package.Agent.Builder;

/// <summary>Resolves local developer packages once per accepted job, outside backend construction.</summary>
internal sealed class BuilderConfiguredJobDriver(IBuilderProjectDriverToolchains toolchains,
    IBuilderLocalProcessRunner processes, BuilderProjectDriverOptions options) : IBuilderJobDriver
{
    public async Task ExecuteAsync(BuilderJobRequest request, IBuilderJobReporter reporter, CancellationToken cancellationToken)
    {
        request = BuilderJobValidation.Normalize(request);
        var selected = options;
        if (request.Operation is "ensure-sdk" or "create" or "setup" or "build" or "archive")
            selected = request.Kind == BuilderProjectKind.Dotnet
                ? await BuilderLocalNuGet.LoadOptionsAsync(options, cancellationToken).ConfigureAwait(false)
                : await BuilderLocalNpm.LoadOptionsAsync(options, cancellationToken).ConfigureAwait(false);
        // JobManager executes a job only once (including exact-ID retries). The driver and
        // retained source selection live until the finite operation and its children drain.
        await new BuilderProjectDriver(toolchains, processes, selected)
            .ExecuteAsync(request, reporter, cancellationToken).ConfigureAwait(false);
    }
}
