using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class DeferredAgentRpcServiceHandlerTests
{
    [Fact]
    public void RetiredDeferredHandler_IsNotPartOfTheCurrentProviderComposition()
    {
        var repository = BuilderSourceMigrationTests.FindRepository();

        Assert.False(File.Exists(Path.Combine(repository,
            "src/Sunder.Package.Agent/Runtime/DeferredAgentRpcServiceHandler.cs")));
        Assert.DoesNotContain(typeof(SunderRpcContractDescriptor).Assembly.GetTypes(),
            type => type.Name == "DeferredAgentRpcServiceHandler");
    }

    [Fact]
    public async Task GeneratedSessionCleaner_DeclaresRoutesBeforeActivationAndDispatchesAfterRegistration()
    {
        using var package = RegressionTestPackageScope.Create();
        var cleaner = new AgentAttachmentService(package.Context);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        await using var receiver = new GeneratedServiceTestScope(rpc);
        var descriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.SessionCleaners);
        var registration = receiver.Register(cleaner, descriptor.ContractId);

        Assert.Equal("agent.attachment.cleaner", registration.ProviderId);
        Assert.Equal(descriptor.ContractId, registration.ContractId);
        Assert.Equal(descriptor.Version, registration.ContractVersion);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        Assert.Equal(
            descriptor.Services.SelectMany(service => service.Methods.Select(method =>
                $"{service.ServiceId}/{method.MethodId}:{string.Join(',', method.Errors)}")).Order(StringComparer.Ordinal),
            registration.DeclaredErrors.SelectMany(service => service.Value.Select(method =>
                $"{service.Key}/{method.Key}:{string.Join(',', method.Value)}")).Order(StringComparer.Ordinal));

        state.Add("sunder.package.agent", descriptor, cleaner, registration.Handler,
            EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var client = Assert.Single((await receiver.Providers<AgentServices.ISessionCleanerService>().ListAsync()).Providers).Service;
        var identity = await client.DescribeAsync();

        Assert.Equal(cleaner.CleanerId, identity.Id);
        Assert.Equal(cleaner.CleanerId, identity.DisplayName);
    }

    [Fact]
    public async Task GeneratedSessionCleaner_HonorsCancellationAndRetirementWithoutDeletingSessionData()
    {
        using var package = RegressionTestPackageScope.Create();
        var cleaner = new AgentAttachmentService(package.Context);
        var sessionId = Guid.NewGuid();
        await cleaner.StoreAttachmentAsync(sessionId, new AgentAttachmentUploadRequest("keep.txt", "text/plain", [1]));
        var attachmentDirectory = package.Context.Storage.RoleLocalWorkspace.GetLocalPath(
            Path.Combine("agent", "attachments", sessionId.ToString("N")));
        Assert.True(Directory.Exists(attachmentDirectory));

        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        await using var receiver = new GeneratedServiceTestScope(rpc);
        var descriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.SessionCleaners);
        var registration = receiver.Register(cleaner, descriptor.ContractId);
        state.Add("sunder.package.agent", descriptor, cleaner, registration.Handler,
            EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var entry = Assert.Single((await receiver.Providers<AgentServices.ISessionCleanerService>().ListAsync()).Providers);
        var client = entry.Service;

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var cancelled = await Assert.ThrowsAsync<OperationCanceledException>(
                () => client.DeleteSessionAsync(sessionId, cancellation.Token).AsTask());
            Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        }
        Assert.True(Directory.Exists(attachmentDirectory));

        await state.RetireAsync(cleaner);

        var stale = await Assert.ThrowsAsync<SunderRpcException>(() => entry.EnsureCurrentAsync().AsTask());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
        Assert.True(entry.RetirementToken.IsCancellationRequested);
        var retired = await Assert.ThrowsAsync<SunderRpcException>(() => client.DeleteSessionAsync(sessionId).AsTask());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, retired.Error.Kind);
        Assert.Equal("rpc.stale-endpoint", retired.Error.Code);
        Assert.Null(state.LastInvocationFailure);
        Assert.True(Directory.Exists(attachmentDirectory));
    }

    private sealed class EmptyDisposable : IDisposable
    {
        internal static EmptyDisposable Instance { get; } = new();

        public void Dispose() { }
    }
}
