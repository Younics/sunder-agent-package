extern alias SubagentsApp;

using SubagentsApp::Sunder.Package.Agent.Subagents.Runtime;
using SubagentsApp::Sunder.Package.Agent.Subagents.PackageViews;
using SubagentsApp::Sunder.Package.Agent.Subagents.Models;
using Sunder.Sdk.Services;
using Xunit;
using SubagentManagementServices = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Subagents_Services;
using AgentTranscriptToolDetailRequest = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTranscriptToolDetailRequest;

namespace Sunder.Package.Agent.Tests;

public sealed class SubagentGatewayLifetimeTests
{
    [Fact]
    public void AppRuntimeGateway_DisposeIsIdempotent()
    {
        var gateway = new SubagentAppRuntimeGateway(NullOwnerClient.Instance);

        gateway.Dispose();
        gateway.Dispose();
    }

    [Fact]
    public async Task AppRuntimeGateway_DisposeAsyncDrainsOwnerObservation()
    {
        var gateway = new SubagentAppRuntimeGateway(NullOwnerClient.Instance);
        await gateway.InitializeAsync();

        await gateway.DisposeAsync();
        await gateway.DisposeAsync();

        Assert.True(gateway.OwnerObservationTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SubsessionsViewModel_DisposeDoesNotDisposeSharedAppRuntimeGateway()
    {
        using var gateway = new SubagentAppRuntimeGateway(NullOwnerClient.Instance);
        var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);

        viewModel.Dispose();

        await gateway.InitializeAsync();
    }

    private sealed class NullOwnerClient : SubagentManagementServices.ISubagentsManagementService
    {
        internal static NullOwnerClient Instance { get; } = new();

        public ValueTask<SunderContent> ManagementAsync(CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> CreateAsync(string displayName, CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> SaveAsync(SubagentSaveRequest request, CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> DeleteAsync(string subagentId, CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> ProviderModelsAsync(string providerId, CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> RuntimeCatalogAsync(CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> RecentTurnsAsync(Guid sessionId, int limit, CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> TurnsBeforeAsync(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit, CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> TurnsAfterAsync(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit, CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> AroundTurnAsync(Guid sessionId, Guid turnId, DateTimeOffset createdAtUtc, Guid itemId, int beforeLimit, int afterLimit, CancellationToken cancellationToken = default)
            => Unavailable();

        public ValueTask<SunderContent> ToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken cancellationToken = default)
            => Unavailable();

        public async IAsyncEnumerable<SunderContent> ChangesAsync(long afterRevision, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        private static ValueTask<SunderContent> Unavailable()
            => ValueTask.FromException<SunderContent>(new InvalidOperationException("Runtime is unavailable."));
    }
}
