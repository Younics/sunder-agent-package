using System.Reflection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyPromptTargetsAsync(Assembly assembly, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors,
        PromptFixture fixture, PromptProbe probe, PromptContextRequestWire request)
    {
        var identity = new PromptTargetProbe();
        var descriptor = descriptors[AgentRpcContractIds.ExecutionTarget];
        var handler = new Sunder.Generated.Rpc.ExecutionTarget.SunderAgentExecutionTargetRpcHandler(new RegressionTestExecutionProvider(identity));
        fixture.State.Add("test.execution.owner", descriptor, identity, handler, EmptyDisposable.Instance, "target.provider", "2.0.0");
        var provider = Assert.Single((await fixture.Rpc.DiscoverAsync(descriptor.ContractId)).Providers);
        var handle = AgentRpcProviderHandle.From(provider);
        var withTarget = request with { ExecutionTarget = handle };
        await InvokeGeneratedClient(fixture.Client, "ContributeAsync", RunJson(withTarget));
        var reference = Assert.IsType<AgentRpcReference<Sunder.Package.Agent.Contracts.Contracts.IAgentExecutionTarget>>(probe.Request!.ExecutionTargetReference);
        Assert.Equal(handle, reference.ToHandle());
        Assert.True(reference.TryAcquire(out var lease));
        using (lease) Assert.Equal("target.one", lease.Service.Descriptor.TargetId);
        var describes = identity.Describes;
        Assert.True(describes > 0);

        foreach (var mismatch in new[]
        {
            handle with { PackageId = "different.package" }, handle with { PackageVersion = "2.0.1" }, handle with { ProviderId = "different.provider" },
            handle with { ContractVersion = "1.0.1" }, handle with { DescriptorSha256 = new string('0', 64) }, handle with { AbiSha256 = new string('0', 64) },
            handle with { ActivationId = Guid.NewGuid() }, handle with { ActivationEpoch = handle.ActivationEpoch + 1 },
            handle with { SessionGeneration = handle.SessionGeneration + 1 }, handle with { EndpointReference = "test.rpc.missing" },
        })
        {
            var json = RunJson(request with { ExecutionTarget = mismatch });
            await InvokeGeneratedClient(fixture.Client, "ContributeAsync", json);
            Assert.Null(probe.Request!.ExecutionTargetReference);
        }
        var calls = probe.Contributions;
        await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "ContributeAsync", RunJson(request with { ExecutionTarget = handle with { ContractId = "wrong.contract" } })));
        Assert.Equal(calls, probe.Contributions);
        Assert.Equal("agent.rpc.contract-mismatch", Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure).Error.Code);
        foreach (var kind in new[] { SunderRpcErrorKind.Unavailable, SunderRpcErrorKind.StaleEndpoint, SunderRpcErrorKind.PermissionDenied })
        {
            fixture.State.SetProviderLookupFailure(fixture.PackageId, kind);
            try
            {
                if (kind == SunderRpcErrorKind.PermissionDenied)
                {
                    calls = probe.Contributions;
                    await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "ContributeAsync", RunJson(withTarget)));
                    Assert.Equal(calls, probe.Contributions);
                    Assert.Equal(kind, Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure).Error.Kind);
                }
                else
                {
                    await InvokeGeneratedClient(fixture.Client, "ContributeAsync", RunJson(withTarget));
                    Assert.Null(probe.Request!.ExecutionTargetReference);
                }
            }
            finally { fixture.State.SetProviderLookupFailure(fixture.PackageId, null); }
        }
        await fixture.State.RetireAsync(identity);
        fixture.State.Add("test.execution.owner", descriptor, new object(), handler, EmptyDisposable.Instance, "target.provider", "2.0.0");
        await InvokeGeneratedClient(fixture.Client, "ContributeAsync", RunJson(withTarget));
        Assert.Null(probe.Request!.ExecutionTargetReference);
        Assert.Equal(describes, identity.Describes);

        var noCatalog = new PromptProbe();
        await using var noCatalogFixture = await CreatePromptFixture(assembly, descriptors[AgentRpcContractIds.PromptContextContributor], noCatalog, includeCatalog: false);
        await InvokeGeneratedClient(noCatalogFixture.Client, "ContributeAsync", RunJson(withTarget));
        Assert.Null(noCatalog.Request!.ExecutionTargetReference);
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
