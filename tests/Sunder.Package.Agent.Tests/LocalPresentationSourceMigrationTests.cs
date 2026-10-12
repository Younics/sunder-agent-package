using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias LocalWorker;
using LocalWorker::Sunder.Package.Agent.Execution.Local;

public sealed class LocalPresentationSourceMigrationTests
{
    internal static readonly string[] SourcePaths =
    [
        "src/Sunder.Package.Agent.Execution.Local/LocalExecutionPresentationService.cs",
        "src/Sunder.Package.Agent.Execution.Local/App/LocalExecutionSettingsViewModel.cs",
    ];

    [AgentPublicSourceMigrationFact]
    public async Task PresentationSource_PreservesContractSettingsAndRevisionConflictsAcrossIsolatedApp()
    {
        using var source = PrivateManagementSourceFixture.Load("Execution.Local", CurrentPrivateRpcDescriptors.Get("sunder.package.agent.execution.local.presentation"));
        using var package = RegressionTestPackageScope.Create();
        var context = new TestPackageContext("sunder.package.agent.execution.local", package.RootPath, storage: package.Context.Storage);
        var catalog = new LocalShellCatalogService(context);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.execution.local");
        var (options, lifetime, _) = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(source.Worker, context, rpc);
        await using var owned = lifetime;
        var registration = Assert.Single(options.Providers, provider => provider.ProviderId == "local.presentation");
        Assert.Equal("local.presentation", registration.ProviderId);
        state.Add("sunder.package.agent.execution.local", source.Descriptor, registration.Handler, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        await using var app = AgentSourceArtifactMigrationTests.CreateGeneratedApp(source.App, context, rpc);
        var client = app.GetRequiredService(source.App.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Execution_Local_Services.IPresentationService", true)!);
        Assert.Same(source.App, client.GetType().Assembly);

        var initial = await InvokeAsync("GetSettingsAsync", CancellationToken.None);
        Assert.True(initial.GetProperty("success").GetBoolean());
        Assert.Equal("300", initial.GetProperty("timeoutSeconds").GetString());
        Assert.Empty(initial.GetProperty("customShells").EnumerateArray());
        var saved = await InvokeAsync("SaveSettingsAsync", "41", CancellationToken.None);
        Assert.Equal("41", saved.GetProperty("timeoutSeconds").GetString());
        Assert.Equal("41", (await InvokeAsync("GetSettingsAsync", CancellationToken.None)).GetProperty("timeoutSeconds").GetString());
        var invalid = await InvokeAsync("SaveSettingsAsync", "86401", CancellationToken.None);
        Assert.False(invalid.GetProperty("success").GetBoolean());
        Assert.Equal("local.timeout.invalid", invalid.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, invalid.GetProperty("error").GetProperty("correlationId").ValueKind);
        var executable = Path.Combine(package.RootPath, "source-shell");
        File.WriteAllText(executable, string.Empty);
        var initialRevision = initial.GetProperty("shellCatalogRevision").GetInt64();
        var shellSaved = await InvokeAsync("SaveShellsAsync", new[] { new LocalShellDefinition("source-shell", "Source", executable, "posix-sh", false) }, initialRevision, CancellationToken.None);
        Assert.True(shellSaved.GetProperty("success").GetBoolean());
        Assert.Equal("source-shell", Assert.Single(shellSaved.GetProperty("customShells").EnumerateArray()).GetProperty("shellId").GetString());
        Assert.Equal(initialRevision + 1, shellSaved.GetProperty("shellCatalogRevision").GetInt64());
        var conflict = await InvokeAsync("SaveShellsAsync", Array.Empty<LocalShellDefinition>(), initialRevision, CancellationToken.None);
        Assert.False(conflict.GetProperty("success").GetBoolean());
        Assert.Equal("local.shell-catalog.conflict", conflict.GetProperty("error").GetProperty("code").GetString());
        Assert.True(conflict.GetProperty("error").GetProperty("isTransient").GetBoolean());
        Assert.Equal("source-shell", Assert.Single(await catalog.ListCustomShellsAsync()).ShellId);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync("SaveSettingsAsync", "52", cancelled.Token).AsTask());
        }
        Assert.Equal("41", (await InvokeAsync("GetSettingsAsync", CancellationToken.None)).GetProperty("timeoutSeconds").GetString());
        await state.RetireAsync(registration.Handler);
        var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeAsync("GetSettingsAsync", CancellationToken.None).AsTask());
        Assert.Equal("rpc.provider-not-found", unavailable.Error.Code);

        async ValueTask<JsonElement> InvokeAsync(string method, params object?[] arguments)
        {
            var result = await AgentRoleBoundary.Await(AgentRoleBoundary.Call(client, method, arguments)!);
            Assert.Same(source.App, result!.GetType().Assembly);
            return JsonSerializer.SerializeToElement(result, result.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
    }

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
