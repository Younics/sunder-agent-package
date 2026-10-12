using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Rpc.Public;
using Sunder.Sdk.Rpc;
using Xunit;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static async Task VerifyAgentToolProfiles(Assembly owner)
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var (_, lifetime, provider) = CreateGeneratedWorker(owner, package.Context, rpc);
        await using var owned = lifetime;
        var profiles = provider.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentProfileService", true)!);
        var tools = provider.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentToolService", true)!);
        var catalog = AgentRoleBoundary.As<AgentServices.IRuntimeCatalogService>(provider.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentRuntimeCatalog", true)!));
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfileRecord("profile.catalog", "Catalog 漢字", "Description", "Instructions", "chat", "chat-model", "embedding", "embedding-model", now, now,
            ModelBindings: [new("profile.catalog", AgentModelCapabilityKinds.Chat, "chat", "chat-model", "{opaque model settings", now),
                new("profile.catalog", AgentModelCapabilityKinds.Embedding, "embedding", "embedding-model", null, now)],
            SelectableCapabilityAssignments: [new("tool", "profile-tool", "fixture.profile")],
            BehaviorLoopId: "loop", BehaviorLoopSourceId: "loop.source", BehaviorLoopSettingsJson: "{opaque loop settings");
        SurfaceCall(profiles, "ImportProfile", profile);
        // Persistence normalizes model bindings and stamps their update times.
        profile = (await catalog.GetProfileAsync(profile.ProfileId))!;
        var tool = new ProfileCatalogTool();
        await using var toolScope = new GeneratedServiceTestScope(rpc.ForCaller("example.profile.tools"));
        Assert.True(RegressionTestGeneratedProviders.TryRegister(toolScope, RegressionTestBindings.ToolSources, tool, out var toolRegistration));
        AddSurfaceProvider(state, "example.profile.tools", toolRegistration);
        // The owning tool service resolves its profile from the local store;
        // remote catalog availability must not gate this local business path.
        Assert.Null(await Permission(profile.ProfileId));

        await using var catalogProviderScope = new GeneratedServiceTestScope(rpc.ForCaller("sunder.package.agent"));
        Assert.True(RegressionTestGeneratedProviders.TryRegister(catalogProviderScope, RegressionTestBindings.RuntimeCatalog,
            catalog, out var catalogRegistration));
        var handler = AddSurfaceProvider(state, "sunder.package.agent", catalogRegistration);
        Assert.Null(await Permission(profile.ProfileId));
        Assert.Equal(profile.ProfileId, tool.LastProfile!.ProfileId);
        Assert.Equal(profile.BehaviorLoopSettingsJson, tool.LastProfile.BehaviorLoopSettingsJson);
        Assert.Equal(profile.ModelBindings, tool.LastProfile.ModelBindings);
        Assert.Equal(profile.SelectableCapabilityAssignments, tool.LastProfile.SelectableCapabilityAssignments);
        Assert.Equal("", (await Permission("missing"))!.ActionId);
        var calls = handler.Calls.Count;
        Assert.Null(await Permission(" "));
        Assert.Equal(calls, handler.Calls.Count);

        var observed = new FilesRpcObserver(rpc, "get-profile");
        await using var catalogClientScope = new GeneratedServiceTestScope(observed);
        var client = await SelectCatalogAsync();
        async Task<AgentProfileRecord?> GetProfileAsync(string id)
            => await client.GetProfileAsync(id);

        var committed = await GetProfileAsync(profile.ProfileId);
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(profile, MemoryProviderJson),
            JsonSerializer.SerializeToElement(committed, MemoryProviderJson)));
        Assert.Null(await GetProfileAsync("missing"));
        Assert.Null(owner.GetType("Sunder.Package.Agent.Runtime.IAgentToolProfileCatalog"));
        Assert.Null(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.AgentToolProfileBindings"));
        Assert.Null(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedToolProfileCatalogClient"));
        profile = profile with { SelectableCapabilityAssignments = [], Description = null, Instructions = null, ChatProviderId = null, ChatModelId = null,
            EmbeddingProviderId = null, EmbeddingModelId = null, ModelBindings = null, BehaviorLoopSettingsJson = null };
        SurfaceCall(profiles, "ImportProfile", profile);
        Assert.Equal("", (await Permission(profile.ProfileId))!.ActionId);
        var empty = (await GetProfileAsync(profile.ProfileId))!; Assert.Null(empty.Description); Assert.Null(empty.Instructions); Assert.Null(empty.ChatProviderId);
        Assert.Empty(empty.SelectableCapabilityAssignments ?? []);
        profile = profile with { SelectableCapabilityAssignments = [new("TOOL", "PROFILE-TOOL", "different-source")] };
        SurfaceCall(profiles, "ImportProfile", profile);
        Assert.Equal("", (await Permission(profile.ProfileId))!.ActionId);
        profile = profile with { SelectableCapabilityAssignments = [new("TOOL", "PROFILE-TOOL", "FIXTURE.PROFILE")] };
        SurfaceCall(profiles, "ImportProfile", profile);
        Assert.Null(await Permission(profile.ProfileId));
        SurfaceCall(profiles, "DeleteProfile", profile.ProfileId);
        Assert.Null(await GetProfileAsync(profile.ProfileId));
        Assert.Equal("", (await Permission(profile.ProfileId))!.ActionId);
        SurfaceCall(profiles, "ImportProfile", profile);

        foreach (var change in new Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>[]
        {
            value => value with { PackageId = "other.package" }, value => value with { PackageVersion = "2.0.1" },
            value => value with { ProviderId = "other.provider" }, value => value with { ContractId = "other.contract" },
            value => value with { ContractVersion = "1.0.1" }, value => value with { DescriptorSha256 = new string('c', 64) },
            value => value with { AbiSha256 = new string('d', 64) }, value => value with { ActivationId = Guid.NewGuid() },
            value => value with { ActivationEpoch = value.ActivationEpoch + 1 }, value => value with { SessionGeneration = value.SessionGeneration + 1 },
            value => value with { Endpoint = new("other-endpoint") }, value => value with { State = SunderRpcProviderState.Inactive },
        })
        {
            await using var checkScope = new GeneratedServiceTestScope(observed);
            var checkClient = await SelectCatalogAsync(checkScope);
            calls = handler.Calls.Count;
            observed.Transform = change;
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint,
                (await Assert.ThrowsAsync<SunderRpcException>(() => checkClient.GetProfileAsync(profile.ProfileId).AsTask())).Error.Kind);
            Assert.Equal(calls, handler.Calls.Count);
            observed.Transform = null;
        }
        await using (var checkScope = new GeneratedServiceTestScope(observed))
        {
            var checkClient = await SelectCatalogAsync(checkScope);
            calls = handler.Calls.Count;
            observed.InvalidateAfterInvoke = true;
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint,
                (await Assert.ThrowsAsync<SunderRpcException>(() => checkClient.GetProfileAsync(profile.ProfileId).AsTask())).Error.Kind);
            Assert.Equal(calls + 1, handler.Calls.Count);
            observed.InvalidateAfterInvoke = false;
            observed.Transform = null;
        }
        await using (var checkScope = new GeneratedServiceTestScope(observed))
        {
            var checkClient = await SelectCatalogAsync(checkScope);
            calls = handler.Calls.Count;
            observed.LookupFailure = SunderRpcErrorKind.PermissionDenied;
            Assert.Equal(SunderRpcErrorKind.PermissionDenied,
                (await Assert.ThrowsAsync<SunderRpcException>(() => checkClient.GetProfileAsync(profile.ProfileId).AsTask())).Error.Kind);
            Assert.Equal(calls, handler.Calls.Count);
            observed.LookupFailure = null;
        }
        handler.FailMethod = "get-profile";
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted,
            (await Assert.ThrowsAsync<SunderRpcException>(() => GetProfileAsync(profile.ProfileId))).Error.Kind);
        calls = handler.Calls.Count;
        Assert.Null(await Permission(profile.ProfileId));
        Assert.Equal(calls, handler.Calls.Count);
        handler.FailMethod = null;
        handler.BlockMethod = "get-profile";
        handler.Gate = new();
        var pending = GetProfileAsync(profile.ProfileId);
        await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(handler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint,
            (await Assert.ThrowsAsync<SunderRpcException>(() => pending)).Error.Kind);
        calls = handler.Calls.Count;
        Assert.Null(await Permission(profile.ProfileId));
        Assert.Equal(calls, handler.Calls.Count);
        handler = AddSurfaceProvider(state, "sunder.package.agent", catalogRegistration);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint,
            (await Assert.ThrowsAsync<SunderRpcException>(() => GetProfileAsync(profile.ProfileId))).Error.Kind);
        Assert.Null(await Permission(profile.ProfileId));
        client = await SelectCatalogAsync();
        Assert.Equal(profile.ProfileId, (await GetProfileAsync(profile.ProfileId))!.ProfileId);
        handler.BlockMethod = "get-profile";
        handler.Gate = new();
        var stopping = GetProfileAsync(profile.ProfileId);
        await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = catalogClientScope.DisposeAsync().AsTask();
        Assert.Same(stopped, catalogClientScope.DisposeAsync().AsTask());
        AssertRetired((await Assert.ThrowsAsync<SunderRpcException>(() => stopping)).Error);
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => GetProfileAsync(profile.ProfileId));
        Assert.Equal(0, handler.Streams);
        observed.AssertScopedOperations();
        Assert.Equal(0, observed.ActiveWatches);
        Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));

        async Task<AgentServices.IRuntimeCatalogService> SelectCatalogAsync(GeneratedServiceTestScope? scope = null)
            => Assert.Single((await (scope ?? catalogClientScope).Providers<AgentServices.IRuntimeCatalogService>().ListAsync()).Providers).Service;

        static void AssertRetired(SunderRpcError error)
        {
            Assert.Equal(SunderRpcErrorKind.Unavailable, error.Kind);
            Assert.Equal("rpc.binding.retired", error.Code);
            Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, error.DispatchState);
            Assert.Equal(SunderRpcRetrySafety.Unsafe, error.RetrySafety);
        }

        Task<AgentPermissionRequest?> Permission(string id)
            => ((Task<AgentPermissionRequest?>)SurfaceCall(tools, "BuildPermissionRequestAsync",
                "profile-tool", "{}", null, id, null, null, null, null, null, CancellationToken.None, null)!).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class ProfileCatalogTool : IAgentToolSource
    {
        public string SourceId => "fixture.profile";
        public string DisplayName => "Profile tool";
        public string SourceKind => "local";
        internal AgentProfileRecord? LastProfile;
        public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); LastProfile = context.Profile; return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([new("profile-tool", "Profile tool", "Read-only fixture")]); }
        public ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentToolReadiness?>(new(toolId, AgentToolReadinessStatus.Ready, "Ready")); }
        public ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Profile preparation must not execute the tool.");
    }
}
