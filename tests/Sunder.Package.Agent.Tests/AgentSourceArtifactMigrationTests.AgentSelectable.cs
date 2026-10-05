using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static IAgentProfileSelectableCapabilityProvider CreateAgentSelectableClient(Assembly owner, ISunderRpcClient rpc, SunderRpcProviderSnapshot selected)
        => AgentRoleBoundary.As<IAgentProfileSelectableCapabilityProvider>(Activator.CreateInstance(
            owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedSelectableCapabilityClient", true)!, rpc, selected)!);

    private static async Task VerifyAgentSelectableConsumers(Assembly owner, Assembly skills, Assembly probes)
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var (_, lifetime, provider) = CreateGeneratedWorker(owner, package.Context, rpc);
        await using var owned = lifetime;
        var roleCatalog = provider.GetRequiredService(owner.GetType("Sunder.Package.Agent.Protocol.AgentRpcCatalog", true)!);
        var profileType = owner.GetType("Sunder.Package.Agent.Services.AgentProfileService", true)!;
        var profileService = provider.GetRequiredService(profileType);
        Func<AgentProfileRecord?, CancellationToken, Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>> list = (profile, token) =>
            AgentRoleBoundary.As<Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>>(AgentRoleBoundary.Call(profileService, "ListSelectableProfileCapabilitiesAsync", profile, token)!);
        Assert.Empty(await list(null, default));
        var peer = new SelectablePeer();
        var registration = AddSelectableProvider(skills, probes, state, peer);
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
        var service = owner.GetType("Sunder.Package.Agent.Runtime.Rpc.AgentSelectableBindings", true)!
            .GetProperty("Service", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var reference = AgentRoleBoundary.As<AgentRpcReference<IAgentProfileSelectableCapabilityProvider>>(Assert.Single(AgentRoleBoundary.References(roleCatalog, service)));
        Assert.True(reference.TryAcquire(out var lease));
        IAgentProfileSelectableCapabilityProvider client;
        using (lease) client = lease.Service;
        Assert.Same(owner, AgentRoleBoundary.Unwrap(client).GetType().Assembly);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = 0;
        profileType.GetEvent("SelectableCapabilitiesChanged")!.AddEventHandler(profileService,
            (Action)(() => { Interlocked.Increment(ref changes); changed.TrySetResult(); }));
        peer.Values = [new("skill", "updated", peer.ProviderId, "Updated", null, "Ready", true, "Fixture", "group", "Group", "Group description", -4)];
        peer.Notify(); await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        var forwarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IAgentProfileSelectableCapabilityChangeNotifier)client).SelectableCapabilitiesChanged += () => forwarded.TrySetResult();
        peer.Notify(); await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(beforeDisposal, Volatile.Read(ref changes));
        await state.RetireAsync(registration.Probe).WaitAsync(TimeSpan.FromSeconds(5));
        await Observation(client).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, peer.Subscribers);
        Assert.Null(await reference.TryAcquireAsync());
        var replacement = new SelectablePeer(); AddSelectableProvider(skills, probes, state, replacement);
        Assert.Null(await reference.TryAcquireAsync());
        var currentReference = AgentRoleBoundary.As<AgentRpcReference<IAgentProfileSelectableCapabilityProvider>>(Assert.Single(AgentRoleBoundary.References(roleCatalog, service)));
        Assert.True(currentReference.TryAcquire(out var currentLease));
        IAgentProfileSelectableCapabilityProvider current;
        using (currentLease) current = currentLease.Service;
        Assert.NotSame(client, current);
        Assert.NotEmpty(await current.ListCapabilitiesAsync(new(profile)));
        ((IDisposable)roleCatalog).Dispose();
        await ((IAsyncDisposable)client).DisposeAsync(); await ((IAsyncDisposable)current).DisposeAsync();
        Assert.Equal(0, replacement.Subscribers);
        await VerifySelectableStreamEdges(owner, skills, probes);
    }

    private static Task Observation(IAgentProfileSelectableCapabilityProvider value)
        => (Task)AgentRoleBoundary.Unwrap(value).GetType().GetProperty("ObservationTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(AgentRoleBoundary.Unwrap(value))!;

    private static (SunderWorkerProviderRegistration Registration, SelectableHandlerProbe Probe) AddSelectableProvider(
        Assembly skills, Assembly probes, RegressionTestRpcState state, SelectablePeer peer, string mode = "normal")
    {
        var type = skills.GetType("Sunder.Package.Agent.Skills.Rpc.SelectableCapabilityProvider", true)!;
        Func<string, CancellationToken, ValueTask<string>> list = async (request, token) => JsonSerializer.Serialize(
            await peer.ListCapabilitiesAsync(JsonSerializer.Deserialize<AgentProfileSelectableCapabilityRequest>(request, MemoryProviderJson)!, token), MemoryProviderJson);
        Action<Action> subscribe = value => peer.SelectableCapabilitiesChanged += value;
        Action<Action> unsubscribe = value => peer.SelectableCapabilitiesChanged -= value;
        var local = Activator.CreateInstance(probes.GetType("RoleLocalSelectableProbe", true)!, peer.ProviderId, peer.DisplayName, list, subscribe, unsubscribe)!;
        Assert.True(type.GetConstructors().Single().GetParameters()[0].ParameterType.IsInstanceOfType(local));
        var instance = Activator.CreateInstance(type, local)!;
        var registration = (SunderWorkerProviderRegistration)type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(instance, null)!;
        var probe = new SelectableHandlerProbe(registration.Handler, mode);
        state.Add("example.selectable", RegressionTestRpcDescriptors.Get(registration.ContractId), probe, probe, Noop.Instance, registration.ProviderId, "2.0.0");
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
