using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.Subagents.PackageViews;
using Xunit;

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

    private sealed class NullOwnerClient : ISubagentOwnerClient
    {
        internal static NullOwnerClient Instance { get; } = new();

        public ValueTask<SubagentProjection> QueryAsync(
            SubagentQuery request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SubagentProjection>(new InvalidOperationException("Runtime is unavailable."));

        public ValueTask<SubagentProjection> CommandAsync(
            SubagentCommand request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SubagentProjection>(new InvalidOperationException("Runtime is unavailable."));

        public async IAsyncEnumerable<SubagentChanged> SubscribeChangesAsync(
            SubagentChangeSubscription request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}
