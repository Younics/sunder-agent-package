using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentModels = Sunder.Generated.Packages.Sunder_Package_Agent_Models;

namespace Sunder.Package.Agent.Builder;

[SunderService(Key = "builder-runtime", ContractId = "sunder.package.agent.builder.runtime", ServiceId = "builder-runtime", IncludeSchemaDialect = true)]
[SunderServiceProvider(ProviderId = "builder.runtime", Audience = SunderServiceAudience.Owner)]
[SunderServiceError("agent.builder.execution-target-unavailable")]
[SunderServiceError("agent.builder.invalid-request")]
[SunderServiceError("agent.builder.request-too-large")]
[SunderServiceError("agent.builder.response-too-large")]
[SunderServiceError("agent.builder.unsupported-operation")]
[SunderServiceError("agent.builder.workspace-execution-unavailable")]
public sealed partial class BuilderRuntimeService(IServiceProviders<IWorkspaceExecutionResolverService> resolvers)
{
    [SunderMethod(TimeoutSeconds = 2100, Idempotency = SunderServiceIdempotency.Idempotent,
        Errors = ["agent.builder.workspace-execution-unavailable", "agent.builder.response-too-large"])]
    public async ValueTask<SunderContent> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        => BuilderContent.Create(await WithResolverAsync(
            (resolver, token) => resolver.Service.ListWorkspacesAsync(token), cancellationToken).ConfigureAwait(false));

    [SunderMethod(TimeoutSeconds = 2100, Idempotency = SunderServiceIdempotency.Idempotent,
        Errors = ["agent.builder.workspace-execution-unavailable", "agent.builder.execution-target-unavailable", "agent.builder.invalid-request", "agent.builder.response-too-large"])]
    public async ValueTask<SunderContent> ResolveWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        => BuilderContent.Create(await WithTargetAsync(workspaceId, async (operation, token) =>
        {
            var shell = await operation.Target.ShellAsync(operation.Context, token).ConfigureAwait(false);
            return new BuilderWorkspaceExecutionProjection(operation.Resolution.Workspace, operation.Resolution.Binding,
                operation.Resolution.Target, operation.Resolution.Scope, shell,
                operation.Description.Facets.Contains(AgentExecutionFacetIds.PathMapping, StringComparer.Ordinal),
                operation.Description.Facets.Contains(AgentExecutionFacetIds.PathEnvironment, StringComparer.Ordinal));
        }, cancellationToken).ConfigureAwait(false));

    [SunderMethod(TimeoutSeconds = 2100,
        Errors = ["agent.builder.workspace-execution-unavailable", "agent.builder.execution-target-unavailable", "agent.builder.invalid-request", "agent.builder.request-too-large", "agent.builder.response-too-large", "agent.builder.unsupported-operation"])]
    public async ValueTask<SunderContent> ExecuteProcessAsync(string workspaceId, AgentModels.AgentProcessCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        BuilderContent.ValidateRequest(new { workspaceId, request });
        Require(request.FileName, "Process file name");
        return BuilderContent.Create(await WithTargetAsync(workspaceId, async (operation, token) =>
        {
            if (!operation.Description.Facets.Contains(AgentExecutionFacetIds.ProcessExecution, StringComparer.Ordinal))
                throw Domain("agent.builder.unsupported-operation", "Package Builder requires the execution target's structured process-execution facet.");
            return Process(await operation.Target.ExecuteProcessAsync(new() { Context = operation.Context, Request = request }, token).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false));
    }

    [SunderMethod(TimeoutSeconds = 2100,
        Errors = ["agent.builder.workspace-execution-unavailable", "agent.builder.execution-target-unavailable", "agent.builder.invalid-request", "agent.builder.request-too-large", "agent.builder.response-too-large"])]
    public async ValueTask<SunderContent> ExecuteShellAsync(string workspaceId, AgentModels.AgentShellCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        BuilderContent.ValidateRequest(new { workspaceId, request });
        Require(request.Command, "Shell command");
        return BuilderContent.Create(await WithTargetAsync(workspaceId, async (operation, token) =>
            Process(await operation.Target.ExecuteShellAsync(new() { Context = operation.Context, Request = request }, token).ConfigureAwait(false)),
            cancellationToken).ConfigureAwait(false));
    }

    [SunderMethod(TimeoutSeconds = 2100, Idempotency = SunderServiceIdempotency.Idempotent,
        Errors = ["agent.builder.workspace-execution-unavailable", "agent.builder.execution-target-unavailable", "agent.builder.invalid-request", "agent.builder.request-too-large", "agent.builder.unsupported-operation"])]
    public ValueTask<AgentModels.AgentExecutionPathMapping> MapToHostPathAsync(string workspaceId, string executionPath,
        CancellationToken cancellationToken = default)
    {
        BuilderContent.ValidateRequest(new { workspaceId, executionPath });
        Require(executionPath, "Execution path");
        return WithTargetAsync(workspaceId, (operation, token) =>
        {
            if (!Supports(operation, AgentExecutionFacetIds.PathMapping))
                throw Domain("agent.builder.unsupported-operation", "The selected execution target does not map execution paths to host paths.");
            return operation.Target.MapPathAsync(new() { Context = operation.Context, Path = executionPath }, token);
        }, cancellationToken);
    }

    [SunderMethod(TimeoutSeconds = 2100,
        Errors = ["agent.builder.workspace-execution-unavailable", "agent.builder.execution-target-unavailable", "agent.builder.invalid-request", "agent.builder.request-too-large"])]
    public async ValueTask AddPathEntryAsync(string workspaceId, string executionPath, CancellationToken cancellationToken = default)
    {
        BuilderContent.ValidateRequest(new { workspaceId, executionPath });
        Require(executionPath, "Execution path");
        await WithTargetAsync(workspaceId, async (operation, token) =>
        {
            if (Supports(operation, AgentExecutionFacetIds.PathEnvironment))
                await operation.Target.AddPathAsync(new() { Context = operation.Context, Path = executionPath }, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> WithResolverAsync<T>(
        Func<ServiceProvider<IWorkspaceExecutionResolverService>, CancellationToken, ValueTask<T>> invoke, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        var catalog = await resolvers.ListAsync(caller).ConfigureAwait(false);
        var candidates = catalog.Providers.Where(provider => provider.State == ServiceProviderState.Active).ToArray();
        if (candidates.Length != 1) throw Unavailable(resolver: true);
        var resolver = candidates[0];
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, resolver.RetirementToken);
        try
        {
            await resolver.EnsureCurrentAsync(lifetime.Token).ConfigureAwait(false);
            var result = await invoke(resolver, lifetime.Token).ConfigureAwait(false);
            await resolver.EnsureCurrentAsync(lifetime.Token).ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (!caller.IsCancellationRequested && resolver.RetirementToken.IsCancellationRequested
            && error is OperationCanceledException or SunderRpcException { Error.Kind: SunderRpcErrorKind.Cancelled })
        { throw Unavailable(resolver: true); }
        catch (SunderRpcException error) when (!caller.IsCancellationRequested
            && error.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        { throw Unavailable(resolver: true); }
    }

    private ValueTask<T> WithTargetAsync<T>(string workspaceId, Func<TargetOperation, CancellationToken, ValueTask<T>> invoke,
        CancellationToken caller)
    {
        Require(workspaceId, "Workspace id");
        BuilderContent.ValidateRequest(new { workspaceId });
        return WithResolverAsync(async (resolver, resolverToken) =>
        {
            var resolution = await resolver.Service.ResolveAsync(workspaceId, resolverToken).ConfigureAwait(false);
            await resolver.EnsureCurrentAsync(resolverToken).ConfigureAwait(false);
            var reference = resolution.ExecutionTarget;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(resolverToken, reference.RetirementToken);
            var token = lifetime.Token;
            try
            {
                await reference.EnsureCurrentAsync(token).ConfigureAwait(false);
                var target = reference.Service;
                var description = await target.DescribeAsync(token).ConfigureAwait(false);
                if (!string.Equals(description.TargetId, resolution.Binding.ContributionId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(description.TargetKind, resolution.Binding.ContributionId, StringComparison.OrdinalIgnoreCase))
                    throw Unavailable(resolver: false);
                var context = new AgentModels.AgentExecutionTargetContext
                {
                    SessionId = null, ProfileId = null, Workspace = resolution.Workspace, Binding = resolution.Binding,
                    AllowOutsideConfiguredScope = false, ApprovedResourceReferences = [], ApprovedResourceClaims = [],
                    ApprovedResourceCapabilities = [], ResourceOperation = null, CapturePostMutationResource = false,
                    ExpectedConfigurationGeneration = null,
                };
                var result = await invoke(new(resolution, description, target, context), token).ConfigureAwait(false);
                await reference.EnsureCurrentAsync(token).ConfigureAwait(false);
                return result;
            }
            catch (Exception error) when (!caller.IsCancellationRequested && !resolver.RetirementToken.IsCancellationRequested
                && reference.RetirementToken.IsCancellationRequested && error is OperationCanceledException or SunderRpcException { Error.Kind: SunderRpcErrorKind.Cancelled })
            { throw Unavailable(resolver: false); }
            catch (SunderRpcException error) when (!caller.IsCancellationRequested && !resolver.RetirementToken.IsCancellationRequested
                && error.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            { throw Unavailable(resolver: false); }
        }, caller);
    }

    private static bool Supports(TargetOperation operation, string facet)
        => operation.Description.Facets.Contains(facet, StringComparer.Ordinal)
           && operation.Resolution.Target.Facets.Contains(facet, StringComparer.Ordinal);

    private static BuilderProcessResult Process(AgentModels.AgentShellCommandResult value)
    {
        const int maximum = 512 * 1024;
        return new(checked((int)value.ExitCode), value.Output.Length <= maximum ? value.Output : value.Output[..maximum], string.Empty,
            value.WasTruncated || value.Output.Length > maximum);
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Domain("agent.builder.invalid-request", name + " is required.");
    }

    private static SunderRpcException Unavailable(bool resolver)
        => Domain(resolver ? "agent.builder.workspace-execution-unavailable" : "agent.builder.execution-target-unavailable",
            resolver ? "Agent workspace execution service is unavailable." : "The selected execution-target package is unavailable.");

    private static SunderRpcException Domain(string code, string message)
        => new(new(SunderRpcErrorKind.Domain, code, message));

    private sealed record TargetOperation(AgentModels.AgentWorkspaceExecutionResolution Resolution,
        AgentModels.AgentExecutionTargetDescriptor Description, IExecutionTargetService Target, AgentModels.AgentExecutionTargetContext Context);
}
