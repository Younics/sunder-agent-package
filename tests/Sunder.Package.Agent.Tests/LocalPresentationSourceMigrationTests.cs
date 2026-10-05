using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias LocalWorker;
using LocalWorker::Sunder.Package.Agent.Execution.Local;

public sealed class LocalPresentationSourceMigrationTests
{
    internal static readonly string[] SourcePaths =
    [
        "src/Sunder.Package.Agent.Execution.Local/Rpc/ManagementService.cs",
        "src/Sunder.Package.Agent.Execution.Local/Rpc/ManagementClient.cs",
    ];

    [AgentPublicSourceMigrationFact]
    public async Task PresentationSource_PreservesContractSettingsAndRevisionConflictsAcrossIsolatedApp()
    {
        using var source = PrivateManagementSourceFixture.Load("Execution.Local", RegressionTestRpcDescriptors.Get("sunder.package.agent.execution.local.presentation"));
        using var package = RegressionTestPackageScope.Create();
        var context = new TestPackageContext("sunder.package.agent.execution.local", package.RootPath, storage: package.Context.Storage);
        var catalog = new LocalShellCatalogService(context);
        var handler = new LocalExecutionPresentationRpcHandler(context, catalog);
        var type = source.Worker.GetType("Sunder.Package.Agent.Execution.Local.Rpc.ManagementService", true)!;
        var service = AgentRoleBoundary.Create(type, handler);
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(service, null));
        Assert.Equal("local.presentation", registration.ProviderId);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        state.Add("sunder.package.agent.execution.local", source.Descriptor, service, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        var client = AgentRoleBoundary.As<ILocalExecutionPresentationClient>(AgentRoleBoundary.Create(source.App.GetType("Sunder.Package.Agent.Execution.Local.App.LocalExecutionSourceClient", true)!,
            new RegressionTestRpcClient(state, "sunder.package.agent.execution.local")));

        var initial = await client.ExecuteAsync(new(LocalExecutionOperationKind.GetSettings));
        Assert.True(initial.Success);
        Assert.Equal("300", initial.TimeoutSeconds);
        Assert.Empty(initial.CustomShells!);
        var saved = await client.ExecuteAsync(new(LocalExecutionOperationKind.SaveSettings, TimeoutSeconds: "41"));
        Assert.Equal("41", saved.TimeoutSeconds);
        Assert.Equal("41", (await client.ExecuteAsync(new(LocalExecutionOperationKind.GetSettings))).TimeoutSeconds);
        var invalid = await client.ExecuteAsync(new(LocalExecutionOperationKind.SaveSettings, TimeoutSeconds: "86401"));
        Assert.False(invalid.Success);
        Assert.Equal("local.timeout.invalid", invalid.Error!.Code);
        Assert.Null(invalid.Error.CorrelationId);
        var executable = Path.Combine(package.RootPath, "source-shell");
        File.WriteAllText(executable, string.Empty);
        var shell = new LocalShellDefinition("source-shell", "Source λ", executable, AgentShellSyntaxKinds.PosixSh, false);
        var shellSaved = await client.ExecuteAsync(new(LocalExecutionOperationKind.SaveShells,
            Shells: [shell], ExpectedShellCatalogRevision: initial.ShellCatalogRevision));
        Assert.True(shellSaved.Success);
        Assert.Equal(shell, Assert.Single(shellSaved.CustomShells!));
        Assert.Equal(initial.ShellCatalogRevision + 1, shellSaved.ShellCatalogRevision);
        var conflict = await client.ExecuteAsync(new(LocalExecutionOperationKind.SaveShells,
            Shells: [], ExpectedShellCatalogRevision: initial.ShellCatalogRevision));
        Assert.False(conflict.Success);
        Assert.Equal("local.shell-catalog.conflict", conflict.Error!.Code);
        Assert.True(conflict.Error.IsTransient);
        Assert.Equal(shell, Assert.Single(await catalog.ListCustomShellsAsync()));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync(new(LocalExecutionOperationKind.SaveSettings, TimeoutSeconds: "52"), cancelled.Token).AsTask());
        }
        Assert.Equal("41", (await client.ExecuteAsync(new(LocalExecutionOperationKind.GetSettings))).TimeoutSeconds);
        await state.RetireAsync(service);
        var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => client.ExecuteAsync(new(LocalExecutionOperationKind.GetSettings)).AsTask());
        Assert.Equal("local.presentation.unavailable", unavailable.Error.Code);
    }

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
