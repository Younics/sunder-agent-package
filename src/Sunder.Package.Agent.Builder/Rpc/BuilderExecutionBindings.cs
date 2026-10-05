using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using ResolverWire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Workspace_Execution_Resolver;
using ExecutionApi = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Execution_Target;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Execution_Target;

namespace Sunder.Package.Agent.Builder.Rpc;

internal sealed class BuilderExecutionBindings(ISunderRpcClient rpc) : IBuilderRuntimeOperations, IBuilderRuntimeLifetime, ISunderWorkerActivationService
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Operation> _operations = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _watch = Task.CompletedTask;
    private Task? _stop;
    private bool _started;
    private bool _stopped;

    public ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken cancellationToken = default)
        => StartAsync(cancellationToken);

    public ValueTask StartAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (!_started) { _started = true; _watch = Task.WhenAll(Task.Run(() => WatchAsync(true)), Task.Run(() => WatchAsync(false))); }
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask<BuilderRpcResponse> ExecuteAsync(BuilderRpcRequest request, DateTimeOffset deadline, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        Operation operation;
        lock (_gate)
        {
            if (!_started || _stopped) throw Unavailable(resolver: true);
            operation = new(_lifetime.Token); _operations.Add(operation);
        }
        ISunderRpcCallScope? scope = null;
        try
        {
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(caller, operation.Token);
            var token = invocation.Token;
            var maximum = DateTimeOffset.UtcNow.AddMinutes(35);
            scope = await rpc.CreateCallScopeAsync(new(DeadlineUtc: deadline < maximum ? deadline : maximum), token).ConfigureAwait(false);
            var api = new AgentPackageApi(scope);
            var resolverApi = api.CSunder_Agent_Workspace_Execution_Resolver;
            var resolver = await resolverApi.ResolveSelectedPackageAsync(token).ConfigureAwait(false);
            operation.Resolver = resolver.Snapshot;
            if (!Matches(resolver.Snapshot, (await resolverApi.GetAsync(resolver, token).ConfigureAwait(false))?.Snapshot)) throw Unavailable(true);
            var client = resolverApi.CreateClient(scope, resolver).SWorkspace_2dexecution_2dresolver;
            if (request.Kind == BuilderRpcOperationKind.ListWorkspaces)
            {
                var workspaces = await client.ListWorkspacesAsync(new(), token).ConfigureAwait(false);
                await ValidateAsync(scope, operation, token).ConfigureAwait(false);
                return new(Workspaces: BuilderExecutionValues.Convert<AgentWorkspaceRecord[]>(workspaces));
            }
            var resolution = await client.ResolveAsync(new() { Value = Require(request.WorkspaceId, "Workspace id") }, token).ConfigureAwait(false);
            await ValidateAsync(scope, operation, token).ConfigureAwait(false);
            var handle = resolution.ExecutionTarget;
            var snapshot = await scope.GetProviderAsync(new(handle.EndpointReference), token).ConfigureAwait(false);
            if (!Matches(handle, snapshot)) throw Unavailable(false);
            operation.Target = snapshot;
            var targetApi = api.CSunder_Agent_Execution_Target;
            var selected = new ExecutionApi.Provider(snapshot!);
            if (!Matches(snapshot!, (await targetApi.GetAsync(selected, token).ConfigureAwait(false))?.Snapshot)) throw Unavailable(false);
            var target = targetApi.CreateClient(scope, selected).SExecution_2dtarget;
            var description = await target.DescribeAsync(new(), token).ConfigureAwait(false);
            await ValidateAsync(scope, operation, token).ConfigureAwait(false);
            var binding = BuilderExecutionValues.Convert<AgentWorkspaceBindingRecord>(resolution.Binding);
            if (!string.Equals(description.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(description.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase)) throw Unavailable(false);
            var workspace = BuilderExecutionValues.Convert<AgentWorkspaceRecord>(resolution.Workspace);
            var context = new Wire.AgentExecutionTargetContextWire
            {
                Context = BuilderExecutionValues.Convert<Wire.AgentExecutionTargetContext>(new AgentExecutionTargetContext(null, null, workspace, binding)),
                ApprovedResourceCapabilities = [], CapturePostMutationResource = false,
            };
            BuilderRpcResponse result;
            switch (request.Kind)
            {
                case BuilderRpcOperationKind.ResolveWorkspace:
                    var shell = await target.ShellAsync(context, token).ConfigureAwait(false);
                    result = new(Execution: new(workspace, binding, BuilderExecutionValues.Convert<AgentExecutionTargetDescriptor>(resolution.Target),
                        BuilderExecutionValues.Convert<AgentExecutionScopeDescriptor>(resolution.Scope), BuilderExecutionValues.Convert<AgentExecutionShellDescriptor>(shell),
                        description.Facets.Contains(AgentExecutionFacetIds.PathMapping, StringComparer.Ordinal), description.Facets.Contains(AgentExecutionFacetIds.PathEnvironment, StringComparer.Ordinal)));
                    break;
                case BuilderRpcOperationKind.ExecuteProcess:
                    if (!description.Facets.Contains(AgentExecutionFacetIds.ProcessExecution, StringComparer.Ordinal))
                        throw new BuilderRpcDomainException("agent.builder.unsupported-operation", "Package Builder requires the execution target's structured process-execution facet.");
                    result = new(Process: Process(await target.ExecuteProcessAsync(new()
                    {
                        Context = context, Request = BuilderExecutionValues.Convert<Wire.AgentProcessCommandRequest>(new AgentProcessCommandRequest(
                            Require(request.FileName, "Process file name"), request.Arguments ?? [], request.WorkingDirectory, request.TimeoutSeconds)),
                    }, token).ConfigureAwait(false)));
                    break;
                case BuilderRpcOperationKind.ExecuteShell:
                    result = new(Process: Process(await target.ExecuteShellAsync(new()
                    {
                        Context = context, Request = BuilderExecutionValues.Convert<Wire.AgentShellCommandRequest>(new AgentShellCommandRequest(
                            Require(request.Command, "Shell command"), request.WorkingDirectory, request.TimeoutSeconds)),
                    }, token).ConfigureAwait(false)));
                    break;
                case BuilderRpcOperationKind.MapToHostPath:
                    if (!description.Facets.Contains(AgentExecutionFacetIds.PathMapping, StringComparer.Ordinal)
                        || !resolution.Target.Facets.Contains(AgentExecutionFacetIds.PathMapping, StringComparer.Ordinal))
                        throw new BuilderRpcDomainException("agent.builder.unsupported-operation", "The selected execution target does not map execution paths to host paths.");
                    result = new(PathMapping: BuilderExecutionValues.Convert<AgentExecutionPathMapping>(await target.MapPathAsync(new()
                    { Context = context, Path = Require(request.ExecutionPath, "Execution path") }, token).ConfigureAwait(false)));
                    break;
                case BuilderRpcOperationKind.AddPathEntry:
                    if (description.Facets.Contains(AgentExecutionFacetIds.PathEnvironment, StringComparer.Ordinal)
                        && resolution.Target.Facets.Contains(AgentExecutionFacetIds.PathEnvironment, StringComparer.Ordinal))
                        await target.AddPathAsync(new() { Context = context, Path = Require(request.ExecutionPath, "Execution path") }, token).ConfigureAwait(false);
                    result = new();
                    break;
                default: throw new BuilderRpcDomainException("agent.builder.invalid-request", "Unknown Package Builder RPC operation.");
            }
            await ValidateAsync(scope, operation, token).ConfigureAwait(false);
            return result;
        }
        catch (SunderRpcException error) when (!caller.IsCancellationRequested && error.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        { throw Unavailable(operation.ResolverRetired || operation.Target is null); }
        catch (SunderRpcException error) when (!caller.IsCancellationRequested && error.Error.Kind == SunderRpcErrorKind.NotFound && error.Error.Code == "rpc.provider-not-found")
        { throw Unavailable(true); }
        catch (Exception error) when (!caller.IsCancellationRequested && operation.Token.IsCancellationRequested
            && error is OperationCanceledException or SunderRpcException { Error.Kind: SunderRpcErrorKind.Cancelled })
        { throw Unavailable(operation.ResolverRetired || operation.Target is null); }
        finally
        {
            try { if (scope is not null) await scope.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                operation.Dispose();
                lock (_gate) { _operations.Remove(operation); if (_stopped && _operations.Count == 0) _drained.TrySetResult(); }
            }
        }
    }

    private static async ValueTask ValidateAsync(ISunderRpcClient scope, Operation operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (operation.Resolver is { } resolver && !Matches(resolver, await scope.GetProviderAsync(resolver.Endpoint, token).ConfigureAwait(false)))
        { operation.Retire(true); throw Unavailable(true); }
        if (operation.Target is { } target && !Matches(target, await scope.GetProviderAsync(target.Endpoint, token).ConfigureAwait(false)))
        { operation.Retire(false); throw Unavailable(false); }
    }
    private static BuilderProcessResult Process(Wire.AgentShellCommandResult value)
    {
        const int maximum = 512 * 1024;
        return new(checked((int)value.ExitCode), value.Output.Length <= maximum ? value.Output : value.Output[..maximum], string.Empty,
            value.WasTruncated || value.Output.Length > maximum);
    }
    private static string Require(string? value, string name) => !string.IsNullOrWhiteSpace(value) ? value : throw new BuilderRpcDomainException("agent.builder.invalid-request", name + " is required.");
    private static BuilderRpcDomainException Unavailable(bool resolver) => new(resolver ? "agent.builder.workspace-execution-unavailable" : "agent.builder.execution-target-unavailable",
        resolver ? "Agent workspace execution service is unavailable." : "The selected execution-target package is unavailable.");
    private static bool Matches(SunderRpcProviderSnapshot expected, SunderRpcProviderSnapshot? value)
        => value is not null && value.State == SunderRpcProviderState.Active && expected.PackageId == value.PackageId && expected.PackageVersion == value.PackageVersion
           && expected.ProviderId == value.ProviderId && expected.ContractId == value.ContractId && expected.ContractVersion == value.ContractVersion
           && expected.DescriptorSha256 == value.DescriptorSha256 && expected.AbiSha256 == value.AbiSha256 && expected.ActivationId == value.ActivationId
           && expected.ActivationEpoch == value.ActivationEpoch && expected.SessionGeneration == value.SessionGeneration && expected.Endpoint == value.Endpoint;
    private static bool Matches(ResolverWire.AgentRpcProviderHandle expected, SunderRpcProviderSnapshot? value)
        => value is not null && value.State == SunderRpcProviderState.Active && expected.PackageId == value.PackageId && expected.PackageVersion == value.PackageVersion
           && expected.ProviderId == value.ProviderId && expected.ContractId == value.ContractId && expected.ContractVersion == value.ContractVersion
           && expected.DescriptorSha256 == value.DescriptorSha256 && expected.AbiSha256 == value.AbiSha256 && expected.ActivationId == value.ActivationId
           && expected.ActivationEpoch == value.ActivationEpoch && expected.SessionGeneration == value.SessionGeneration && expected.EndpointReference == value.Endpoint.Value;

    private async Task WatchAsync(bool resolver)
    {
        var api = new AgentPackageApi(rpc);
        long revision = 0, sequence = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in Changes().ConfigureAwait(false))
                {
                    revision = change.Revision; sequence = change.Sequence;
                    Operation[] active; lock (_gate) active = _operations.ToArray();
                    foreach (var operation in active)
                    {
                        var expected = resolver ? operation.Resolver : operation.Target;
                        if (change.Kind == SunderRpcCatalogEventKind.ResetRequired || expected is not null && change.Provider is { } provider
                            && expected.Endpoint == provider.Endpoint && (change.Kind is SunderRpcCatalogEventKind.Deactivated or SunderRpcCatalogEventKind.Removed or SunderRpcCatalogEventKind.Faulted || !Matches(expected, provider)))
                            operation.Retire(resolver);
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is SunderRpcException or OperationCanceledException) { }
            Operation[] invalid; lock (_gate) invalid = _operations.ToArray();
            foreach (var operation in invalid) operation.Retire(resolver);
            try { await Task.Delay(250, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
        async IAsyncEnumerable<Change> Changes()
        {
            if (resolver)
                await foreach (var item in api.CSunder_Agent_Workspace_Execution_Resolver.WatchAsync(revision, sequence, _lifetime.Token).ConfigureAwait(false))
                    yield return new(item.Revision, item.Sequence, item.Kind, item.Provider?.Snapshot);
            else
                await foreach (var item in api.CSunder_Agent_Execution_Target.WatchAsync(revision, sequence, _lifetime.Token).ConfigureAwait(false))
                    yield return new(item.Revision, item.Sequence, item.Kind, item.Provider?.Snapshot);
        }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_stop is not null) return new(_stop);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _stop = completion.Task; _stopped = true;
            if (_operations.Count == 0) _drained.TrySetResult();
        }
        _ = StopAsync(completion); return new(completion.Task);
    }
    private async Task StopAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { try { await _lifetime.CancelAsync().ConfigureAwait(false); } finally { await Task.WhenAll(_watch, _drained.Task).ConfigureAwait(false); } }
        catch (Exception error) { failure = error; }
        finally { _lifetime.Dispose(); }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }
    private sealed class Operation : IDisposable
    {
        private readonly CancellationTokenSource _retirement;
        private int _resolverRetired;
        internal Operation(CancellationToken lifetime)
        { _retirement = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Token = _retirement.Token; }
        internal volatile SunderRpcProviderSnapshot? Resolver;
        internal volatile SunderRpcProviderSnapshot? Target;
        internal CancellationToken Token { get; }
        internal bool ResolverRetired => Volatile.Read(ref _resolverRetired) != 0;
        internal void Retire(bool resolver)
        {
            if (resolver) Interlocked.Exchange(ref _resolverRetired, 1);
            try { _retirement.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
        }
        public void Dispose() => _retirement.Dispose();
    }
    private sealed record Change(long Revision, long Sequence, SunderRpcCatalogEventKind Kind, SunderRpcProviderSnapshot? Provider);
}
