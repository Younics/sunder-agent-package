using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class WorkspaceSessionTests
{
    [Fact]
    public void AgentSessionService_CreateRootSessionWithoutWorkspace_Throws()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);

        var exception = Assert.Throws<AgentSessionOperationException>(() => sessionService.CreateSession("Root Session"));

        Assert.Equal("Root sessions must be created with an explicit workspace id.", exception.Message);
    }

    [Fact]
    public void AgentSessionService_CreateRootSessionInUnassignedWorkspace_Throws()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);

        var exception = Assert.Throws<AgentSessionOperationException>(() => sessionService.CreateSession(
            "Root Session",
            workspaceId: AgentWorkspaceService.UnassignedSessionsWorkspaceId));

        Assert.Equal("Root sessions cannot be created in Unassigned Sessions.", exception.Message);
    }

    [Fact]
    public void AgentSessionService_CreateChildSessionWithoutWorkspace_InheritsParentWorkspace()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var parent = sessionService.CreateSession("Parent Session", workspaceId: workspace.WorkspaceId);

        var child = sessionService.CreateSession("Child Session", parentSessionId: parent.SessionId);

        Assert.Equal(workspace.WorkspaceId, child.WorkspaceId);
        Assert.Equal(workspace.WorkspaceId, store.GetSession(child.SessionId)?.WorkspaceId);
    }

    [Fact]
    public void AgentSessionService_CreateChildSessionWithDifferentWorkspace_Throws()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var now = DateTimeOffset.UtcNow;
        var parentWorkspace = new AgentWorkspaceRecord(Guid.NewGuid().ToString("N"), "Parent Workspace", null, now, now);
        var otherWorkspace = new AgentWorkspaceRecord(Guid.NewGuid().ToString("N"), "Other Workspace", null, now, now);
        store.SaveWorkspace(parentWorkspace);
        store.SaveWorkspace(otherWorkspace);
        var parent = sessionService.CreateSession("Parent Session", workspaceId: parentWorkspace.WorkspaceId);

        var exception = Assert.Throws<AgentSessionOperationException>(() => sessionService.CreateSession(
            "Child Session",
            parentSessionId: parent.SessionId,
            workspaceId: otherWorkspace.WorkspaceId));

        Assert.Equal("Child sessions must use their parent session workspace.", exception.Message);
    }

    [Fact]
    public void AgentSessionService_UpdateSessionRejectsBlankWorkspace()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = sessionService.CreateSession("Workspace Session", workspaceId: workspace.WorkspaceId);

        var exception = Assert.Throws<AgentSessionOperationException>(() => sessionService.UpdateSession(session with
        {
            WorkspaceId = " ",
        }));

        Assert.Equal("Sessions must have an assigned workspace.", exception.Message);
        Assert.Equal(workspace.WorkspaceId, store.GetSession(session.SessionId)?.WorkspaceId);
    }

    [Fact]
    public void AgentSessionService_UpdateSessionRejectsMoveToUnassignedWorkspace()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = sessionService.CreateSession("Workspace Session", workspaceId: workspace.WorkspaceId);

        var exception = Assert.Throws<AgentSessionOperationException>(() => sessionService.UpdateSession(session with
        {
            WorkspaceId = AgentWorkspaceService.UnassignedSessionsWorkspaceId,
        }));

        Assert.Equal("Sessions cannot be moved to Unassigned Sessions.", exception.Message);
        Assert.Equal(workspace.WorkspaceId, store.GetSession(session.SessionId)?.WorkspaceId);
    }

    [Fact]
    public void AgentSessionService_UpdateSessionRejectsMoveBetweenWorkspaces()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var now = DateTimeOffset.UtcNow;
        var firstWorkspace = new AgentWorkspaceRecord(Guid.NewGuid().ToString("N"), "First Workspace", null, now, now);
        var secondWorkspace = new AgentWorkspaceRecord(Guid.NewGuid().ToString("N"), "Second Workspace", null, now, now);
        store.SaveWorkspace(firstWorkspace);
        store.SaveWorkspace(secondWorkspace);
        var session = sessionService.CreateSession("Workspace Session", workspaceId: firstWorkspace.WorkspaceId);

        var exception = Assert.Throws<AgentSessionOperationException>(() => sessionService.UpdateSession(session with
        {
            WorkspaceId = secondWorkspace.WorkspaceId,
        }));

        Assert.Equal("Sessions cannot be moved between workspaces.", exception.Message);
        Assert.Equal(firstWorkspace.WorkspaceId, store.GetSession(session.SessionId)?.WorkspaceId);
    }

    [Fact]
    public void AgentSessionService_SaveCheckpoint_IgnoresThrowingSessionChangedSubscriber()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = sessionService.CreateSession("Event Isolation Session", workspaceId: workspace.WorkspaceId);
        var observed = false;
        sessionService.SessionChanged += _ => throw new InvalidOperationException("Subscriber failed.");
        sessionService.SessionChanged += _ => observed = true;

        var checkpoint = sessionService.SaveCheckpoint(session.SessionId, 1, AgentRunStatus.Running, "Still running.");

        Assert.Equal(AgentRunStatus.Running, checkpoint.Status);
        Assert.True(observed);
    }

    [Fact]
    public void AgentSessionService_AppendTextTurn_IgnoresThrowingTurnChangedSubscriber()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = sessionService.CreateSession("Turn Event Isolation Session", workspaceId: workspace.WorkspaceId);
        var observed = false;
        sessionService.TurnChanged += (_, _) => throw new InvalidOperationException("Subscriber failed.");
        sessionService.TurnChanged += (_, _) => observed = true;

        var turn = sessionService.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "hello");

        Assert.Equal(AgentMessageRole.Assistant, turn.Role);
        Assert.True(observed);
    }

    [Fact]
    public void AgentSessionService_AppendToolResultTurn_PersistsArgumentsJson()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        var argumentsJson = JsonSerializer.Serialize(new { pattern = "*.html" });
        store.SaveWorkspace(workspace);
        var session = store.CreateSession("Tool Args", workspaceId: workspace.WorkspaceId);

        sessionService.AppendToolResultTurn(
            session.SessionId,
            "call-1",
            "glob",
            argumentsJson,
            "index.html",
            "Found 1 match",
            structuredPayloadJson: null,
            sourcesJson: null,
            wasTruncated: false,
            isError: false,
            errorCode: null,
            backendId: null);

        var resultItem = Assert.Single(store.ListTurns(session.SessionId).SelectMany(turn => turn.Items), item => item.Kind == AgentTurnItemKind.ToolResult);
        Assert.Equal(argumentsJson, resultItem.ArgumentsJson);
    }

    [Fact]
    public void AgentSessionService_AppendToolResultTurn_PersistsPresentationPayloadJson()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var sessionService = new AgentSessionService(store);
        var workspace = CreateWorkspace();
        var payloadJson = JsonSerializer.Serialize(new { schema = "younics.ai.agent.tools.files.diff.v1" });
        store.SaveWorkspace(workspace);
        var session = store.CreateSession("Tool Payload", workspaceId: workspace.WorkspaceId);

        sessionService.AppendToolResultTurn(
            session.SessionId,
            "call-1",
            "edit",
            argumentsJson: null,
            content: "Wrote 42 character(s).",
            resultSummary: "Wrote 42 character(s).",
            structuredPayloadJson: null,
            sourcesJson: null,
            wasTruncated: false,
            isError: false,
            errorCode: null,
            backendId: null,
            presentationPayloadJson: payloadJson);

        var reopenedStore = new AgentLocalStore(scope.Context);
        var resultItem = Assert.Single(reopenedStore.ListTurns(session.SessionId).SelectMany(turn => turn.Items), item => item.Kind == AgentTurnItemKind.ToolResult);
        Assert.Equal(payloadJson, resultItem.PresentationPayloadJson);
    }
}
