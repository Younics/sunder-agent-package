using System.Text.Json;
using Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
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
        var view = await ViewReferenceAsync(fixture, tools);
        var request = ViewInspect(view);
        Assert.Null(await tools.ValidateArgumentsAsync(request));
        var inspected = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "inspect-view" }, request);
        Assert.False(inspected.IsError, inspected.Content);
        Assert.DoesNotContain(snapshot.SnapshotId, inspected.Content, StringComparison.Ordinal);
        Assert.Contains("Untrusted package label", inspected.Content, StringComparison.Ordinal);
        var element = ElementReference(inspected);
        var input = ViewInput(element);
        var inputContext = fixture.Context with { ToolCallId = "input-view" };
        Assert.Null(await tools.PreflightAsync(inputContext, input));
        var permission = await tools.PermissionAsync(inputContext, input);
        Assert.Equal("maker.view.input", permission!.ActionId);
        Assert.True(permission.IsMutation);
        var accepted = await tools.ExecuteAsync(inputContext, input);
        Assert.False(accepted.IsError, accepted.Content);
        using var acceptance = JsonDocument.Parse(accepted.Content!);
        Assert.Equal("dispatched", acceptance.RootElement.GetProperty("status").GetString());
        var result = await tools.ExecuteAsync(inputContext, input);
        Assert.False(result.IsError, result.Content);
        Assert.Equal(accepted.Content, result.Content);
        Assert.Single(session.ViewInputs);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Equal(0, fixture.PageResolutions);

        foreach (var invalid in new[]
        {
            ViewInput("e" + Guid.NewGuid().ToString("N")),
            ViewInput(view),
            ViewInput(element, action: "toggle"),
        })
        {
            var rejected = await tools.ExecuteAsync(inputContext with { ToolCallId = Guid.NewGuid().ToString("N") }, invalid);
            Assert.True(rejected.IsError);
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
        var (session, _) = await LoadViewAsync(fixture);
        var inspected = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "inspect-view" }, ViewInspect(await ViewReferenceAsync(fixture, tools)));
        var context = fixture.Context with { ToolCallId = "input-view" };
        var input = ViewInput(ElementReference(inspected));
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
        var (session, _) = await LoadViewAsync(fixture);
        var inspected = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "inspect-view" }, ViewInspect(await ViewReferenceAsync(fixture, tools)));
        var context = fixture.Context with { ToolCallId = "input-view" };
        var input = ViewInput(ElementReference(inspected));
        Assert.Null(await tools.PreflightAsync(context, input));
        var completing = BuilderUiFixture.Signal(); var release = BuilderUiFixture.Signal();
        fixture.Transport.Before = async (method, response, token) =>
        {
            if (method != "complete-development-command" || !ContainsOperationReceipt(response)) return;
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
            Assert.Equal(recovered.Content, (await tools.ExecuteAsync(context, input)).Content);
            Assert.Single(session.ViewInputs);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task AViewBackendFailureAfterPossibleDispatchRemainsAmbiguousAndCannotReplay()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        var (session, _) = await LoadViewAsync(fixture);
        session.OnViewInput = (_, _) => throw new IOException("The native input may have dispatched before its reply was lost.");
        var inspected = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "inspect-view" }, ViewInspect(await ViewReferenceAsync(fixture, tools)));
        var context = fixture.Context with { ToolCallId = "input-view" };
        var input = ViewInput(ElementReference(inspected));
        Assert.Null(await tools.PreflightAsync(context, input));
        var accepted = await tools.ExecuteAsync(context, input);
        Assert.True(accepted.IsError);
        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, accepted.ErrorCode);
        using var acceptance = JsonDocument.Parse(accepted.Content!);
        Assert.Equal("unknown", acceptance.RootElement.GetProperty("status").GetString());
        var duplicate = await tools.ExecuteAsync(context, input);
        Assert.Equal(accepted.Content, duplicate.Content);
        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, duplicate.ErrorCode);
        Assert.Single(session.ViewInputs);
    }

    private static AgentToolRequest ViewInspect(string view)
        => new("maker_view", JsonSerializer.Serialize(new { target = view }));
    private static AgentToolRequest ViewInput(string element, string action = "invoke")
        => new("maker_interact", JsonSerializer.Serialize(new { target = element, action }));
    private static string ElementReference(AgentToolResult result)
    {
        Assert.False(result.IsError, result.Content);
        using var json = JsonDocument.Parse(result.Content!);
        return json.RootElement.GetProperty("elements")[0].GetProperty("ref").GetString()!;
    }
    private static async Task<string> ViewReferenceAsync(RelayFixture fixture, IToolSourceService tools)
    {
        var result = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "discover-view" },
            new("maker_package", JsonSerializer.Serialize(new { package = fixture.Project.EntryPoint, query = "views" })));
        Assert.False(result.IsError, result.Content);
        using var json = JsonDocument.Parse(result.Content!);
        return json.RootElement.GetProperty("views")[0].GetProperty("ref").GetString()!;
    }
    private static async Task<(BuilderUiSession, PackageDevelopmentViewSnapshot)> LoadViewAsync(RelayFixture fixture)
    {
        await fixture.LoadOutputAsync();
        var session = Assert.Single(fixture.Development.Sessions);
        session.Views = [new("local.view", "Example", true)];
        var snapshot = new PackageDevelopmentViewSnapshot(Guid.NewGuid().ToString("N"), fixture.Project.PackageId, "local.view",
            new(Guid.NewGuid(), 1, fixture.BuiltIdentity!), [new(Guid.NewGuid().ToString("N"), "Button", "Untrusted package label", true, ["invoke"])], false);
        session.OnQueryView = (_, _, _) => Task.FromResult(new PackageDevelopmentViewPage(snapshot, false, null));
        return (session, snapshot);
    }
}
