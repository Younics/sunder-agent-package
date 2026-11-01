extern alias BuilderApp;

using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Xunit;
using App = BuilderApp::Sunder.Package.Agent.Builder;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderDevelopmentRelayTests
{
    [Fact]
    public async Task CompactToolsetLoadsBuiltOutputOnceAndReusesItsOwnerWithoutBuilding()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        var info = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "status" }, new("maker_package", JsonSerializer.Serialize(new { package = fixture.Project.EntryPoint, query = (string?)null })));
        Assert.False(info.IsError, info.Content);
        using var initial = JsonDocument.Parse(info.Content!);
        var package = initial.RootElement.GetProperty("package").GetString()!;
        Assert.False(initial.RootElement.GetProperty("loaded").GetBoolean());
        Assert.DoesNotContain("sourceRevision", info.Content);
        Assert.DoesNotContain(fixture.Project.ProjectFolder, info.Content);
        Assert.InRange(info.Content!.Length, 1, 400);
        var folder = App.BuilderProjectDriverOutputs.DevelopmentFolder(fixture.Project, "Debug");
        var identity = await BuilderTestDevelopmentOutput.WriteAsync(fixture.Project, folder);
        fixture.Development.PackageIdResolver = _ => fixture.Project.PackageId;
        fixture.Development.AfterLoad = session => session.Status = session.Status with { AppliedRevision = new(Guid.NewGuid(), 1, identity) };
        var request = new AgentToolRequest("maker_apply", JsonSerializer.Serialize(new { package }));
        var context = fixture.Context with { ToolCallId = "apply-built" };
        var permission = await tools.PermissionAsync(context, request);
        Assert.NotNull(permission);
        Assert.Equal("maker.apply", permission.ActionId);
        Assert.Equal(fixture.Project.EntryPoint, permission.Path);
        Assert.Empty(fixture.Development.Loads);
        Assert.Null(await tools.PreflightAsync(context, request));
        Assert.Equal(JsonSerializer.Serialize(permission), JsonSerializer.Serialize(await tools.PermissionAsync(context, request)));
        var applied = await tools.ExecuteAsync(context, request);
        Assert.False(applied.IsError, applied.Content);
        using var result = JsonDocument.Parse(applied.Content!);
        Assert.Equal("applied", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("loaded", result.RootElement.GetProperty("effect").GetString());
        Assert.InRange(applied.Content!.Length, 1, 150);
        Assert.Single(fixture.Development.Loads);
        Assert.Empty(fixture.Backend.Starts);
        fixture.Backend.Projects[fixture.Project.EntryPoint] = fixture.Project with { SourceRevision = "changed-source" };
        Assert.False((await tools.ExecuteAsync(context, request)).IsError);
        Assert.Single(fixture.Development.Loads);
    }

    [Fact]
    public async Task MissingBuiltOutputFailsBeforeAcceptanceAndDoesNotBreakTheCommandConsumer()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        var rejected = await tools.ExecuteAsync(fixture.Context, new("maker_apply", JsonSerializer.Serialize(new { package = fixture.Project.EntryPoint })));
        Assert.True(rejected.IsError);
        Assert.Contains("build_required", rejected.Content);
        var info = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "status-after-failure" }, new("maker_package", JsonSerializer.Serialize(new { package = fixture.Project.EntryPoint })));
        Assert.False(info.IsError, info.Content);
        Assert.True(fixture.Runtime.HasDevelopmentApp);
        Assert.Empty(fixture.Development.Loads);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task RejectedApplyCannotBecomeANewLoadWhenOutputLaterAppears()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        var request = new AgentToolRequest("maker_apply", JsonSerializer.Serialize(new { package = fixture.Project.EntryPoint }));
        var rejected = await tools.ExecuteAsync(fixture.Context, request);
        using var first = JsonDocument.Parse(rejected.Content!);
        Assert.Equal("rejected", first.RootElement.GetProperty("status").GetString());
        await BuilderTestDevelopmentOutput.WriteAsync(fixture.Project, App.BuilderProjectDriverOutputs.DevelopmentFolder(fixture.Project, "Debug"));
        var duplicate = await tools.ExecuteAsync(fixture.Context, request);
        Assert.Equal(rejected.Content, duplicate.Content);
        Assert.Empty(fixture.Development.Loads);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("workspace")]
    [InlineData("binding")]
    public async Task PreflightCannotCarryApprovalAcrossAChangedWorkspaceBindingEvenForTheSamePath(string difference)
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        await BuilderTestDevelopmentOutput.WriteAsync(fixture.Project, App.BuilderProjectDriverOutputs.DevelopmentFolder(fixture.Project, "Debug"));
        var request = new AgentToolRequest("maker_apply", JsonSerializer.Serialize(new { package = fixture.Project.EntryPoint }));
        Assert.Null(await tools.PreflightAsync(fixture.Context, request));
        var changed = difference == "profile" ? fixture.Context with { ProfileId = "different-profile" }
            : difference == "binding" ? fixture.Context with { ExecutionBinding = fixture.Context.ExecutionBinding! with { BindingId = "different-binding" } }
            : fixture.Context with
            {
                Workspace = fixture.Context.Workspace! with { WorkspaceId = "different-workspace",
                    Paths = fixture.Context.Workspace!.Paths.Select(path => path with { WorkspaceId = "different-workspace" }).ToArray() },
                ExecutionBinding = fixture.Context.ExecutionBinding! with { WorkspaceId = "different-workspace" },
            };
        var denied = await tools.ExecuteAsync(changed, request);
        Assert.True(denied.IsError);
        Assert.Empty(fixture.Development.Loads);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task LostInitialLoadReplyIsUnknownAndAnotherCallCannotReplayTheLoad()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        await BuilderTestDevelopmentOutput.WriteAsync(fixture.Project, App.BuilderProjectDriverOutputs.DevelopmentFolder(fixture.Project, "Debug"));
        fixture.Development.BeforeLoad = (_, _, _) => throw new IOException("Initial load reply lost after dispatch.");
        var request = new AgentToolRequest("maker_apply", JsonSerializer.Serialize(new { package = fixture.Project.EntryPoint }));
        var unknown = await tools.ExecuteAsync(fixture.Context, request);
        Assert.True(unknown.IsError);
        Assert.Equal("tool-outcome-ambiguous", unknown.ErrorCode);
        using var json = JsonDocument.Parse(unknown.Content!);
        Assert.Equal("unknown", json.RootElement.GetProperty("status").GetString());
        var duplicate = await tools.ExecuteAsync(fixture.Context, request);
        Assert.Equal(unknown.Content, duplicate.Content);
        Assert.Equal(unknown.ErrorCode, duplicate.ErrorCode);
        var blocked = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "new-call-must-not-replay-load" }, request);
        Assert.True(blocked.IsError);
        Assert.Equal("maker.busy", blocked.ErrorCode);
        Assert.Single(fixture.Development.Loads);
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task PendingLoadRemainsObservableWithoutBlockingTheCommandConsumerOrAdmittingASecondApply()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        var identity = await BuilderTestDevelopmentOutput.WriteAsync(fixture.Project, App.BuilderProjectDriverOutputs.DevelopmentFolder(fixture.Project, "Debug"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Development.BeforeLoad = (_, _, token) => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        fixture.Development.PackageIdResolver = _ => fixture.Project.PackageId;
        fixture.Development.AfterLoad = session => session.Status = session.Status with { AppliedRevision = new(Guid.NewGuid(), 1, identity) };
        var request = new AgentToolRequest("maker_apply", JsonSerializer.Serialize(new { package = fixture.Project.EntryPoint }));
        try
        {
            var pending = tools.ExecuteAsync(fixture.Context, request).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            var status = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "pending-status" }, new("maker_package", request.ArgumentsJson)).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(status.IsError, status.Content);
            using var statusJson = JsonDocument.Parse(status.Content!);
            Assert.False(string.IsNullOrWhiteSpace(statusJson.RootElement.GetProperty("pending").GetString()));
            Assert.False(pending.IsCompleted);
            var second = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "second-apply" }, request).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(second.IsError);
            Assert.Equal("maker.busy", second.ErrorCode);
            release.SetResult();
            var resolved = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(resolved.IsError, resolved.Content);
            Assert.Contains("applied", resolved.Content);
            Assert.Single(fixture.Development.Loads);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ViewDiscoveryOpeningFilteredQueryAndVerifiedInputUseCompactOriginalOwnerReferences()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        await fixture.LoadOutputAsync();
        var session = Assert.Single(fixture.Development.Sessions);
        session.Views = [new("local.view", "Example", false)];
        var snapshotId = Guid.NewGuid().ToString("N");
        var elementId = Guid.NewGuid().ToString("N");
        session.OnQueryView = (view, query, _) =>
        {
            Assert.Equal("local.view", view);
            Assert.Equal(query.Text is null ? "Send" : null, query.Name);
            var elements = query.Text is null ? new PackageDevelopmentViewElement[] { new(elementId, "button", "Send", true, ["invoke"]) }
                : [new(Guid.NewGuid().ToString("N"), "text", "Saved", true, [])];
            return Task.FromResult(new PackageDevelopmentViewPage(new(snapshotId, fixture.Project.PackageId, view, new(Guid.NewGuid(), 1, fixture.BuiltIdentity!), elements, false), false, null));
        };
        var discovered = await Run("maker_package", new { package = fixture.Project.EntryPoint, query = "views" });
        var viewRef = discovered.GetProperty("views")[0].GetProperty("ref").GetString();
        Assert.False(discovered.TryGetProperty("ownerId", out _));
        var opened = await Run("maker_interact", new { target = viewRef, action = "open" });
        Assert.Equal("opened", opened.GetProperty("status").GetString());
        Assert.Single(session.OpenedViews);
        var queried = await Run("maker_view", new { target = viewRef, name = "Send" });
        Assert.False(queried.TryGetProperty("snapshot", out _));
        var element = queried.GetProperty("elements")[0].GetProperty("ref").GetString();
        var inputRequest = new AgentToolRequest("maker_interact", JsonSerializer.Serialize(new { target = element, action = "invoke", verify = "Saved" }));
        var inputContext = fixture.Context with { ToolCallId = "one-input" };
        var dispatched = await tools.ExecuteAsync(inputContext, inputRequest);
        Assert.False(dispatched.IsError, dispatched.Content);
        using var dispatch = JsonDocument.Parse(dispatched.Content!);
        Assert.Equal("dispatched", dispatch.RootElement.GetProperty("status").GetString());
        Assert.Equal("matched", dispatch.RootElement.GetProperty("verification").GetString());
        Assert.False((await tools.ExecuteAsync(inputContext, inputRequest)).IsError);
        Assert.Single(session.ViewInputs);
        await fixture.Operations.ReleaseAsync(fixture.Operations.GetSession(fixture.Project.EntryPoint)!);
        await fixture.LoadOutputAsync();
        var replaced = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "old-view-owner" }, new("maker_view", JsonSerializer.Serialize(new { target = viewRef })));
        Assert.True(replaced.IsError);
        Assert.Contains("replaced", replaced.Content);
        Assert.Empty(fixture.Development.Sessions[1].ViewInputs);

        async Task<JsonElement> Run(string tool, object args)
        {
            var result = await tools.ExecuteAsync(fixture.Context with { ToolCallId = Guid.NewGuid().ToString("N") }, new(tool, JsonSerializer.Serialize(args)));
            Assert.False(result.IsError, result.Content);
            using var json = JsonDocument.Parse(result.Content!);
            return json.RootElement.Clone();
        }
    }

    [Fact]
    public async Task LogDefaultsFiltersAndCursorAreRetainedAndDetailsRemainOptIn()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        await fixture.LoadOutputAsync();
        var calls = 0;
        fixture.Development.ReadLogs = (package, query) =>
        {
            Assert.Equal(Sunder.Sdk.Logging.PackageLogLevel.Warning, query.MinimumLevel);
            Assert.Equal(4096, query.MaximumBytes);
            Assert.Equal(10, query.MaximumEntries);
            Assert.Equal("connection", query.Contains);
            Assert.InRange(DateTimeOffset.UtcNow - query.Since!.Value, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
            Assert.Equal(calls++ == 0 ? null : "original-cursor", query.Cursor);
            var entry = new PackageDevelopmentLogEntry(DateTimeOffset.UtcNow, "runtime", "Warning", "diagnostic", "Connection delayed", false)
                { Properties = new Dictionary<string, string> { ["operation"] = "private-diagnostic-id" } };
            return new(package, [entry, entry], false) { HasMore = query.Cursor is null, NextCursor = query.Cursor is null ? "original-cursor" : null };
        };
        var result = await tools.ExecuteAsync(fixture.Context, new("maker_logs", JsonSerializer.Serialize(new { package = fixture.Project.EntryPoint, contains = "connection" })));
        Assert.False(result.IsError, result.Content);
        Assert.DoesNotContain("private-diagnostic-id", result.Content);
        using var page = JsonDocument.Parse(result.Content!);
        var entry = page.RootElement.GetProperty("entries")[0];
        Assert.Equal(2, entry.GetProperty("count").GetInt32());
        var detail = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "detail" }, new("maker_logs", JsonSerializer.Serialize(new { entry = entry.GetProperty("ref").GetString() })));
        Assert.False(detail.IsError, detail.Content);
        Assert.Contains("private-diagnostic-id", detail.Content);
        var next = await tools.ExecuteAsync(fixture.Context with { ToolCallId = "next-log-page" }, new("maker_logs", JsonSerializer.Serialize(new { package = fixture.Project.EntryPoint, cursor = page.RootElement.GetProperty("next").GetString() })));
        Assert.False(next.IsError, next.Content);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ProjectReferencesCannotCrossSessionOrWorkspaceAuthority()
    {
        await using var fixture = new RelayFixture();
        var tools = await fixture.ConnectToolsAsync();
        var info = await tools.ExecuteAsync(fixture.Context, new("maker_package", JsonSerializer.Serialize(new { package = fixture.Project.EntryPoint })));
        using var json = JsonDocument.Parse(info.Content!);
        var request = new AgentToolRequest("maker_package", JsonSerializer.Serialize(new { package = json.RootElement.GetProperty("package").GetString() }));
        var denied = await tools.ExecuteAsync(fixture.Context with { SessionId = Guid.NewGuid(), ToolCallId = "foreign-session" }, request);
        Assert.True(denied.IsError);
        Assert.Empty(fixture.Backend.Starts);
        Assert.Empty(fixture.Development.Loads);
    }
}
