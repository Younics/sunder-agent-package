using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text.Json;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentWorkspaceAndPermissionConsumers(string root)
    {
        var load = new AssemblyLoadContext("agent-workspace-permission-consumers", isCollectible: true);
        try
        {
            var owner = Load("Worker", "Sunder.Package.Agent");
            var local = Load("LocalWorker", "Sunder.Package.Agent.Execution.Local");
            var docker = Load("DockerWorker", "Sunder.Package.Agent.Execution.Docker");
            var files = Load("FilesWorker", "Sunder.Package.Agent.Tools.Files");
            var shell = Load("ShellWorker", "Sunder.Package.Agent.Tools.Shell");
            using var probeBytes = File.OpenRead(Path.Combine(root, "LocalWorkerProbes/bin/Debug/net10.0/Sunder.Package.Agent.Execution.Local.SemanticProbes.dll"));
            var localProbes = load.LoadFromStream(probeBytes);
            Assert.NotSame(local.GetType("Sunder.Agent.Execution.Common.HostMutationCoordinator"), docker.GetType("Sunder.Agent.Execution.Common.HostMutationCoordinator"));
            await VerifyAgentWorkspacePaths(owner, local, docker);
            await VerifyAgentWorkspacePathLifetime(owner, local, localProbes);
            await VerifyAgentPermissionSurfaces(owner, files, shell);
            await VerifyAgentToolProfiles(owner);
            await VerifyAgentPromptContext(owner);
            await VerifyAgentExecutionConsumer(owner);
            await VerifyAgentFilesPromptContext(owner, files, local);
        }
        finally { load.Unload(); }
        Assembly Load(string directory, string name)
        {
            var output = SingleProjectRoleOutput(Path.Combine(root, directory), "worker");
            using var bytes = File.OpenRead(Path.Combine(output, name + ".dll"));
            return load.LoadFromStream(bytes);
        }
    }

    private static object? SurfaceCall(object instance, string method, params object?[] arguments)
        => AgentRoleBoundary.AssertionValue(AgentRoleBoundary.Call(instance, method, arguments));

    private static SurfaceHandler AddSurfaceProvider(RegressionTestRpcState state, string packageId, SunderWorkerProviderRegistration registration)
    {
        var probe = new SurfaceHandler(registration.Handler);
        state.Add(packageId, RegressionTestRpcDescriptors.Get(registration.ContractId), probe, probe, Noop.Instance, registration.ProviderId, "2.0.0");
        return probe;
    }

    private static SunderWorkerProviderRegistration SurfaceRegistration(object service)
        => (SunderWorkerProviderRegistration)SurfaceCall(service, "CreateSunderRpcProviderRegistration")!;

    private sealed class SurfaceHandler(ISunderRpcServiceHandler inner) : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        internal string? BlockMethod;
        internal FilesCallGate? Gate;
        internal Action<string, JsonElement>? Before;
        internal Func<string, JsonElement, JsonElement>? Transform;
        internal string? FailMethod;
        internal int Streams;
        internal ConcurrentQueue<(string Method, JsonElement Request)> Calls { get; } = new();
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors => ((ISunderRpcDeclaredErrorMetadata)inner).DeclaredErrors;
        public async ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
        {
            Calls.Enqueue((methodId, request.Clone()));
            if (methodId == BlockMethod && Gate is { } gate)
            {
                gate.Started.TrySetResult();
                try { await gate.Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { gate.Cancelled.TrySetResult(); throw; }
            }
            Before?.Invoke(methodId, request);
            if (methodId == FailMethod) throw new InvalidOperationException("private-surface-failure");
            var value = await inner.InvokeUnaryAsync(context, serviceId, methodId, request, token);
            return Transform?.Invoke(methodId, value) ?? value;
        }
        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
        { Interlocked.Increment(ref Streams); return inner.InvokeServerStreamAsync(context, serviceId, methodId, request, token); }
    }
}
