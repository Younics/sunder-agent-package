using System.Collections.Concurrent;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private sealed class BuilderArtifactResolver(AgentWorkspaceRecord workspace, AgentWorkspaceBindingRecord binding, AgentExecutionTargetDescriptor target, AgentExecutionScopeDescriptor scope)
        : IAgentWorkspaceExecutionResolver
    {
        internal AgentRpcProviderHandle Handle = null!;
        internal Func<AgentRpcProviderHandle, AgentRpcProviderHandle>? Transform;
        internal int CatalogCount = 1;
        internal FilesCallGate? BlockList;
        public async ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken token = default)
        {
            if (BlockList is { } blocked)
            {
                blocked.Started.TrySetResult();
                try { await blocked.Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { blocked.Cancelled.TrySetResult(); throw; }
            }
            return Enumerable.Range(0, CatalogCount).Select(index => index == 0 ? workspace : workspace with
            { WorkspaceId = "workspace." + index, DisplayName = new string('x', 1000), Paths = [] }).ToArray();
        }
        public ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(string workspaceId, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Assert.Equal(workspace.WorkspaceId, workspaceId);
            return ValueTask.FromResult(new AgentWorkspaceExecutionResolution(workspace, binding, target, scope, null!)
            { ExecutionTargetHandle = Transform?.Invoke(Handle) ?? Handle });
        }
    }

    private sealed class BuilderProcessProbe(ISunderRpcServiceHandler inner, string workspace) : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        internal FilesCallGate? BlockProcess;
        internal bool FailBuild;
        internal bool EmptyFacets;
        internal ConcurrentQueue<AgentProcessCommandRequest> Processes { get; } = new();
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors => ((ISunderRpcDeclaredErrorMetadata)inner).DeclaredErrors;
        public async ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
        {
            if (methodId == "describe" && EmptyFacets)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse((await inner.InvokeUnaryAsync(context, serviceId, methodId, request, token)).GetRawText())!;
                node["facets"] = new System.Text.Json.Nodes.JsonArray(); return JsonSerializer.SerializeToElement(node);
            }
            if (methodId != "execute-process") return await inner.InvokeUnaryAsync(context, serviceId, methodId, request, token);
            var process = request.GetProperty("request").Deserialize<AgentProcessCommandRequest>(MemoryProviderJson)!;
            Processes.Enqueue(process);
            if (BlockProcess is { } blocked)
            {
                blocked.Started.TrySetResult();
                try { await blocked.Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { blocked.Cancelled.TrySetResult(); throw; }
            }
            if (process.FileName != "dotnet") return await inner.InvokeUnaryAsync(context, serviceId, methodId, request, token);
            var args = process.Arguments;
            string output;
            var exit = 0;
            if (args.SequenceEqual(new[] { "--list-sdks" })) output = "10.0.100 [artifact-sdk]";
            else if (args.SequenceEqual(new[] { "new", "list", "sunder-package" })) output = "sunder-package";
            else if (args.Take(2).SequenceEqual(new[] { "new", "sunder-package" }))
            {
                var index = Array.IndexOf(args.ToArray(), "--output"); Assert.True(index >= 0);
                var directory = Path.GetFullPath(args[index + 1]);
                Assert.StartsWith(Path.GetFullPath(workspace) + Path.DirectorySeparatorChar, directory, StringComparison.Ordinal);
                Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "Artifact.csproj"), "<Project />"); output = "Template created.";
            }
            else if (args.Count == 2 && args[0] is "restore" or "build")
            {
                Assert.True(Directory.Exists(args[1]));
                if (FailBuild && args[0] == "build") { exit = 1; output = "Controlled build failed."; }
                else output = args[0] == "build" ? "Build succeeded." : "Restore completed.";
            }
            else if (args.SequenceEqual(new[] { "msbuild", "-t:AcceptSunderContracts", "-p:SunderContractKey=sample", "-p:SunderContractNextVersion=2.0.0" }))
            { Assert.True(Directory.Exists(process.WorkingDirectory)); output = "Initial contract accepted."; }
            else if (args.SequenceEqual(new[] { "msbuild", "-t:CheckSunderContracts;FreezeSunderContracts;PackSunderPackage", "-p:Configuration=Release" }))
            { Assert.True(Directory.Exists(process.WorkingDirectory)); output = "Package archive created."; }
            else throw new InvalidOperationException("Unexpected Builder process: " + string.Join(" ", args));
            return JsonSerializer.SerializeToElement(new AgentShellCommandResult(exit, output), MemoryProviderJson);
        }
        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
            => inner.InvokeServerStreamAsync(context, serviceId, methodId, request, token);
    }

    private sealed class BuilderArtifactQueue : IBackgroundProcessQueue
    {
        public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged { add { } remove { } }
        public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request) => throw new InvalidOperationException("The artifact invokes explicit application-service operations.");
        public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null) => [];
        public bool Cancel(Guid processId) => false;
    }
}
