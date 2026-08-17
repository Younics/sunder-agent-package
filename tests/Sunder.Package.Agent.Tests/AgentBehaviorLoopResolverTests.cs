using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentBehaviorLoopResolverTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", " ")]
    [InlineData(DefaultAgentBehaviorLoop.LoopId, null)]
    [InlineData(DefaultAgentBehaviorLoop.LoopId, " ")]
    [InlineData(DefaultAgentBehaviorLoop.LoopId, "sunder.package.agent")]
    [InlineData("unknown-built-in-loop", "sunder.package.agent")]
    public void Resolve_BuiltInSelectionUsesLocalSingletonWithoutCatalogDiscovery(
        string? loopId,
        string? sourceId)
    {
        using var catalog = new RegressionTestExtensionCatalog();
        var defaultLoop = CreateDefaultLoop(catalog);
        catalog.AddBehaviorLoop(defaultLoop, "sunder.package.agent");
        var resolver = new AgentBehaviorLoopResolver(
            catalog,
            catalog.BehaviorLoops,
            defaultLoop);
        var discoveryCount = catalog.DiscoveryCount;

        using var selection = resolver.Resolve(Profile(loopId, sourceId));

        Assert.Same(defaultLoop.Descriptor, selection.Descriptor);
        Assert.Equal("sunder.package.agent", selection.Descriptor.SourceId);
        Assert.Equal("sunder.package.agent", selection.OwnerPackageId);
        Assert.Equal(discoveryCount, catalog.DiscoveryCount);
        Assert.False(selection.IsRetiring);
    }

    [Theory]
    [InlineData(DefaultAgentBehaviorLoop.LoopId, "external.default", "external.default")]
    [InlineData("external-loop", null, "external.loop")]
    public async Task Resolve_ExternalSelectionUsesLeasedRpcService(
        string loopId,
        string? requestedSourceId,
        string descriptorSourceId)
    {
        using var catalog = new RegressionTestExtensionCatalog();
        var defaultLoop = CreateDefaultLoop(catalog);
        catalog.AddBehaviorLoop(defaultLoop, "sunder.package.agent");
        var externalLoop = new ProbeBehaviorLoop(loopId, descriptorSourceId);
        catalog.AddBehaviorLoop(externalLoop, "external.package");
        var resolver = new AgentBehaviorLoopResolver(
            catalog,
            catalog.BehaviorLoops,
            defaultLoop);

        using var selection = resolver.Resolve(Profile(loopId, requestedSourceId));

        Assert.NotSame(defaultLoop.Descriptor, selection.Descriptor);
        Assert.Equal(loopId, selection.Descriptor.LoopId);
        Assert.Equal(descriptorSourceId, selection.Descriptor.SourceId);
        Assert.Equal("external.package", selection.OwnerPackageId);
        Assert.True(catalog.DiscoveryCount > 0);

        await catalog.RetireProviderAsync(externalLoop);
        _ = catalog.GetServiceReferences(catalog.BehaviorLoops);
        Assert.True(selection.IsRetiring);
    }

    [Fact]
    public async Task Resolve_UnavailableExternalSelectionFallsBackToLocalSingleton()
    {
        using var catalog = new RegressionTestExtensionCatalog();
        var defaultLoop = CreateDefaultLoop(catalog);
        var externalLoop = new ProbeBehaviorLoop("external-loop", "external.loop");
        catalog.AddBehaviorLoop(externalLoop, "external.package");
        await catalog.RetireProviderAsync(externalLoop);
        var resolver = new AgentBehaviorLoopResolver(
            catalog,
            catalog.BehaviorLoops,
            defaultLoop);

        using var selection = resolver.Resolve(Profile("external-loop", "external.loop"));

        Assert.Same(defaultLoop.Descriptor, selection.Descriptor);
    }

    [Fact]
    public void Resolve_UnknownSelectionFallsBackToReusableLocalSingleton()
    {
        using var catalog = new RegressionTestExtensionCatalog();
        var defaultLoop = CreateDefaultLoop(catalog);
        var resolver = new AgentBehaviorLoopResolver(
            catalog,
            catalog.BehaviorLoops,
            defaultLoop);

        var first = resolver.Resolve(Profile("missing-loop", "missing.source"));
        first.Dispose();
        using var second = resolver.Resolve(Profile("missing-loop", "missing.source"));

        Assert.Same(defaultLoop.Descriptor, first.Descriptor);
        Assert.Same(defaultLoop.Descriptor, second.Descriptor);
    }

    private static DefaultAgentBehaviorLoop CreateDefaultLoop(AgentRpcCatalog catalog)
        => new(new AgentSystemPromptComposer(catalog));

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

    private sealed class ProbeBehaviorLoop(string loopId, string sourceId) : IAgentBehaviorLoop
    {
        public AgentBehaviorLoopDescriptor Descriptor { get; } = new(
            loopId,
            loopId,
            "Resolver test behavior loop.",
            sourceId);

        public ValueTask<AgentBehaviorLoopResult> RunAsync(
            AgentBehaviorLoopContext context,
            IAgentBehaviorLoopRuntime runtime,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
