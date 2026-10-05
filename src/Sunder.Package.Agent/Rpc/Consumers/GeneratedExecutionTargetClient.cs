using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Execution_Target;
using Wire = Sunder.Generated.Rpc.ExecutionTarget;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed partial class GeneratedExecutionTargetClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentProcessExecutionTarget, IAgentExecutionResourceResolver, IAgentResourceAuthorityExecutionTarget,
        IAgentExecutionScopeProvider, IAgentExecutionPathMapper, IAgentExecutionPathEnvironment,
        IAgentRangedFileExecutionTarget, IAgentStructuredFileSearchExecutionTarget, IAgentScopedInstructionDiscoveryTarget,
        IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.execution.provider-stale");
    private AgentExecutionTargetDescriptor? _descriptor;

    public AgentExecutionTargetDescriptor Descriptor => DescribeAsync().AsTask().GetAwaiter().GetResult();
    internal async ValueTask<AgentExecutionTargetDescriptor> DescribeAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _calls.ThrowIfStopped();
        var descriptor = Volatile.Read(ref _descriptor);
        if (descriptor is null)
        {
            var value = await Invoke(async (client, cancellation) => AgentConsumerValues.Convert<AgentExecutionTargetDescriptor>(
                await client.DescribeAsync(new(), cancellation).ConfigureAwait(false)), token).ConfigureAwait(false);
            descriptor = Interlocked.CompareExchange(ref _descriptor, value, null) ?? value;
        }
        return descriptor with { Facets = descriptor.Facets.ToArray() };
    }

    public ValueTask<string?> GetConfigurationGenerationAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => Invoke((client, token) => client.ConfigurationGenerationAsync(AgentExecutionValues.Context(context), token), cancellationToken);
    public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => AgentConsumerValues.Convert<AgentExecutionTargetReadiness>(await client.ReadinessAsync(AgentExecutionValues.Context(context), token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => AgentConsumerValues.Convert<AgentExecutionShellDescriptor>(await client.ShellAsync(AgentExecutionValues.Context(context), token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => AgentExecutionValues.Resource(await client.ResolveFileAsync(new() { Context = AgentExecutionValues.Context(context), Path = path }, token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => AgentConsumerValues.Convert<AgentShellCommandResult>(await client.ExecuteShellAsync(new() { Context = AgentExecutionValues.Context(context), Request = AgentConsumerValues.Convert<Wire.AgentShellCommandRequest>(request) }, token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => AgentConsumerValues.Convert<AgentFileReadResult>(await client.ReadFileAsync(new() { Context = AgentExecutionValues.Context(context), Request = AgentConsumerValues.Convert<Wire.AgentFileReadRequest>(request) }, token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => AgentExecutionValues.Mutation(await client.WriteFileAsync(new() { Context = AgentExecutionValues.Context(context), Request = AgentConsumerValues.Convert<Wire.AgentFileWriteRequest>(request) }, token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => AgentExecutionValues.Mutation(await client.DeleteFileAsync(new() { Context = AgentExecutionValues.Context(context), Request = AgentConsumerValues.Convert<Wire.AgentFileDeleteRequest>(request) }, token).ConfigureAwait(false)), cancellationToken);

    private ValueTask<T> Invoke<T>(Func<Wire.ExecutionTargetClient, CancellationToken, ValueTask<T>> callback, CancellationToken token)
        => _calls.Invoke((scope, cancellation) => callback(new PackageApi(scope).ExecutionTarget.CreateClient(scope, new Api.Provider(provider)).SExecution_2dtarget, cancellation), token);
    private async ValueTask<T> InvokeFacet<T>(string facet, Func<Wire.ExecutionTargetClient, CancellationToken, ValueTask<T>> callback, CancellationToken token)
    {
        if (!(await DescribeAsync(token).ConfigureAwait(false)).SupportsFacet(facet))
            throw new NotSupportedException($"Execution target facet '{facet}' is not advertised.");
        return await Invoke(callback, token).ConfigureAwait(false);
    }

    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
