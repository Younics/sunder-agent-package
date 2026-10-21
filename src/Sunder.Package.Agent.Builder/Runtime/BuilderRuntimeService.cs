using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Builder;

[SunderService(Key = "builder-runtime", ContractId = "younics.sunder.package.maker.runtime", ServiceId = "builder-runtime", IncludeSchemaDialect = true)]
[SunderServiceProvider(ProviderId = "builder.runtime", Audience = SunderServiceAudience.Owner)]
[SunderServiceError("agent.builder.invalid-request")]
[SunderServiceError("agent.builder.request-too-large")]
[SunderServiceError("agent.builder.response-too-large")]
[SunderServiceError("agent.builder.operation-failed")]
[SunderServiceError("agent.builder.busy")]
[SunderServiceError("agent.builder.job-not-found")]
[SunderServiceError("agent.builder.project-conflict")]
[SunderServiceError("agent.builder.backend-changed")]
public sealed partial class BuilderRuntimeService(IPackageContext context) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly string backendId = Guid.NewGuid().ToString("N");
    private Backend? backend;
    private bool stopping;
    private Task? disposal;

    [SunderMethod(TimeoutSeconds = 120, Idempotency = SunderServiceIdempotency.Idempotent,
        Errors = ["agent.builder.invalid-request", "agent.builder.request-too-large", "agent.builder.response-too-large", "agent.builder.operation-failed", "agent.builder.project-conflict"])]
    public ValueTask<SunderContent> DiscoverAsync(string folder, CancellationToken cancellationToken = default)
        => InvokeAsync(async () =>
        {
            BuilderContent.ValidateRequest(folder);
            folder = BuilderJobValidation.Absolute(folder, "Discovery folder");
            var projects = await Task.Run(() => new BuilderProjectDiscovery().DiscoverAsync(folder, cancellationToken), cancellationToken).ConfigureAwait(false);
            return BuilderContent.Create(projects);
        }, cancellationToken);

    [SunderMethod(TimeoutSeconds = 120, Idempotency = SunderServiceIdempotency.Idempotent,
        Errors = ["agent.builder.invalid-request", "agent.builder.request-too-large", "agent.builder.response-too-large", "agent.builder.operation-failed", "agent.builder.project-conflict"])]
    public ValueTask<SunderContent> InspectAsync(string entryPoint, CancellationToken cancellationToken = default)
        => InvokeAsync(async () =>
        {
            BuilderContent.ValidateRequest(entryPoint);
            entryPoint = BuilderJobValidation.Absolute(entryPoint, "Project entry point");
            var project = await Task.Run(() => new BuilderProjectDiscovery().InspectAsync(entryPoint, cancellationToken), cancellationToken).ConfigureAwait(false);
            return BuilderContent.Create(project);
        }, cancellationToken);

    [SunderMethod(TimeoutSeconds = 300, Idempotency = SunderServiceIdempotency.Idempotent,
        Errors = ["agent.builder.invalid-request", "agent.builder.request-too-large", "agent.builder.response-too-large", "agent.builder.operation-failed"])]
    public ValueTask<SunderContent> GetToolchainsAsync(string kind, bool checkUpdates, CancellationToken cancellationToken = default)
        => InvokeAsync(async () =>
        {
            BuilderContent.ValidateRequest(kind);
            var parsed = kind switch { "dotnet" => BuilderProjectKind.Dotnet, "node" => BuilderProjectKind.Node, _ => throw new ArgumentException("Toolchain kind must be 'dotnet' or 'node'.") };
            var status = await Task.Run(() => GetBackend().Toolchains.GetStatusAsync(parsed, checkUpdates, cancellationToken), cancellationToken).ConfigureAwait(false);
            return BuilderContent.Create(status with
            {
                CatalogError = status.CatalogError is null ? null : BuilderJobText.Safe(status.CatalogError, 4096),
                Installed = status.Installed.Select(item => item with { HealthError = item.HealthError is null ? null : BuilderJobText.Safe(item.HealthError, 4096) }).ToArray(),
                LastOperation = status.LastOperation is { Error: { } error } operation ? operation with { Error = BuilderJobText.Safe(error, 8192) } : status.LastOperation,
            });
        }, cancellationToken);

    [SunderMethod(TimeoutSeconds = 30, Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<string> GetBackendIdAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() => Task.FromResult(backendId), cancellationToken);

    [SunderMethod(TimeoutSeconds = 30,
        Errors = ["agent.builder.invalid-request", "agent.builder.request-too-large", "agent.builder.operation-failed", "agent.builder.busy", "agent.builder.job-not-found", "agent.builder.backend-changed"])]
    public ValueTask<string> StartJobAsync(string requestJson, CancellationToken cancellationToken = default)
        => InvokeAsync(async () =>
        {
            var request = BuilderContent.ReadJobRequest(requestJson);
            RequireBackend(request.BackendId);
            return await Task.Run(() => GetBackend().Jobs.Start(request, cancellationToken), cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    [SunderMethod(TimeoutSeconds = 30, Idempotency = SunderServiceIdempotency.Idempotent,
        Errors = ["agent.builder.invalid-request", "agent.builder.response-too-large", "agent.builder.job-not-found", "agent.builder.operation-failed", "agent.builder.backend-changed"])]
    public ValueTask<SunderContent> GetJobAsync(string jobId, string expectedBackendId, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            RequireBackend(expectedBackendId);
            return Task.FromResult(BuilderContent.Create(GetBackend().Jobs.GetJob(jobId) with { BackendId = backendId }));
        }, cancellationToken);

    [SunderMethod(TimeoutSeconds = 30, Idempotency = SunderServiceIdempotency.Idempotent,
        Errors = ["agent.builder.invalid-request", "agent.builder.job-not-found", "agent.builder.operation-failed", "agent.builder.backend-changed"])]
    public async ValueTask CancelJobAsync(string jobId, string expectedBackendId, CancellationToken cancellationToken = default)
        => _ = await InvokeAsync(async () =>
        {
            RequireBackend(expectedBackendId);
            await GetBackend().Jobs.CancelAsync(jobId, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    private void RequireBackend(string? expected)
    {
        if (!string.Equals(expected, backendId, StringComparison.Ordinal))
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.builder.backend-changed",
                "The Builder backend was replaced. The previous job cannot be resumed or acknowledged by this backend."));
    }

    private Backend GetBackend()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            // RoleLocalWorkspace becomes available at activation, not provider construction.
            if (backend is null)
            {
                var service = new BuilderToolchainService(context);
                try
                {
                    var toolchains = new BuilderProjectDriverToolchains(service);
                    var driver = new BuilderConfiguredJobDriver(toolchains, new BuilderLocalProcessRunner(),
                        new BuilderProjectDriverOptions(Path.Combine(service.RootPath, "project-tools")));
                    backend = new(service, toolchains, new BuilderJobManager(driver));
                }
                catch { service.Dispose(); throw; }
            }
            return backend;
        }
    }

    private async ValueTask<T> InvokeAsync<T>(Func<Task<T>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (sync) ObjectDisposedException.ThrowIf(stopping, this);
        try { return await operation().ConfigureAwait(false); }
        catch (SunderRpcException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or NotSupportedException
            or UnauthorizedAccessException or TimeoutException or HttpRequestException or System.ComponentModel.Win32Exception
            or KeyNotFoundException or FormatException or OverflowException or System.Xml.XmlException)
        {
            var code = error switch
            {
                BuilderJobBusyException => "agent.builder.busy",
                BuilderJobNotFoundException => "agent.builder.job-not-found",
                BuilderProjectConflictException => "agent.builder.project-conflict",
                ArgumentException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException or FormatException or OverflowException or System.Xml.XmlException => "agent.builder.invalid-request",
                _ => "agent.builder.operation-failed",
            };
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, code, BuilderJobText.Safe(error.Message, 4096)));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposal is not null) return new(disposal);
            stopping = true;
            var current = backend;
            disposal = Task.Run(async () =>
            {
                if (current is null) return;
                await current.Jobs.DisposeAsync().ConfigureAwait(false);
                current.Service.Dispose();
            });
            return new(disposal);
        }
    }

    private sealed record Backend(BuilderToolchainService Service, IBuilderProjectDriverToolchains Toolchains, BuilderJobManager Jobs);
}
