using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentBehaviorLoopResolverTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", " ")]
    [InlineData(DefaultAgentBehaviorLoop.LoopId, null)]
    [InlineData(DefaultAgentBehaviorLoop.LoopId, " ")]
    [InlineData(DefaultAgentBehaviorLoop.LoopId, "younics.ai.agent")]
    [InlineData("unknown-built-in-loop", "younics.ai.agent")]
    public async Task Resolve_BuiltInSelectionUsesLocalSingletonWithoutCatalogDiscovery(
        string? loopId,
        string? sourceId)
    {
        await using var fixture = new ResolverFixture();

        var selection = await fixture.Resolver.ResolveAsync(Profile(loopId, sourceId));

        Assert.Same(fixture.DefaultLoop.Descriptor, selection.Descriptor);
        Assert.Equal("younics.ai.agent", selection.Descriptor.SourceId);
        Assert.Equal("younics.ai.agent", selection.OwnerPackageId);
        Assert.Equal(0, fixture.BehaviorLoops.DiscoveryCount);
    }

    [Theory]
    [InlineData(DefaultAgentBehaviorLoop.LoopId, "external.default", "external.default")]
    [InlineData("external-loop", null, "external.loop")]
    public async Task Resolve_ExternalSelectionUsesTrustedProviderIdentity(
        string loopId,
        string? requestedSourceId,
        string descriptorSourceId)
    {
        await using var fixture = new ResolverFixture();
        var externalLoop = new ProbeBehaviorLoop(loopId, descriptorSourceId);
        var provider = fixture.BehaviorLoops.Add(externalLoop, "external.package");

        var selection = await fixture.Resolver.ResolveAsync(Profile(loopId, requestedSourceId));

        Assert.NotSame(fixture.DefaultLoop.Descriptor, selection.Descriptor);
        Assert.Equal(loopId, selection.Descriptor.LoopId);
        Assert.Equal(descriptorSourceId, selection.Descriptor.SourceId);
        Assert.Equal("external.package", selection.OwnerPackageId);
        Assert.True(fixture.BehaviorLoops.DiscoveryCount > 0);

        fixture.BehaviorLoops.Retire(provider);
        var replacement = await fixture.Resolver.ResolveAsync(Profile(loopId, requestedSourceId));
        Assert.Same(fixture.DefaultLoop.Descriptor, replacement.Descriptor);
        await Assert.ThrowsAsync<SunderRpcException>(() => provider.EnsureCurrentAsync().AsTask());
        Assert.True(provider.RetirementToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Resolve_UnavailableExternalSelectionFallsBackToLocalSingleton()
    {
        await using var fixture = new ResolverFixture();
        var externalLoop = new ProbeBehaviorLoop("external-loop", "external.loop");
        var provider = fixture.BehaviorLoops.Add(externalLoop, "external.package");
        fixture.BehaviorLoops.Retire(provider);

        var selection = await fixture.Resolver.ResolveAsync(Profile("external-loop", "external.loop"));

        Assert.Same(fixture.DefaultLoop.Descriptor, selection.Descriptor);
    }

    [Fact]
    public async Task Resolve_UnknownSelectionFallsBackToReusableLocalSingleton()
    {
        await using var fixture = new ResolverFixture();

        var first = await fixture.Resolver.ResolveAsync(Profile("missing-loop", "missing.source"));
        var second = await fixture.Resolver.ResolveAsync(Profile("missing-loop", "missing.source"));

        Assert.Same(fixture.DefaultLoop.Descriptor, first.Descriptor);
        Assert.Same(fixture.DefaultLoop.Descriptor, second.Descriptor);
    }

    private sealed class ResolverFixture : IAsyncDisposable
    {
        internal FakeServiceCatalog<AgentServices.IBehaviorLoopService> BehaviorLoops { get; } = new(
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.BehaviorLoops));
        private readonly FakeServiceCatalog<AgentServices.IRunControlService> _runControls = new(
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.RunControls));
        private readonly FakeServiceCatalog<AgentServices.ISystemPromptContributorService> _prompts = new(
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.SystemPromptContributors));
        internal DefaultAgentBehaviorLoop DefaultLoop { get; }
        internal AgentBehaviorLoopResolver Resolver { get; }

        internal ResolverFixture()
        {
            DefaultLoop = new(new AgentSystemPromptComposer(_prompts));
            Resolver = new(BehaviorLoops, _runControls, new AgentActiveRunRegistry(), DefaultLoop);
        }

        public async ValueTask DisposeAsync()
        {
            try { await BehaviorLoops.DisposeAsync(); }
            finally
            {
                try { await _runControls.DisposeAsync(); }
                finally { await _prompts.DisposeAsync(); }
            }
        }
    }

    private static AgentProfileRecord Profile(string? loopId, string? sourceId)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentProfileRecord(
            "profile",
            "Profile",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now,
            BehaviorLoopId: loopId,
            BehaviorLoopSourceId: sourceId);
    }

    private sealed class ProbeBehaviorLoop(string loopId, string sourceId) : AgentServices.IBehaviorLoopService
    {
        public AgentBehaviorLoopDescriptor Descriptor { get; } = new(
            loopId,
            loopId,
            "Resolver test behavior loop.",
            sourceId);

        public ValueTask<AgentBehaviorLoopDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Descriptor);
        }

        public ValueTask<AgentBehaviorLoopResult> RunAsync(
            AgentBehaviorLoopContext context,
            Guid invocationId,
            ServiceReference<AgentServices.IRunControlService> runControl,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
