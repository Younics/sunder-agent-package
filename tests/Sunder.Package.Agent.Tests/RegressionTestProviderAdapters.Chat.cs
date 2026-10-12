using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Tests;

// Reuses the reviewed Worker stream implementation; the generated fixture still
// owns the RPC surface and projects its imported stream events at the boundary.
internal sealed class RegressionTestChatProvider(IAgentChatProvider provider, GeneratedServiceTestScope receiver,
    RegressionTestGeneratedProviderProjection projection) : AgentServices.IChatProviderService
{
    public ValueTask<AgentProviderDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(provider.Descriptor); }
    public ValueTask<IReadOnlyList<AgentModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
        => provider.GetAvailableModelsAsync(cancellationToken);
    public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
        => provider.GetReadinessAsync(cancellationToken);
    public ValueTask<AgentProviderRunCapabilities> GetCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
        => provider.GetRunCapabilitiesAsync(modelId, cancellationToken);
    public ValueTask<string?> ResolveUtilityModelAsync(CancellationToken cancellationToken = default)
        => provider is IAgentUtilityModelProvider utility ? utility.ResolveUtilityModelIdAsync(cancellationToken) : ValueTask.FromResult<string?>(null);
    public async IAsyncEnumerable<AgentChatStreamEvent> StreamChatAsync(AgentChatStreamRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Resolve the qualified role lazily: linked harness consumers must not
        // require a compile-time alias or friend access to a provider's internals.
        // The real shared provider stream still owns every streaming operation.
        var assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(value => value.GetName().Name == "Sunder.Package.Agent.Provider.Gemini")
            ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot,
                "src/Sunder.Package.Agent.Provider.Gemini/bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
                "net10.0/sunder-roles/worker", RuntimeInformation.RuntimeIdentifier, "Sunder.Package.Agent.Provider.Gemini.dll"));
        var read = assembly.GetType("Sunder.Package.Agent.Provider.Shared.ProviderChatStream", true)!
            .GetMethod("ReadAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var parameters = read.GetParameters();
        var factory = GetType().GetMethod(nameof(CreateClientFactory), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(parameters[0].ParameterType.GenericTypeArguments[0]).Invoke(this, null)!;
        var projected = await projection.ProjectAsync(request, parameters[1].ParameterType, receiver, cancellationToken).ConfigureAwait(false);
        IAsyncEnumerable<object> stream;
        try { stream = (IAsyncEnumerable<object>)read.Invoke(null, [factory, projected, cancellationToken])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return (AgentChatStreamEvent)(await projection.ProjectAsync(item, typeof(AgentChatStreamEvent), receiver, cancellationToken).ConfigureAwait(false))!;
    }

    private Func<TContext, CancellationToken, ValueTask<IChatClient>> CreateClientFactory<TContext>()
        => (context, token) => provider.CreateChatClientAsync(AgentRoleBoundary.As<AgentChatClientContext>(context!), token);
}
