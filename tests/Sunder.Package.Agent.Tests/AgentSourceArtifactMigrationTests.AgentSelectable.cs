using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;
using Xunit;

using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentSelectableConsumers(Assembly owner)
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "younics.ai.agent");
        var (_, lifetime, provider) = CreateGeneratedWorker(owner, package.Context, rpc);
        await using var owned = lifetime;
        var profileType = owner.GetType("Sunder.Package.Agent.Services.AgentProfileService", true)!;
        var profileService = provider.GetRequiredService(profileType);
        Func<AgentProfileRecord?, CancellationToken, Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>> list = (profile, token) =>
            AgentRoleBoundary.As<Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>>(AgentRoleBoundary.Call(profileService, "ListSelectableProfileCapabilitiesAsync", profile, token)!);
        Assert.Empty(await list(null, default));
        var peer = new SelectablePeer();
        var registration = AddSelectableProvider(state, peer);
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfileRecord("profile.selectable", "Profile", null, null, null, null, null, null, now, now,
            SelectableCapabilityAssignments: [new("skill", "one", peer.ProviderId)], BehaviorLoopSettingsJson: "{opaque");
        var values = await list(profile, default);
        Assert.Equal(new[] { "two", "one" }, values.Select(value => value.CapabilityId));
        Assert.Equal("{opaque", peer.LastRequest!.Profile!.BehaviorLoopSettingsJson);
        Assert.Equal(profile.SelectableCapabilityAssignments, peer.LastRequest.Profile.SelectableCapabilityAssignments);
        await registration.Probe.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, peer.Subscribers);
        await list(profile, default); Assert.Equal(1, peer.Subscribers);
        await using var receiver = new GeneratedServiceTestScope(rpc);
        var selectable = Assert.Single(await ProjectedProviders(receiver, owner, "ISelectableCapabilityProviderService", imported: false));
        using var client = selectable.Service;
        Assert.Same(owner, client.Value.GetType().Assembly);
        await using var watch = new ProjectedArtifactWatch(client.Value);
        Assert.True(await watch.Next());
        var independentChange = watch.Next();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = 0;
        profileType.GetEvent("SelectableCapabilitiesChanged")!.AddEventHandler(profileService,
            (Action)(() => { Interlocked.Increment(ref changes); changed.TrySetResult(); }));
        peer.Values = [new("skill", "updated", peer.ProviderId, "Updated", null, "Ready", true, "Fixture", "group", "Group", "Group description", -4)];
        peer.Notify(); await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await independentChange);
        var updated = Assert.Single(await list(profile, default));
        Assert.Equal("updated", updated.CapabilityId); Assert.Equal(-4, updated.GroupSortOrder); Assert.Equal("Group description", updated.GroupDescription);

        AgentRoleBoundary.Call(profileService, "ImportProfile", profile);
        var stack = (IPackageStackExporter)provider.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentProfileStackContributor", true)!);
        Assert.Contains((await stack.ExportAsync(new([new(profile.ProfileId)]))).PackageRequirements,
            value => value.PackageId == "example.selectable");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var before = peer.ListCalls;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => list(profile, cancelled.Token));
            Assert.Equal(before, peer.ListCalls);
        }
        peer.ListGate = new();
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = list(profile, cancelled.Token);
            await peer.ListGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        peer.ListGate = null;
        ((IDisposable)profileService).Dispose();
        var beforeDisposal = Volatile.Read(ref changes);
        var forwarded = watch.Next();
        peer.Notify(); Assert.True(await forwarded);
        Assert.Equal(beforeDisposal, Volatile.Read(ref changes));
        var retiredEvent = watch.Next();
        await state.RetireAsync(registration.Probe).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => retiredEvent)).Error.Kind);
        Assert.Equal(0, peer.Subscribers);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() =>
            client.InvokeAsync<AgentProfileSelectableCapabilityDescriptor[]>("ListAsync", profile, CancellationToken.None).AsTask())).Error.Kind);
        var replacement = new SelectablePeer(); AddSelectableProvider(state, replacement);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() =>
            client.InvokeAsync<AgentProfileSelectableCapabilityDescriptor[]>("ListAsync", profile, CancellationToken.None).AsTask())).Error.Kind);
        Assert.Equal(0, replacement.ListCalls);
        using var current = Assert.Single(await ProjectedProviders(receiver, owner, "ISelectableCapabilityProviderService", imported: false)).Service;
        Assert.NotSame(client.Value, current.Value);
        Assert.NotEmpty(await current.InvokeAsync<AgentProfileSelectableCapabilityDescriptor[]>("ListAsync", profile, CancellationToken.None));
        await receiver.DisposeAsync();
        Assert.Equal(0, replacement.Subscribers);
        await VerifySelectableStreamEdges(owner);
    }

    private static (SunderWorkerProviderRegistration Registration, SelectableHandlerProbe Probe) AddSelectableProvider(
        RegressionTestRpcState state, SelectablePeer peer, string mode = "normal")
    {
        var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "example.selectable"));
        using var providers = new RegressionTestSourceProviders();
        var generated = providers.Create(RegressionTestBindings.SelectableCapabilityProviders, peer, receiver);
        var probe = new SelectableHandlerProbe(generated.Handler, mode);
        var registration = new SunderWorkerProviderRegistration("skills.selectable.capabilities", generated.ContractId,
            generated.ContractVersion, generated.DescriptorSha256, generated.AbiSha256, probe);
        state.Add("example.selectable", CurrentPublicRpcDescriptors.Get(registration.ContractId), probe, probe, new GeneratedPeerLifetime(receiver), registration.ProviderId, "2.0.0");
        return (registration, probe);
    }

    private sealed class SelectablePeer : IAgentProfileSelectableCapabilityProvider, IAgentProfileSelectableCapabilityChangeNotifier
    {
        private Action? _changed;
        internal int Subscribers;
        internal int ListCalls;
        internal FilesCallGate? ListGate;
        internal AgentProfileSelectableCapabilityRequest? LastRequest;
        internal IReadOnlyList<AgentProfileSelectableCapabilityDescriptor> Values =
        [
            new("skill", "one", "fixture.selectable", "Zeta", "First"),
            new("SKILL", "ONE", "FIXTURE.SELECTABLE", "Duplicate", "Ignored"),
            new("skill", "two", "fixture.selectable", "Alpha", null),
        ];
        public string ProviderId => "fixture.selectable";
        public string DisplayName => "Selectable fixture";
        public event Action? SelectableCapabilitiesChanged
        {
            add { _changed += value; Interlocked.Increment(ref Subscribers); }
            remove { _changed -= value; Interlocked.Decrement(ref Subscribers); }
        }
        internal void Notify() => _changed?.Invoke();
        public async ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(AgentProfileSelectableCapabilityRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref ListCalls); LastRequest = request;
            if (ListGate is { } gate)
            {
                gate.Started.TrySetResult();
                try { await gate.Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { gate.Cancelled.TrySetResult(); throw; }
            }
            return Values;
        }
    }
}
