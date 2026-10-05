using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Wire = Sunder.Generated.Rpc.ExecutionTarget;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static Assembly LoadExecutionOwner(string worker, AssemblyLoadContext load)
    {
        using var bytes = File.OpenRead(Path.Combine(SingleProjectRoleOutput(Path.Combine(Path.GetDirectoryName(worker)!, "Worker"), "worker"), "Sunder.Package.Agent.dll"));
        return load.LoadFromStream(bytes);
    }

    // The established provider behavior scenarios use frozen wire values as their
    // oracle. Bridge those values only in tests to exercise the owning domain client.
    private static async Task<JsonElement> InvokeAgentExecution(IAgentExecutionTarget client, string method, object request, CancellationToken token)
    {
        var json = JsonSerializer.SerializeToElement(request, MemoryProviderJson);
        var nested = json.TryGetProperty("context", out var context) && context.TryGetProperty("context", out _);
        var execution = method is "describe" or "release-resource-authority" ? null
            : RegressionTestExecutionProvider.Context((nested ? context : json).Deserialize<Wire.AgentExecutionTargetContextWire>(MemoryProviderJson)!);
        object? result = method switch
        {
            "describe" => client.Descriptor,
            "configuration-generation" => await client.GetConfigurationGenerationAsync(execution!, token),
            "readiness" => await client.GetReadinessAsync(execution!, token),
            "shell" => await client.GetShellAsync(execution!, token),
            "resolve-file" => RegressionTestExecutionProvider.Resource(await client.ResolveFileResourceAsync(execution!, json.GetProperty("path").GetString()!, token)),
            "execute-shell" => await client.ExecuteShellAsync(execution!, RegressionTestRpcValues.Convert<AgentShellCommandRequest>(Read<Wire.ExecutionShellRequest>().Request), token),
            "read-file" => await client.ReadFileAsync(execution!, RegressionTestRpcValues.Convert<AgentFileReadRequest>(Read<Wire.ExecutionFileReadRequest>().Request), token),
            "write-file" => RegressionTestExecutionProvider.Mutation(await client.WriteFileAsync(execution!, RegressionTestRpcValues.Convert<AgentFileWriteRequest>(Read<Wire.ExecutionFileWriteRequest>().Request), token)),
            "delete-file" => RegressionTestExecutionProvider.Mutation(await client.DeleteFileAsync(execution!, RegressionTestRpcValues.Convert<AgentFileDeleteRequest>(Read<Wire.ExecutionFileDeleteRequest>().Request), token)),
            "execute-process" => await ((IAgentProcessExecutionTarget)client).ExecuteProcessAsync(execution!, RegressionTestRpcValues.Convert<AgentProcessCommandRequest>(Read<Wire.ExecutionProcessRequest>().Request), token),
            "resolve-resources" => await ((IAgentExecutionResourceResolver)client).ResolveResourcesAsync(execution!, RegressionTestRpcValues.Convert<AgentExecutionResourceDescriptor[]>(Read<Wire.ExecutionResourcesRequest>().Resources), token),
            "validate-resource-authority" => await ((IAgentResourceAuthorityExecutionTarget)client).ValidateResourceAuthorityAsync(execution!, token),
            "release-resource-authority" => Release(),
            "execution-scope" => await ((IAgentExecutionScopeProvider)client).GetExecutionScopeAsync(execution!, token),
            "map-path" => await ((IAgentExecutionPathMapper)client).MapToHostPathAsync(execution!, Read<Wire.ExecutionPathRequest>().Path, token),
            "list-path" => await ((IAgentExecutionPathEnvironment)client).ListPathEntriesAsync(execution!, token),
            "add-path" => await AddPath(),
            "structured-file-search" => await ((IAgentStructuredFileSearchExecutionTarget)client).ExecuteFileSearchAsync(execution!, RegressionTestRpcValues.Convert<AgentFileSearchRequest>(Read<Wire.ExecutionFileSearchRequest>().Request), token),
            "discover-instructions" => await ((IAgentScopedInstructionDiscoveryTarget)client).DiscoverScopedInstructionsAsync(execution!, RegressionTestRpcValues.Convert<AgentScopedInstructionDiscoveryRequest>(Read<Wire.ExecutionInstructionDiscoveryRequest>().Request), token),
            _ => throw new InvalidOperationException("Unknown execution scenario: " + method),
        };
        return JsonSerializer.SerializeToElement(result, result?.GetType() ?? typeof(object), MemoryProviderJson);
        T Read<T>() => json.Deserialize<T>(MemoryProviderJson)!;
        object Release() { token.ThrowIfCancellationRequested(); ((IAgentResourceAuthorityExecutionTarget)client).ReleaseResourceAuthority(Read<Wire.ResourceCapabilitiesRequest>().Capabilities); return new { }; }
        async Task<object> AddPath() { await ((IAgentExecutionPathEnvironment)client).AddPathEntryAsync(execution!, Read<Wire.ExecutionPathRequest>().Path, token); return new { }; }
    }
}
