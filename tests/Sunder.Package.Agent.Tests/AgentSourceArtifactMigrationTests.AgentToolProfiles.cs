using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

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
        var domain = AgentRoleBoundary.As<IAgentRuntimeCatalog>(provider.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentRuntimeCatalog", true)!));
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfileRecord("profile.catalog", "Catalog 漢字", "Description", "Instructions", "chat", "chat-model", "embedding", "embedding-model", now, now,
            ModelBindings: [new("profile.catalog", AgentModelCapabilityKinds.Chat, "chat", "chat-model", "{opaque model settings", now),
                new("profile.catalog", AgentModelCapabilityKinds.Embedding, "embedding", "embedding-model", null, now)],
            SelectableCapabilityAssignments: [new("tool", "profile-tool", "fixture.profile")],
            BehaviorLoopId: "loop", BehaviorLoopSourceId: "loop.source", BehaviorLoopSettingsJson: "{opaque loop settings");
        SurfaceCall(profiles, "ImportProfile", profile);
        // Persistence normalizes model bindings and stamps their update times.
        // The transport must preserve that committed snapshot, not the import input.
        profile = domain.GetProfile(profile.ProfileId)!;
        var tool = new ProfileCatalogTool();
        using var providerCatalog = AgentRpcCatalog.CreateDormant(rpc.ForCaller("example.profile.tools"));
        var toolHandler = new Sunder.Generated.Rpc.ToolSource.SunderAgentToolSourceRpcHandler(new RegressionTestToolProvider(tool, providerCatalog));
        state.Add("example.profile.tools", RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ToolSource), toolHandler, toolHandler, Noop.Instance, "fixture.profile.tools", "2.0.0");
        Assert.Equal("", (await Permission(profile.ProfileId))!.ActionId);
        var service = AgentRoleBoundary.Create(owner.GetType("Sunder.Package.Agent.Rpc.Public.RuntimeCatalogService", true)!, domain);
        var registration = SurfaceRegistration(service);
        var handler = AddSurfaceProvider(state, "sunder.package.agent", registration);
        Assert.Null(await Permission(profile.ProfileId));
        Assert.Equal(profile.ProfileId, tool.LastProfile!.ProfileId);
        Assert.Equal(profile.BehaviorLoopSettingsJson, tool.LastProfile.BehaviorLoopSettingsJson);
        Assert.Equal(profile.ModelBindings, tool.LastProfile.ModelBindings);
        Assert.Equal(profile.SelectableCapabilityAssignments, tool.LastProfile.SelectableCapabilityAssignments);
        Assert.Equal("", (await Permission("missing"))!.ActionId);
        var calls = handler.Calls.Count;
        Assert.Null(await Permission(" ")); Assert.Equal(calls, handler.Calls.Count);

        var observed = new FilesRpcObserver(rpc, "get-profile");
        var snapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.RuntimeCatalog)).Providers);
        var clientType = owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedToolProfileCatalogClient", true)!;
        object Create() => Activator.CreateInstance(clientType, observed, snapshot)!;
        static Func<string, AgentProfileRecord?> Get(object client) => id => AgentRoleBoundary.As<AgentProfileRecord?>(AgentRoleBoundary.Call(client, "GetProfile", id)!);
        var client = Create(); await using var clientLifetime = (IAsyncDisposable)client;
        var get = Get(client);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(domain.GetProfile(profile.ProfileId), MemoryProviderJson),
            JsonSerializer.SerializeToElement(get(profile.ProfileId), MemoryProviderJson)));
        Assert.Null(get("missing"));
        profile = profile with { SelectableCapabilityAssignments = [], Description = null, Instructions = null, ChatProviderId = null, ChatModelId = null,
            EmbeddingProviderId = null, EmbeddingModelId = null, ModelBindings = null, BehaviorLoopSettingsJson = null };
        SurfaceCall(profiles, "ImportProfile", profile);
        Assert.Equal("", (await Permission(profile.ProfileId))!.ActionId);
        var empty = get(profile.ProfileId)!; Assert.Null(empty.Description); Assert.Null(empty.Instructions); Assert.Null(empty.ChatProviderId);
        Assert.Empty(empty.SelectableCapabilityAssignments ?? []);
        profile = profile with { SelectableCapabilityAssignments = [new("TOOL", "PROFILE-TOOL", "different-source")] };
        SurfaceCall(profiles, "ImportProfile", profile); Assert.Equal("", (await Permission(profile.ProfileId))!.ActionId);
        profile = profile with { SelectableCapabilityAssignments = [new("TOOL", "PROFILE-TOOL", "FIXTURE.PROFILE")] };
        SurfaceCall(profiles, "ImportProfile", profile); Assert.Null(await Permission(profile.ProfileId));
        SurfaceCall(profiles, "DeleteProfile", profile.ProfileId); Assert.Null(get(profile.ProfileId));
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
            observed.Transform = change;
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => get(profile.ProfileId)).Error.Kind);
        }
        observed.Transform = null; observed.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => get(profile.ProfileId)).Error.Kind);
        observed.InvalidateAfterInvoke = false; observed.Transform = null;
        observed.LookupFailure = SunderRpcErrorKind.PermissionDenied;
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, Assert.Throws<SunderRpcException>(() => get(profile.ProfileId)).Error.Kind);
        observed.LookupFailure = null;
        handler.FailMethod = "get-profile";
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, Assert.Throws<SunderRpcException>(() => get(profile.ProfileId)).Error.Kind);
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => Permission(profile.ProfileId))).Error.Kind);
        handler.FailMethod = null; handler.BlockMethod = "get-profile"; handler.Gate = new();
        var pending = Task.Run(() => get(profile.ProfileId));
        await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(handler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => pending)).Error.Kind);
        Assert.Equal("", (await Permission(profile.ProfileId))!.ActionId);
        handler = AddSurfaceProvider(state, "sunder.package.agent", registration);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => get(profile.ProfileId)).Error.Kind);
        Assert.Null(await Permission(profile.ProfileId));
        snapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.RuntimeCatalog)).Providers);
        var replacement = Create(); await using var nextLifetime = (IAsyncDisposable)replacement;
        var next = Get(replacement); Assert.Equal(profile.ProfileId, next(profile.ProfileId)!.ProfileId);
        handler.BlockMethod = "get-profile"; handler.Gate = new();
        var stopping = Task.Run(() => next(profile.ProfileId)); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = nextLifetime.DisposeAsync().AsTask(); Assert.Same(stopped, nextLifetime.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping); await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => next(profile.ProfileId)).Error.Kind);
        Assert.Equal(0, handler.Streams); Assert.Equal(0, observed.UnscopedLookups); Assert.Equal(0, observed.ActiveWatches);
        Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));

        Task<AgentPermissionRequest?> Permission(string id) => ((Task<AgentPermissionRequest?>)SurfaceCall(tools, "BuildPermissionRequestAsync",
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
