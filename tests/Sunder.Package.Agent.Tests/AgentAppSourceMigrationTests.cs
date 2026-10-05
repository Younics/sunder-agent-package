using System.Reflection;
using System.Runtime.Loader;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static class AgentAppSourceMigrationTests
{
    internal const string SourceClient = "src/Sunder.Package.Agent/Rpc/Owner/AgentOwnerRpcClient.cs";
    internal const string CompositionSource = "src/Sunder.Package.Agent/Rpc/Owner/AppComposition.cs";
    internal const string SourceOperations = "src/Sunder.Package.Agent/Rpc/Owner/AgentRuntimeOperations.cs";
    internal const string LegacyOperations = "src/Sunder.Package.Agent/App/Runtime/AgentRuntimeOperations.Legacy.cs";
    internal const string OperationErrorSource = "src/Sunder.Package.Agent/Runtime/AgentOwnerOperationException.cs";
    internal static IEnumerable<string> ToolConsumerSources => AgentCanonicalSourceMigrationTests.ToolSources.Concat(
        ["src/Sunder.Package.Agent/Rpc/Consumers/GeneratedProviderCallLifetime.cs", "src/Sunder.Package.Agent/Rpc/Consumers/AgentConsumerValues.cs"]);
    internal const string LegacyWorkspaceEditorBindings = "src/Sunder.Package.Agent/App/Runtime/AgentWorkspaceEditorBindings.Legacy.cs";
    internal static readonly string[] WorkspaceEditorSources =
    [
        "src/Sunder.Package.Agent/Rpc/Consumers/AgentWorkspaceEditorBindings.cs",
        "src/Sunder.Package.Agent/Rpc/Consumers/GeneratedWorkspaceEditorClient.cs",
    ];
    internal static readonly string[] RelocatedModels =
    [
        "src/Sunder.Package.Agent/Services/AgentExecutionTargetWarmupResult.cs",
        "src/Sunder.Package.Agent/Services/AgentToolCatalogEntry.cs",
        "src/Sunder.Package.Agent/Services/AgentWorkspaceBindingRoles.cs",
        "src/Sunder.Package.Agent/Runtime/AgentTranscriptPageCursor.cs",
        "src/Sunder.Package.Agent/Runtime/AgentWorkspaceDefaults.cs",
    ];
    internal static readonly string[] BusinessSources = [.. RelocatedModels, OperationErrorSource, .. new[]
    {
        "Runtime/AgentPresentationGateways.cs", "Runtime/AgentRuntimeContracts.cs", "Runtime/AgentTranscriptAnchorContracts.cs",
        "HistorySearch/HistorySearchModels.cs", "HistorySearch/HistorySearchText.cs", "HistorySearch/HistorySecretRedactor.cs",
        "Models/AgentPendingPermissionRequestRecord.cs", "Models/AgentRunActivityUpdate.cs",
        "Services/AgentAttachmentUploadRules.cs", "Services/AgentRpcInvocation.cs", "Services/AgentSessionTitleDefaults.cs",
    }.Select(path => "src/Sunder.Package.Agent/" + path)];

    internal static Task<Assembly> Build(string repository, string core, string workerRoot, AssemblyLoadContext loadContext)
    {
        using var receipt = CanonicalAgentRoles.Receipt(app: true);
        Assert.Equal(3, receipt.RootElement.GetProperty("uses").GetArrayLength());
        Assert.Equal(20, receipt.RootElement.GetProperty("contracts").GetArrayLength());
        Assert.All(receipt.RootElement.GetProperty("contracts").EnumerateArray(), contract => Assert.False(contract.TryGetProperty("runtimeUse", out _)));
        Assert.Empty(receipt.RootElement.GetProperty("providers").EnumerateArray());
        Assert.Empty(receipt.RootElement.GetProperty("platformImports").EnumerateArray());
        var assembly = loadContext.LoadFromAssemblyPath(Path.Combine(CanonicalAgentRoles.Output("app"), "Sunder.Package.Agent.App.dll"));
        AssertRoleLocalModels(assembly);
        return Task.FromResult(assembly);
    }

    internal static void AssertRoleLocalModels(Assembly assembly)
    {
        AgentSourceArtifactMigrationTests.AssertAgentAppSemanticOwnership(assembly);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent" or "Sunder.Package.Agent.OwnerProtocol" or "Sunder.Sdk.Worker");
        foreach (var type in new[] { typeof(AgentDashboardProjection), typeof(AgentTransportProfile), typeof(AgentSessionCommand),
            typeof(AgentAttachmentUploadHandle), typeof(IAgentProfileGateway), typeof(Sunder.Package.Agent.Services.AgentToolCatalogEntry),
            typeof(Sunder.Package.Agent.Services.AgentExecutionTargetWarmupResult), typeof(Sunder.Package.Agent.HistorySearch.HistorySearchRequest) })
        {
            var local = assembly.GetType(type.FullName!, true)!;
            Assert.Same(assembly, local.Assembly);
            Assert.NotSame(type, local);
        }
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Runtime.AgentOwnerRuntimeRpc"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Runtime.IAgentOwnerRuntimeProvider"));
    }

    internal static async Task Exercise(Assembly assembly, ISunderRpcClient rpc, IPackageContext context)
    {
        await using var services = CreateAppServices();
        var gatewayType = assembly.GetType("Sunder.Package.Agent.Runtime.AgentAppRuntimeGateway", true)!;
        var instance = services?.GetRequiredService(gatewayType) ?? Activator.CreateInstance(gatewayType, rpc)!;
        await using var gateway = (IAsyncDisposable)instance;
        if (services is not null) Assert.Same(instance, services.GetRequiredService(gatewayType));
        async Task<AgentProfileRecord> createProfile(string name, CancellationToken token) => JsonSerializer.SerializeToElement(
            await AwaitValue(gatewayType.GetMethod("CreateProfileAsync")!.Invoke(instance, [name, token])!)).Deserialize<AgentProfileRecord>()!;
        async Task<AgentWorkspaceRecord> createWorkspace(string name, CancellationToken token) => JsonSerializer.SerializeToElement(
            await AwaitValue(gatewayType.GetMethod("CreateWorkspaceAsync")!.Invoke(instance, [name, token])!)).Deserialize<AgentWorkspaceRecord>()!;
        var profile = await createProfile("Source App profile 漢字", default);
        var workspace = await createWorkspace("Source App workspace", default);
        var dashboard = await LoadDashboard();
        Assert.Contains(dashboard.GetProperty("profiles").EnumerateArray(), value => value.GetProperty("profileId").GetString() == profile.ProfileId && value.GetProperty("displayName").GetString() == profile.DisplayName);
        Assert.Contains(dashboard.GetProperty("workspaces").EnumerateArray(), value => value.GetProperty("workspaceId").GetString() == workspace.WorkspaceId && value.GetProperty("displayName").GetString() == workspace.DisplayName);
        var clientType = assembly.GetType("Sunder.Package.Agent.Runtime.AgentOwnerRpcClient", true)!;
        var client = Activator.CreateInstance(clientType, rpc)!;
        var transport = await AwaitValue(clientType.GetMethod("GetTransportProfileAsync")!.Invoke(client, [CancellationToken.None])!);
        Assert.Same(assembly, transport!.GetType().Assembly);
        Assert.Equal(transport, clientType.GetProperty("TransportProfile")!.GetValue(client));
        var commandType = assembly.GetType(typeof(AgentSessionCommand).FullName!, true)!;
        var resultType = assembly.GetType(typeof(AgentSessionCommandResult).FullName!, true)!;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var command = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { kind = 0, title = "Source App session", profileId = profile.ProfileId, workspaceId = workspace.WorkspaceId }), commandType, json)!;
        var operation = assembly.GetType("Sunder.Package.Agent.Runtime.AgentRuntimeOperations", true)!.GetField("SessionCommands")!.GetValue(null)!;
        var session = await AwaitValue(clientType.GetMethod("InvokeAsync")!.MakeGenericMethod(commandType, resultType).Invoke(client, [operation, command, CancellationToken.None])!);
        Assert.Same(assembly, session!.GetType().Assembly);
        Assert.Equal(workspace.WorkspaceId, JsonSerializer.SerializeToElement(session, session.GetType(), json).GetProperty("session").GetProperty("session").GetProperty("workspaceId").GetString());
        var bytes = System.Text.Encoding.UTF8.GetBytes("Source App attachment 漢字");
        var upload = clientType.GetMethod("UploadAttachmentAsync")!;
        var uploadRequest = JsonSerializer.SerializeToElement(new AgentAttachmentUploadRequest("source.txt", "text/plain", bytes))
            .Deserialize(upload.GetParameters()[0].ParameterType)!;
        var handle = await AwaitValue(upload.Invoke(client, [uploadRequest, CancellationToken.None])!);
        Assert.Same(assembly, handle!.GetType().Assembly);
        await (ValueTask)clientType.GetMethod("DiscardAttachmentAsync")!.Invoke(client, [handle, CancellationToken.None])!;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => createProfile("Cancelled source App profile", cancelled.Token));
        var after = await LoadDashboard();
        Assert.DoesNotContain(after.GetProperty("profiles").EnumerateArray(), value => value.GetProperty("displayName").GetString() == "Cancelled source App profile");

        async Task<JsonElement> LoadDashboard()
        {
            var value = await AwaitValue(gatewayType.GetMethod("LoadDashboardAsync")!.Invoke(instance, [CancellationToken.None])!);
            Assert.Same(assembly, value!.GetType().Assembly);
            return JsonSerializer.SerializeToElement(value, value.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        ServiceProvider? CreateAppServices()
        {
            if (assembly.GetType("Sunder.Generated.Package.AppModule") is null) return null; // The focused gateway fixture has no UI module.
            return AgentSourceArtifactMigrationTests.CreateGeneratedApp(assembly, context, rpc);
        }
    }

    private static async Task<object?> AwaitValue(object pending)
    {
        var task = pending as Task ?? (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
        await task.WaitAsync(TimeSpan.FromSeconds(20));
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

}
