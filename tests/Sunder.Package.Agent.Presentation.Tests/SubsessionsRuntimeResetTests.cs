extern alias SubagentsApp;

using System.Reflection;
using Avalonia.Headless.XUnit;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using SubagentsApp::Sunder.Package.Agent.Subagents.Runtime;
using SubagentModels = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class SubsessionsRuntimeResetTests
{
    [AvaloniaTheory]
    [InlineData("before")]
    [InlineData("during")]
    [InlineData("after")]
    public async Task ResetRefreshesMetadataAndTheAnchoredIntentWithoutReadingRecent(string phase)
    {
        var ports = new ResetPorts { BlockInitialization = phase == "before" };
        using var model = new SubsessionsViewModel(ports, ports, ports, ports);
        var gate = phase == "during" ? ports.HoldNextAroundRead() : null;
        var preparation = model.PrepareNavigationAsync(ports.Navigation()).AsTask();
        if (phase == "before") await ports.InitializationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else if (gate is not null) await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else Assert.True(await preparation);

        ports.Title = "Reset metadata";
        ports.Text = "Reset anchored content";
        ports.Reset();
        await RefreshIdle(model).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Reset metadata", Assert.Single(model.Subsessions, s => s.SessionId == ports.SessionId).Title);
        Assert.Equal(0, ports.RecentReads);

        ports.ReleaseInitialization.TrySetResult();
        gate?.Release.TrySetResult();
        Assert.True(await preparation.WaitAsync(TimeSpan.FromSeconds(5)));
        await RefreshIdle(model).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, ports.RecentReads);
        Assert.Equal(phase == "before" ? 1 : 2, ports.AroundReads);
        Assert.True(model.IsCurrentNavigationPlacement(ports.SessionId));
        Assert.False(model.IsTranscriptFollowingLatest);
        var row = Assert.IsType<SubsessionTextTranscriptRowViewModel>(Assert.Single(model.Messages));
        Assert.Equal(ports.AnchorTurnId, row.RowId);
        Assert.Equal("Reset anchored content", row.Content);
        Assert.False(model.HasLoadError, model.StatusText);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatestOrSessionIntentFencesALateResetAnchorResponse(bool changeSession)
    {
        var ports = new ResetPorts();
        using var model = new SubsessionsViewModel(ports, ports, ports, ports);
        Assert.True(await model.PrepareNavigationAsync(ports.Navigation()));
        var gate = ports.HoldNextAroundRead();
        ports.Reset();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (changeSession)
            model.SelectedSubsession = Assert.Single(model.Subsessions, s => s.SessionId == ports.OtherSessionId);
        else
            model.JumpToLatestTranscriptCommand.Execute(null);
        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await TranscriptLoad(model).WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.TrySetResult(); // Deliberately return the old page despite cancellation.
        await RefreshIdle(model).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(changeSession ? ports.OtherSessionId : ports.SessionId, model.SelectedSubsession?.SessionId);
        Assert.Equal(1, ports.RecentReads);
        Assert.True(model.IsTranscriptFollowingLatest);
        Assert.Null(model.NavigationAnchorKey);
        Assert.Equal(ports.LatestTurnId, Assert.Single(model.Messages).RowId);
        Assert.False(model.HasLoadError, model.StatusText);
    }

    private static Task RefreshIdle(SubsessionsViewModel model)
    {
        var loop = typeof(SubsessionsViewModel).GetField("_runtimeRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        return (Task)loop.GetType().GetProperty("WhenIdle")!.GetValue(loop)!;
    }

    private static Task TranscriptLoad(SubsessionsViewModel model)
        => (Task)typeof(SubsessionsViewModel).GetField("_transcriptLoadOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;

    private sealed class ReadGate
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // A deterministic VM-port boundary. The existing ViewLifecycle regression
    // continues to cover the genuine generated runtime/header path and placement.
    private sealed class ResetPorts : ISubsessionSessionReader, ISubsessionCheckpointReader,
        ISubsessionTranscriptPageReader, ISubsessionChangeNotifications, ISubagentPresentationInitialization
    {
        private readonly DateTimeOffset _created = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        private readonly Guid _parent = Guid.NewGuid();
        private readonly Guid _item = Guid.NewGuid();
        private ReadGate? _nextAround;
        internal Guid SessionId { get; } = Guid.NewGuid();
        internal Guid OtherSessionId { get; } = Guid.NewGuid();
        internal Guid AnchorTurnId { get; } = Guid.NewGuid();
        internal Guid LatestTurnId { get; } = Guid.NewGuid();
        internal string Title { get; set; } = "Child";
        internal string Text { get; set; } = "Initial anchored content";
        internal int RecentReads { get; private set; }
        internal int AroundReads { get; private set; }
        internal bool BlockInitialization { get; init; }
        internal TaskCompletionSource InitializationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseInitialization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action? ResnapshotRequired;
        public event Action<Guid>? SessionChanged { add { } remove { } }
        public event Action<Guid, SubagentModels.AgentTurnRecord>? TurnChanged { add { } remove { } }

        internal void Reset() => ResnapshotRequired?.Invoke();
        internal ReadGate HoldNextAroundRead() => _nextAround = new();
        internal PackageViewNavigationContext Navigation() => new(SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                ["sessionId"] = SessionId.ToString("D"), ["turnId"] = AnchorTurnId.ToString("D"),
                ["itemId"] = _item.ToString("D"), ["anchorKind"] = "Text", ["createdAtUtc"] = _created.ToString("O"),
            });

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializationEntered.TrySetResult();
            if (BlockInitialization) await ReleaseInitialization.Task.WaitAsync(cancellationToken);
        }

        public Task<SubsessionSessionCatalog> ListSessionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionSessionCatalog([Session(SessionId, Title), Session(OtherSessionId, "Other child")], []));
        public Task<IReadOnlyList<SubagentModels.AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SubagentModels.AgentRunCheckpointRecord>>([]);

        public Task<SubsessionTranscriptPage> ListRecentTurnsAsync(Guid sessionId, int limit, CancellationToken cancellationToken = default)
        {
            RecentReads++;
            return Task.FromResult(new SubsessionTranscriptPage([Turn(sessionId, LatestTurnId, "Latest")], false));
        }
        public Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionTranscriptPage([], false));
        public Task<SubsessionTranscriptPage> ListTurnsAfterAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionTranscriptPage([], false));

        public async Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(Guid sessionId, Guid turnId,
            DateTimeOffset createdAtUtc, Guid itemId, int beforeLimit, int afterLimit, CancellationToken cancellationToken = default)
        {
            AroundReads++;
            var page = new SubsessionAroundTurnPage([Turn(sessionId, turnId, Text)], false, true, turnId);
            var gate = Interlocked.Exchange(ref _nextAround, null);
            if (gate is not null)
            {
                using var registration = cancellationToken.Register(() => gate.Cancelled.TrySetResult());
                gate.Started.TrySetResult();
                await gate.Release.Task; // Models a late provider response: VM fencing must reject it.
            }
            return page;
        }

        private SubagentModels.AgentSessionRecord Session(Guid id, string title) => new()
        {
            SessionId = id, Title = title, State = SubagentModels.AgentSessionState.Completed,
            CreatedAtUtc = new SunderRpcOffsetDateTime(_created.ToString("O")),
            UpdatedAtUtc = new SunderRpcOffsetDateTime(_created.ToString("O")),
            ParentSessionId = _parent, RootSessionId = _parent,
        };
        private SubagentModels.AgentTurnRecord Turn(Guid sessionId, Guid turnId, string text) => new()
        {
            TurnId = turnId, SessionId = sessionId, Role = SubagentModels.AgentMessageRole.Assistant,
            Kind = SubagentModels.AgentTurnKind.Message, ContentRevision = 1, IsStreaming = false,
            CreatedAtUtc = new SunderRpcOffsetDateTime(_created.ToString("O")),
            UpdatedAtUtc = new SunderRpcOffsetDateTime(_created.ToString("O")),
            Items = [new SubagentModels.AgentTurnItemRecord
            {
                ItemId = _item, TurnId = turnId, SequenceNumber = 0, Kind = SubagentModels.AgentTurnItemKind.Text,
                TextContent = text, WasTruncated = false, IsError = false,
                ToolDetailRevision = 0, IsToolHeaderProjection = false,
            }],
        };
    }
}
