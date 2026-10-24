using System.Reflection;
using System.Runtime.Loader;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static async Task VerifyAgentToolConsumers(Assembly owner, Assembly app)
    {
        Assert.Null(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedToolSourceClient"));
        Assert.Null(app.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedToolSourceClient"));
        await VerifyAgentObservedTools();
    }

    private static async Task VerifyScriptedToolProvider(string root, string core, string driver, string archive, string cache)
    {
        var fixture = Directory.CreateDirectory(Path.Combine(root, "ScriptedToolProvider")).FullName;
        WriteImportProject(fixture, core, driver, cache, archive, provider: true, packageId: "example.agent.scripted.tools", assemblyName: "ScriptedToolProvider");
        File.Copy(Path.Combine(core, "specs/rpc/v1/authoring/conformance/valid/minimal-contract-lock.json"), Path.Combine(fixture, "sunder-contracts.lock.json"));
        File.WriteAllText(Path.Combine(fixture, "Source.cs"), "public sealed class Seed;");
        await Run(fixture, "restore", "Imported.csproj", "--disable-parallel", "-p:BuildProjectReferences=false");
        await Run(fixture, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Accept", "-p:BuildProjectReferences=false");
        await Run(fixture, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Freeze", "-p:BuildProjectReferences=false");
        await Run(fixture, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Add", "-p:BuildProjectReferences=false");
        File.WriteAllText(Path.Combine(fixture, "Source.cs"), ScriptedToolProviderSource);
        await Run(fixture, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Implement", "-p:ContractId=" + RegressionTestBindings.ToolSources,
            "-p:ProviderKey=scriptedTools", "-p:BuildProjectReferences=false");
        await Run(fixture, "build", "Imported.csproj", "--no-restore", "-p:BuildProjectReferences=false");

        var load = new AssemblyLoadContext("scripted-tool-provider", isCollectible: true);
        load.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), name));
        try
        {
            var module = load.LoadFromAssemblyPath(Path.Combine(fixture, "bin/Debug/net10.0/ScriptedToolProvider.dll"));
            var controlled = await BuildControlledProvidersAsync(root, core, driver, archive, cache);
            var controlledModule = load.LoadFromAssemblyPath(controlled);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, "younics.ai.agent");
            await using var targetReceiver = new GeneratedServiceTestScope(rpc.ForCaller("example.controlled.target"));
            await using var providerReceiver = new GeneratedServiceTestScope(rpc.ForCaller("example.agent.scripted.tools"));
            await using var receiver = new GeneratedServiceTestScope(rpc);
            var target = Activator.CreateInstance(controlledModule.GetType("ControlledNaturalProviders.ControlledTarget", true)!)!;
            var provider = Activator.CreateInstance(module.GetType("ScriptedToolProvider.ScriptedToolProvider", true)!)!;
            var targetRegistration = targetReceiver.Register(target, RegressionTestBindings.ExecutionTargets);
            var registration = providerReceiver.Register(provider, RegressionTestBindings.ToolSources);
            state.AddGenerated("example.controlled.target", target, targetRegistration, Noop.Instance, targetRegistration.ProviderId);
            state.AddGenerated("example.agent.scripted.tools", provider, registration, Noop.Instance, registration.ProviderId);
            var targetReference = Assert.Single((await receiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers).Reference;
            var client = Assert.Single((await receiver.Providers<AgentServices.IToolSourceService>().ListAsync()).Providers).Service;
            var seed = AgentPublicSourceMigrationTests.CreatePromptRequest();
            var claim = new AgentResourceClaim(1, "fixture.claim", "/workspace/file.txt", "/workspace", new('a', 64), true, "file", "fixture.identity", false, true,
                "files.mutate", "workspace", "workspace-generation", "binding", "binding-generation", "call.tools", 7, "example.tools", "example.target");
            var source = new AgentToolSourceContext(seed.Session.SessionId, seed.Profile, seed.Workspace, seed.ExecutionBinding)
            {
                ExecutionTarget = targetReference,
                ExecutionTargetConfigurationGeneration = "captured-generation",
            };
            var execution = new AgentToolExecutionContext(seed.Session.SessionId, seed.Profile!.ProfileId, seed.Workspace, seed.ExecutionBinding, true,
                seed.Run.RunId, seed.Run.Revision, Guid.NewGuid(), "call.tools")
            {
                TranscriptEpoch = 11,
                ApprovedResourceReferences = ["durable"],
                ApprovedResourceClaims = [claim],
                ApprovedResourceCapabilities = ["transient"],
                ExecutionTarget = targetReference,
                ExecutionTargetConfigurationGeneration = "captured-generation",
                ResourceOperation = new(seed.Run.RunId, 7, "call.tools", "files.mutate", -1, "workspace-generation", "binding-generation",
                    "example.tools", "example.target", "activation", 2, true),
            };
            var request = new AgentToolRequest("fixture.tool", "{opaque arguments 漢字");
            var display = new AgentToolPresentationRequest(request.ToolId, request.ArgumentsJson, "summary", "output", "{opaque structured", "[opaque sources", true, "fixture.error", "fixture.backend");
            Assert.Equal("fixture.tools", (await client.DescribeAsync()).SourceId);
            Assert.Equal("fixture.tool", Assert.Single(await client.ListToolsAsync(source)).ToolId);
            Assert.Equal("ready", (await client.GetReadinessAsync(request.ToolId, source))!.Status.ToString().ToLowerInvariant());
            Assert.Equal("Completed 漢字", (await client.PreflightAsync(execution, request))!.Summary);
            var permission = (await client.PermissionAsync(execution, request))!;
            Assert.Equal("files.mutate", permission.ActionId); Assert.Equal("outside-configured-scope", permission.BoundaryId);
            Assert.Equal("Mutation", permission.Summary); Assert.Equal(request.ToolId, permission.ToolId);
            Assert.Equal("fixture command", permission.Command); Assert.Equal(claim.LogicalPath, permission.Path);
            Assert.Equal(seed.Workspace!.WorkspaceId, permission.WorkspaceId); Assert.Equal(seed.ExecutionBinding!.BindingId, permission.BindingId);
            Assert.Equal("Fixture resource", permission.ResourceDisplayName); Assert.Equal("durable", permission.ResourceReference);
            Assert.True(permission.IsMutation); Assert.Equal(AgentPermissionScopeClassificationBasis.VerifiedTargetMapping, permission.ScopeClassificationBasis);
            Assert.Equal(new[] { "durable" }, permission.ResourceReferences); Assert.Equal(new[] { "transient" }, permission.ResourceCapabilities);
            Assert.Equal(claim.LogicalPath, Assert.Single(permission.ResourceClaims).LogicalPath);
            Assert.Equal("Completed 漢字", (await client.ExecuteAsync(execution, request)).Summary);
            var presentation = (await client.PresentationAsync(display))!;
            Assert.Equal("Header", presentation.HeaderText); Assert.Equal("Details", presentation.DetailMarkdown); Assert.Equal("Output", presentation.OutputText);
            var received = provider.GetType().GetProperty("LastExecution")!.GetValue(provider)!;
            Assert.Equal(execution.SessionId, Property(received, "SessionId")); Assert.Equal(execution.ProfileId, Property(received, "ProfileId"));
            Assert.Equal(execution.Workspace!.WorkspaceId, Property(Property(received, "Workspace")!, "WorkspaceId"));
            Assert.Equal(execution.ExecutionBinding!.BindingId, Property(Property(received, "ExecutionBinding")!, "BindingId"));
            Assert.Equal(execution.AllowOutsideConfiguredScope, Property(received, "AllowOutsideConfiguredScope"));
            Assert.Equal(execution.RunId, Property(received, "RunId")); Assert.Equal(execution.RunRevision, Property(received, "RunRevision"));
            Assert.Equal(execution.UserTurnId, Property(received, "UserTurnId")); Assert.Equal(execution.ToolCallId, Property(received, "ToolCallId"));
            Assert.Equal(execution.TranscriptEpoch, Property(received, "TranscriptEpoch"));
            Assert.Equal(execution.ApprovedResourceReferences, (IReadOnlyList<string>)Property(received, "ApprovedResourceReferences")!);
            Assert.Equal(execution.ApprovedResourceCapabilities, (IReadOnlyList<string>)Property(received, "ApprovedResourceCapabilities")!);
            Assert.Equal(execution.ExecutionTargetConfigurationGeneration, Property(received, "ExecutionTargetConfigurationGeneration"));
            Assert.NotNull(Property(received, "ExecutionTarget")); Assert.NotNull(Property(received, "ResourceOperation"));
            Assert.Equal(claim.LogicalPath, Property(((System.Collections.IEnumerable)Property(received, "ApprovedResourceClaims")!).Cast<object>().Single(), "LogicalPath"));
            Assert.Equal(execution.ResourceOperation!.AuthorityActivationId, Property(Property(received, "ResourceOperation")!, "AuthorityActivationId"));
            var presented = provider.GetType().GetProperty("LastPresentation")!.GetValue(provider)!;
            Assert.Equal(display.ToolId, Property(presented, "ToolId"));
            Assert.Equal(display.ArgumentsJson, Property(presented, "ArgumentsJson"));
            Assert.Equal(display.StructuredPayloadJson, Property(presented, "StructuredPayloadJson"));
            Assert.Equal(display.SourcesJson, Property(presented, "SourcesJson"));
        }
        finally { load.Unload(); }

        static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
    }

    private const string ScriptedToolProviderSource = """
        using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
        using IToolSourceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService;
        using Sunder.Sdk.Services;
        namespace ScriptedToolProvider;
        [SunderServiceProvider(typeof(IToolSourceService), ProviderId = "fixture.scripted.tools")]
        public sealed partial class ScriptedToolProvider : IToolSourceService
        {
            public AgentToolExecutionContext? LastExecution { get; private set; }
            public AgentToolPresentationRequest? LastPresentation { get; private set; }
            public ValueTask<AgentToolSourceDescriptor> DescribeAsync(CancellationToken token = default)
                => ValueTask.FromResult(new AgentToolSourceDescriptor { SourceId = "fixture.tools", DisplayName = "Fixture tools", SourceKind = "local" });
            public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken token = default)
                => ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([new() { ToolId = "fixture.tool", DisplayName = "Fixture", Description = "Static package description", IsReadOnly = false, RequiresNetwork = true, ArgumentsJsonSchema = "{opaque schema", SourceKind = "local", SourceId = "fixture.tools", SourceDisplayName = "Fixture tools", Aliases = null, SelectionScope = AgentToolSelectionScope.Tool, SelectionGroupId = null, SelectionGroupDisplayName = null, SelectionGroupDescription = null, RuntimeInstructions = null, ActivationRequirement = null, Priority = AgentToolPriority.High, ConcurrencyMode = AgentToolConcurrencyMode.ParallelSafe }]);
            public ValueTask<AgentToolReadiness?> GetReadinessAsync(CYounics_Ai_Agent_Tool_Source_ToolSourceGetReadinessRequest request, CancellationToken token = default)
                => ValueTask.FromResult<AgentToolReadiness?>(new() { ToolId = request.ToolId, Status = AgentToolReadinessStatus.Ready, Message = "Ready" });
            public ValueTask<AgentToolResult?> ValidateArgumentsAsync(AgentToolRequest request, CancellationToken token = default) => ValueTask.FromResult<AgentToolResult?>(null);
            public ValueTask<AgentToolResult?> PreflightAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePreflightRequest request, CancellationToken token = default) { LastExecution = request.Context; return ValueTask.FromResult<AgentToolResult?>(Result(request.Request.ToolId)); }
            public ValueTask<AgentPermissionRequest?> PermissionAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePermissionRequest request, CancellationToken token = default) { LastExecution = request.Context; return ValueTask.FromResult<AgentPermissionRequest?>(new() { ActionId = "files.mutate", BoundaryId = "outside-configured-scope", Summary = "Mutation", ToolId = request.Request.ToolId, Command = "fixture command", Path = "/workspace/file.txt", WorkspaceId = request.Context.Workspace!.WorkspaceId, BindingId = request.Context.ExecutionBinding!.BindingId, ResourceDisplayName = "Fixture resource", ResourceReference = "durable", IsMutation = true, ScopeClassificationBasis = AgentPermissionScopeClassificationBasis.VerifiedTargetMapping, ResourceClaims = request.Context.ApprovedResourceClaims, ResourceCapabilities = ["transient"], ResourceReferences = ["durable"] }); }
            public ValueTask<AgentToolPresentation?> PresentationAsync(AgentToolPresentationRequest request, CancellationToken token = default) { LastPresentation = request; return ValueTask.FromResult<AgentToolPresentation?>(new() { HeaderText = "Header", DetailMarkdown = "Details", OutputText = "Output" }); }
            public ValueTask<AgentToolResult> ExecuteAsync(CYounics_Ai_Agent_Tool_Source_ToolSourceExecuteRequest request, CancellationToken token = default) { LastExecution = request.Context; return ValueTask.FromResult(Result(request.Request.ToolId)); }
            private static AgentToolResult Result(string toolId) => new() { ToolId = toolId, Summary = "Completed 漢字", Content = "Output", StructuredPayloadJson = "{opaque result", Sources = [], WasTruncated = true, IsError = false, ErrorCode = null, BackendId = "fixture", PresentationPayloadJson = "{opaque presentation", RequiresPromptContextRefresh = true };
        }
        """;
}
