using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static readonly JsonSerializerOptions ExecutionJson = RegressionTestRpcValues.Json;

    private static async Task VerifyExecutionTargetAsync(string root, AssemblyLoadContext loadContext, Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        var probe = new ExecutionTargetProbe();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.builder");
        await using var receiver = new GeneratedServiceTestScope(rpc);
        var implementation = probe;
        using var sources = new RegressionTestSourceProviders();
        var registration = sources.Create(descriptor.ContractId, implementation, receiver);
        state.Add("test.execution.backend", descriptor, implementation, registration.Handler, EmptyDisposable.Instance, "test.execution.target", "2.0.0");
        var selected = Assert.Single((await receiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers);
        var client = selected.Service;

        var domain = CreateResolver(selected.Reference).Resolution;
        var context = JsonSerializer.SerializeToElement(new
        {
            approvedResourceCapabilities = Array.Empty<string>(),
            capturePostMutationResource = false,
            context = new
            {
                allowOutsideConfiguredScope = false,
                approvedResourceClaims = Array.Empty<object>(),
                approvedResourceReferences = Array.Empty<string>(),
                binding = domain.Binding,
                workspace = domain.Workspace,
            },
        }, ExecutionJson);
        probe.Replies["DescribeAsync"] = """
            {"description":"Test target","displayName":"Generated target","facets":["execution-scope"],"supportsFiles":true,"supportsShell":true,"targetId":"target.one","targetKind":"local"}
            """;
        probe.Replies["ConfigurationGenerationAsync"] = "null";
        probe.Replies["ReadinessAsync"] = """
            {"message":"Configure first","status":"needs-configuration","targetId":"target.one","targetKind":"local"}
            """;
        probe.Replies["ReadFileAsync"] = """
            {"content":"Generated content 漢字","isDirectory":false,"isError":false,"path":"/work/one.txt","wasTruncated":false}
            """;
        var targetContext = new AgentExecutionTargetContext(null, null, domain.Workspace, domain.Binding!);
        var description = await client.DescribeAsync();
        Assert.Equal("Generated target", description.DisplayName);
        Assert.Null(await client.ConfigurationGenerationAsync(targetContext));
        var readiness = await client.ReadinessAsync(targetContext);
        Assert.Equal(AgentExecutionTargetReadinessStatus.NeedsConfiguration, readiness.Status);
        var read = JsonSerializer.SerializeToElement(new { context, request = new { path = "/work/one.txt", offset = 0, limit = 2000 } });
        var result = await client.ReadFileAsync(targetContext, read.GetProperty("request").Deserialize<AgentFileReadRequest>(ExecutionJson)!);
        Assert.Equal("Generated content 漢字", result.Content);
        Assert.Null(result.EndLine);
        Assert.False(result.WasTruncated);
        var validGeneratedRead = state.LastInvocationRequest;
        var calls = probe.Calls;
        var peerCalls = state.InvocationAttemptCount;
        var invalid = JsonSerializer.SerializeToElement(new { context, request = new { path = "/work/one.txt", limit = 2001 } });
        await Assert.ThrowsAsync<JsonException>(() => client.ReadFileAsync(targetContext,
            invalid.GetProperty("request").Deserialize<AgentFileReadRequest>(ExecutionJson)!).AsTask());
        Assert.Equal(peerCalls, state.InvocationAttemptCount);
        Assert.Equal(calls, probe.Calls);
        var invalidWire = JsonNode.Parse(validGeneratedRead.GetRawText())!.AsObject();
        invalidWire["request"]!["limit"] = 2001;
        var source = Assert.Single(state.Discover(descriptor.ContractId).Providers).Endpoint;
        var invalidError = await Assert.ThrowsAsync<SunderRpcException>(() => rpc.InvokeAsync(source,
            "execution-target", "read-file", JsonSerializer.SerializeToElement(invalidWire)).AsTask());
        Assert.Equal(SunderRpcErrorKind.Validation, invalidError.Error.Kind);
        Assert.Equal(peerCalls + 1, state.InvocationAttemptCount);
        Assert.Equal(calls, probe.Calls);

        probe.FacetFailure = true;
        var process = JsonSerializer.SerializeToElement(new { context, request = new { arguments = Array.Empty<string>(), fileName = "tool" } });
        var facet = await Assert.ThrowsAsync<SunderRpcException>(() => client.ExecuteProcessAsync(targetContext,
            process.GetProperty("request").Deserialize<AgentProcessCommandRequest>(ExecutionJson)!).AsTask());
        Assert.Equal(SunderRpcErrorKind.Domain, facet.Error.Kind);
        Assert.Equal("agent.execution.facet-unavailable", facet.Error.Code);
        var forged = await Assert.ThrowsAsync<SunderRpcException>(() => client.ReadFileAsync(targetContext,
            read.GetProperty("request").Deserialize<AgentFileReadRequest>(ExecutionJson)!).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, forged.Error.Kind);
        probe.FacetFailure = false;

        using var cancellation = new CancellationTokenSource();
        probe.WaitForCancellation = true;
        var pending = client.ReadinessAsync(targetContext, cancellation.Token).AsTask();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class ExecutionTargetProbe : IAgentExecutionTarget
    {
        public Dictionary<string, string> Replies { get; } = new(StringComparer.Ordinal);
        public int Calls { get; private set; }
        public bool FacetFailure { get; set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentExecutionTargetDescriptor Descriptor => Reply<AgentExecutionTargetDescriptor>("DescribeAsync", default).AsTask().GetAwaiter().GetResult();
        public ValueTask<string?> GetConfigurationGenerationAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => Reply<string?>("ConfigurationGenerationAsync", cancellationToken);
        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => Reply<AgentExecutionTargetReadiness>("ReadinessAsync", cancellationToken);
        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => Reply<AgentExecutionShellDescriptor>("ShellAsync", cancellationToken);
        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => Reply<AgentResolvedResource>("ResolveFileAsync", cancellationToken);
        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => Reply<AgentShellCommandResult>("ExecuteShellAsync", cancellationToken);
        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => Reply<AgentFileReadResult>("ReadFileAsync", cancellationToken);
        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => Reply<AgentFileMutationResult>("WriteFileAsync", cancellationToken);
        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => Reply<AgentFileMutationResult>("DeleteFileAsync", cancellationToken);

        private async ValueTask<T> Reply<T>(string method, CancellationToken token)
        {
            Calls++;
            if (WaitForCancellation)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            if (FacetFailure)
                throw new SunderRpcException(new SunderRpcError(SunderRpcErrorKind.Domain,
                    "agent.execution.facet-unavailable", "Optional facet is unavailable."));
            return JsonSerializer.Deserialize<T>(Replies[method], ExecutionJson)!;
        }
    }
}
