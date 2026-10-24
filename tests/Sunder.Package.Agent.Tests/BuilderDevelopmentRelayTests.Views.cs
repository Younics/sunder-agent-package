extern alias BuilderApp;

using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderDevelopmentRelayTests
{
    [Fact]
    public async Task GeneratedViewToolsRequireAnInspectedExactViewAndDuplicateInputOnlyObservesItsReceipt()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        var (session, snapshot) = await LoadViewAsync(fixture);
        var request = ViewInspect(fixture);
        Assert.Null(await tools.ValidateArgumentsAsync(request));
        var inspected = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "inspect-view" }, request);
        Assert.False(inspected.IsError, inspected.Content);
        Assert.Contains(snapshot.SnapshotId, inspected.Content, StringComparison.Ordinal);
        Assert.Contains("untrusted", inspected.Summary, StringComparison.OrdinalIgnoreCase);
        var input = ViewInput(fixture, snapshot);
        var inputContext = fixture.Context with { ToolCallId = "input-view" };
        Assert.Null(await tools.PreflightAsync(inputContext, input));
        var permission = await tools.PermissionAsync(inputContext, input);
        Assert.Equal("maker.view.input", permission!.ActionId);
        Assert.True(permission.IsMutation);
        var accepted = await tools.ExecuteAsync(inputContext, input);
        Assert.False(accepted.IsError, accepted.Content);
        using var acceptance = JsonDocument.Parse(accepted.Content!);
        var id = acceptance.RootElement.GetProperty("operationId").GetString()!;
        await fixture.ObserveAsync(tools, id, "dispatched");
        var result = await tools.ExecuteAsync(inputContext, input);
        Assert.False(result.IsError, result.Content);
        Assert.Contains("no input was replayed", result.Summary, StringComparison.Ordinal);
        Assert.Equal(id, Assert.Single(session.ViewInputs).OperationId);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Equal(0, fixture.PageResolutions);

        foreach (var invalid in new[]
        {
            ViewInput(fixture, snapshot with { SnapshotId = Guid.NewGuid().ToString("N") }),
            ViewInput(fixture, snapshot with { ViewId = "other.view" }),
            ViewInput(fixture, snapshot, action: "toggle"),
        })
        {
            var rejected = await tools.PreflightAsync(inputContext with { ToolCallId = Guid.NewGuid().ToString("N") }, invalid);
            Assert.True(rejected!.IsError);
        }
        Assert.Single(session.ViewInputs);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("configuration")]
    [InlineData("binding")]
    public async Task GeneratedViewInputRevalidatesItsOriginalSourceAndWorkspaceAfterPermission(string boundary)
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        var (session, snapshot) = await LoadViewAsync(fixture);
        await tools.ExecuteAsync(fixture.Context with { ToolCallId = "inspect-view" }, ViewInspect(fixture));
        var context = fixture.Context with { ToolCallId = "input-view" };
        var input = ViewInput(fixture, snapshot);
        Assert.Null(await tools.PreflightAsync(context, input));
        if (boundary == "source") fixture.Backend.Projects[fixture.Project.EntryPoint] = fixture.Project with { SourceRevision = "changed" };
        if (boundary == "configuration") fixture.Target.Configuration = "changed";
        if (boundary == "binding") context = context with { ExecutionBinding = context.ExecutionBinding! with { UpdatedAtUtc = context.ExecutionBinding.UpdatedAtUtc.AddSeconds(1) } };
        Assert.True((await tools.ExecuteAsync(context, input)).IsError);
        Assert.Equal("package-maker", (await tools.DescribeAsync()).SourceId);
        Assert.Empty(session.ViewInputs);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task LostGeneratedViewInputAcknowledgementNeverDispatchesTheOriginalInputTwice()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        var (session, snapshot) = await LoadViewAsync(fixture);
        await tools.ExecuteAsync(fixture.Context with { ToolCallId = "inspect-view" }, ViewInspect(fixture));
        var context = fixture.Context with { ToolCallId = "input-view" };
        var input = ViewInput(fixture, snapshot);
        Assert.Null(await tools.PreflightAsync(context, input));
        var completing = BuilderUiFixture.Signal(); var release = BuilderUiFixture.Signal();
        fixture.Transport.Before = async (method, response, token) =>
        {
            if (method != "complete-development-command" || !response.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString()!.Contains("\"operationId\"", StringComparison.Ordinal))) return;
            completing.TrySetResult(); await release.Task.WaitAsync(token);
        };
        using var caller = new CancellationTokenSource();
        var execute = tools.ExecuteAsync(context, input, caller.Token).AsTask();
        try
        {
            await completing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await BuilderUiFixture.WaitAsync(() => !session.ViewInputs.IsEmpty);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execute.WaitAsync(TimeSpan.FromSeconds(5)));
            fixture.Transport.Before = null; release.TrySetResult();
            fixture.Backend.Projects[fixture.Project.EntryPoint] = fixture.Project with { SourceRevision = "later" };
            var recovered = await tools.ExecuteAsync(context, input);
            Assert.False(recovered.IsError, recovered.Content);
            Assert.Contains("no input was replayed", recovered.Summary, StringComparison.Ordinal);
            Assert.Single(session.ViewInputs);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task AViewBackendFailureAfterPossibleDispatchRemainsAmbiguousAndCannotReplay()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        var (session, snapshot) = await LoadViewAsync(fixture);
        session.OnViewInput = (_, _) => throw new IOException("The native input may have dispatched before its reply was lost.");
        Assert.False((await tools.ExecuteAsync(fixture.Context with { ToolCallId = "inspect-view" }, ViewInspect(fixture))).IsError);
        var context = fixture.Context with { ToolCallId = "input-view" };
        var input = ViewInput(fixture, snapshot);
        Assert.Null(await tools.PreflightAsync(context, input));
        var accepted = await tools.ExecuteAsync(context, input);
        using var acceptance = JsonDocument.Parse(accepted.Content!);
        var id = acceptance.RootElement.GetProperty("operationId").GetString()!;
        var observe = new AgentToolRequest("maker_get_package_info", JsonSerializer.Serialize(new { entryPoint = fixture.Project.EntryPoint, operationId = id }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var result = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "observe-input" }, observe, timeout.Token);
            using var json = JsonDocument.Parse(result.Content!);
            if (json.RootElement.GetProperty("state").GetString() == "failed")
            {
                Assert.True(result.IsError);
                Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.ErrorCode);
                Assert.True(json.RootElement.GetProperty("outcomeUnknown").GetBoolean());
                break;
            }
            await Task.Delay(10, timeout.Token);
        }
        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, (await tools.ExecuteAsync(context, input)).ErrorCode);
        Assert.Single(session.ViewInputs);
    }

    private static AgentToolRequest ViewInspect(RelayFixture fixture)
        => new("maker_inspect_package_view", JsonSerializer.Serialize(new { entryPoint = fixture.Project.EntryPoint, viewId = "local.view" }));
    private static AgentToolRequest ViewInput(RelayFixture fixture, PackageDevelopmentViewSnapshot snapshot, string action = "invoke")
        => new("maker_interact_package_view", JsonSerializer.Serialize(new { entryPoint = fixture.Project.EntryPoint, viewId = snapshot.ViewId,
            snapshotId = snapshot.SnapshotId, elementReference = snapshot.Elements[0].Reference, action }));
    private static async Task<(BuilderUiSession, PackageDevelopmentViewSnapshot)> LoadViewAsync(RelayFixture fixture)
    {
        fixture.Development.PackageIdResolver = _ => fixture.Project.PackageId;
        await fixture.Operations.LoadAsync(fixture.Project, Path.Combine(fixture.Project.ProjectFolder, "sunder-dev"), default);
        var session = Assert.Single(fixture.Development.Sessions);
        var snapshot = new PackageDevelopmentViewSnapshot(Guid.NewGuid().ToString("N"), fixture.Project.PackageId, "local.view",
            new(Guid.NewGuid(), 1, new('a', 64)), [new(Guid.NewGuid().ToString("N"), "Button", "Untrusted package label", true, ["invoke"])], false);
        session.OnInspectView = (_, _) => Task.FromResult(snapshot);
        return (session, snapshot);
    }
}
