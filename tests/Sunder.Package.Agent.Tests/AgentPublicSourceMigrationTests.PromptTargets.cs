using System.Reflection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyPromptTargetsAsync(Assembly assembly, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors,
        PromptFixture fixture, PromptProbe probe, PromptContextRequestWire request)
    {
        var identity = new PromptTargetProbe();
        var descriptor = descriptors[RegressionTestBindings.ExecutionTargets];
        using var providers = new RegressionTestSourceProviders();
        var registration = providers.Create(descriptor.ContractId, identity, fixture.Receiver);
        fixture.State.Add("test.execution.owner", descriptor, identity, registration.Handler, EmptyDisposable.Instance, "target.provider", "2.0.0");
        var provider = Assert.Single((await fixture.Receiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers,
            value => value.ProviderId == "target.provider");
        var withTarget = request.DomainRequestWithoutTarget() with { ExecutionTarget = provider.Reference };
        await fixture.Client.ContributeAsync(withTarget);
        var reference = Assert.IsType<ServiceReference<AgentServices.IExecutionTargetService>>(probe.Request!.ExecutionTarget);
        var snapshot = typeof(ServiceReference<AgentServices.IExecutionTargetService>)
            .GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(System.Text.Json.JsonElement.DeepEquals(
            RunJson(snapshot.GetValue(provider.Reference)), RunJson(snapshot.GetValue(reference))));
        await reference.EnsureCurrentAsync();
        Assert.Equal("target.one", (await reference.Service.DescribeAsync()).TargetId);
        var describes = identity.Describes;
        Assert.True(describes > 0);

        var calls = probe.Contributions;
        var targetEndpoint = Assert.Single(fixture.State.Discover(descriptor.ContractId).Providers).Endpoint;
        foreach (var kind in new[] { SunderRpcErrorKind.Unavailable, SunderRpcErrorKind.StaleEndpoint, SunderRpcErrorKind.PermissionDenied })
        {
            fixture.State.SetProviderLookupFailure(fixture.PackageId, kind, targetEndpoint);
            try
            {
                if (kind == SunderRpcErrorKind.PermissionDenied)
                {
                    calls = probe.Contributions;
                    var denied = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Client.ContributeAsync(withTarget).AsTask());
                    Assert.Equal(calls, probe.Contributions);
                    Assert.Equal(kind, denied.Error.Kind);
                }
                else
                {
                    await fixture.Client.ContributeAsync(withTarget);
                    Assert.Null(probe.Request!.ExecutionTarget);
                }
            }
            finally { fixture.State.SetProviderLookupFailure(fixture.PackageId, null, targetEndpoint); }
        }
        await fixture.State.RetireAsync(identity);
        fixture.State.Add("test.execution.owner", descriptor, new object(), registration.Handler, EmptyDisposable.Instance, "target.provider", "2.0.0");
        await fixture.Client.ContributeAsync(withTarget);
        Assert.Null(probe.Request!.ExecutionTarget);
        Assert.Equal(describes, identity.Describes);

        var noCatalog = new PromptProbe();
        await using var noCatalogFixture = await CreatePromptFixture(assembly, descriptors[RegressionTestBindings.PromptContextContributors], noCatalog, includeCatalog: false);
        await noCatalogFixture.Client.ContributeAsync(withTarget);
        Assert.Null(noCatalog.Request!.ExecutionTarget);
    }

    internal sealed class PromptTargetProbe : IAgentExecutionTarget
    {
        public int Describes { get; private set; }
        public AgentExecutionTargetDescriptor Descriptor
        {
            get { Describes++; return new(TargetKind: "test", TargetId: "target.one", DisplayName: "Target", Description: null, SupportsShell: true, SupportsFiles: false); }
        }
        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
