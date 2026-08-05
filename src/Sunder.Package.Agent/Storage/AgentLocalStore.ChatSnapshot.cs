using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private const int ChatSnapshotProfileLimit = 100;
    private const int ChatSnapshotWorkspaceLimit = 100;
    private const int ChatSnapshotRootSessionLimit = 128;
    private const int ChatSnapshotChildSessionLimit = 128;
    private const int ChatSnapshotPermissionLimit = 100;
    private const int ChatSnapshotWorkspacePathLimit = 64;
    private const int ChatSnapshotTurnItemLimit = 16;

    internal async Task<AgentChatSnapshotProjection> ReadChatSnapshotAsync(
        long revision,
        AgentChatSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);

        var profileItems = ListProfiles(connection, transaction);
        var workspaceItems = ListChatWorkspaceRecords(connection, transaction);
        var selectedProfile = FindById(profileItems, request.PreferredProfileId);
        var selectedWorkspaceBase = FindById(workspaceItems, request.PreferredWorkspaceId);
        var selectedWorkspace = selectedWorkspaceBase is null
            ? null
            : selectedWorkspaceBase with
            {
                Paths = ListWorkspacePaths(
                    connection,
                    selectedWorkspaceBase.WorkspaceId,
                    transaction),
                Documents = [],
            };

        cancellationToken.ThrowIfCancellationRequested();
        var workspaceSessionRecords = selectedWorkspace is null
            ? []
            : ListChatSessionsForWorkspace(
                connection,
                transaction,
                selectedWorkspace.WorkspaceId);
        var rootSessions = workspaceSessionRecords
            .Where(static session => session.ParentSessionId is null)
            .ToArray();
        var selectedSessionRecord = ResolveSelectedRootSession(
            workspaceSessionRecords,
            rootSessions,
            request.PreferredSessionId);
        var includedSessions = SelectChatSessions(rootSessions, workspaceSessionRecords, selectedSessionRecord);
        var sessionItems = includedSessions
            .Select(session => new AgentSessionSnapshot(
                SanitizeSession(session),
                SanitizeCheckpoint(GetLatestCheckpoint(connection, session.SessionId, transaction))))
            .ToArray();
        var selectedSession = selectedSessionRecord is null
            ? null
            : sessionItems.FirstOrDefault(item =>
                item.Session.SessionId == selectedSessionRecord.SessionId);

        var limit = Math.Clamp(request.InitialTranscriptLimit, 1, 500);
        IReadOnlyList<AgentTurnRecord> turns = selectedSession is null
            || !request.IncludeInitialTranscript
            ? []
            : ListChatRecentTurns(
                connection,
                transaction,
                selectedSession.Session.SessionId,
                limit + 1);
        var hasMoreTurns = turns.Count > limit;
        if (hasMoreTurns)
        {
            turns = turns.Skip(turns.Count - limit).ToArray();
        }

        var permissions = selectedSession is null
            ? new AgentChatPermissionProjection(revision, null, [])
            : new AgentChatPermissionProjection(
                revision,
                ReadChatSessionPermissionState(
                    connection,
                    transaction,
                    selectedSession.Session.SessionId),
                ListChatPendingPermissionRequests(
                        connection,
                        transaction,
                        selectedSession.Session.SessionId)
                    .Take(ChatSnapshotPermissionLimit)
                    .Select(SanitizePermissionRequest)
                    .ToArray());
        var selectedWorkspaceBindings = selectedWorkspace is null
            ? []
            : ListWorkspaceBindings(
                    connection,
                    selectedWorkspace.WorkspaceId,
                    transaction)
                .Take(32)
                .Select(SanitizeWorkspaceBinding)
                .ToArray();

        transaction.Commit();

        var projectedProfiles = SelectBoundedIncluding(
                profileItems,
                selectedProfile,
                ChatSnapshotProfileLimit,
                static profile => profile.ProfileId,
                StringComparer.OrdinalIgnoreCase)
            .Select(SanitizeProfile)
            .ToArray();
        var projectedSelectedProfile = selectedProfile is null
            ? null
            : projectedProfiles.First(profile => string.Equals(
                profile.ProfileId,
                selectedProfile.ProfileId,
                StringComparison.OrdinalIgnoreCase));
        var projectedWorkspaces = SelectBoundedIncluding(
                workspaceItems,
                selectedWorkspaceBase,
                ChatSnapshotWorkspaceLimit,
                static workspace => workspace.WorkspaceId,
                StringComparer.OrdinalIgnoreCase)
            .Select(workspace => selectedWorkspace is not null
                                 && string.Equals(
                                     workspace.WorkspaceId,
                                     selectedWorkspace.WorkspaceId,
                                     StringComparison.OrdinalIgnoreCase)
                ? SanitizeWorkspace(selectedWorkspace, includePaths: true)
                : SanitizeWorkspace(workspace, includePaths: false))
            .ToArray();
        var projectedSelectedWorkspace = selectedWorkspace is null
            ? null
            : projectedWorkspaces.First(workspace => string.Equals(
                workspace.WorkspaceId,
                selectedWorkspace.WorkspaceId,
                StringComparison.OrdinalIgnoreCase));

        var snapshot = new AgentChatSnapshotProjection(
            revision,
            projectedProfiles,
            projectedWorkspaces,
            selectedWorkspaceBindings,
            projectedSelectedProfile,
            projectedSelectedWorkspace,
            selectedSession,
            sessionItems,
            new AgentTranscriptPage(
                revision,
                turns.Select(SanitizeTurn).ToArray(),
                hasMoreTurns),
            permissions);
        return AgentChatSnapshotPayload.Fit(snapshot);
    }

    private static IReadOnlyList<AgentWorkspaceRecord> ListChatWorkspaceRecords(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT WorkspaceId, DisplayName, Description, CreatedAtUtc, UpdatedAtUtc FROM AgentWorkspaces ORDER BY DisplayName, UpdatedAtUtc DESC;";
        using var reader = command.ExecuteReader();
        var workspaces = new List<AgentWorkspaceRecord>();
        while (reader.Read())
        {
            workspaces.Add(ReadWorkspace(reader));
        }

        return workspaces;
    }

    private static IReadOnlyList<AgentSessionRecord> ListChatSessionsForWorkspace(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workspaceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT SessionId, Title, State, CreatedAtUtc, UpdatedAtUtc, ParentSessionId, RootSessionId, ParentRunId, ParentRunRevision, ParentToolCallId, TaskId, ProfileId, BehaviorLoopId, AgentKind, WorkspaceId FROM AgentSessions WHERE WorkspaceId = $workspaceId ORDER BY UpdatedAtUtc DESC;";
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        return ReadSessions(command);
    }

    private static IReadOnlyList<AgentTurnRecord> ListChatRecentTurns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        int limit)
    {
        var turns = ListRecentTurnHeadersForSession(
                connection,
                sessionId,
                limit,
                transaction)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        var items = ListTranscriptHeaderItemsForTurns(
            connection,
            turns.Select(turn => turn.TurnId).ToArray(),
            transaction);
        return AttachItems(turns, items);
    }

    private static AgentSessionPermissionState ReadChatSessionPermissionState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT IsUnrestrictedModeEnabled FROM AgentSessionPermissionStates WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        var value = command.ExecuteScalar();
        return new AgentSessionPermissionState(
            sessionId,
            value is not null && Convert.ToInt64(value) != 0);
    }

    private static IReadOnlyList<AgentPendingPermissionRequestRecord> ListChatPendingPermissionRequests(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        var rootSessionId = sessionId;
        using (var rootCommand = connection.CreateCommand())
        {
            rootCommand.Transaction = transaction;
            rootCommand.CommandText = "SELECT RootSessionId FROM AgentSessions WHERE SessionId = $sessionId;";
            rootCommand.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            var value = rootCommand.ExecuteScalar();
            if (value is string text && Guid.TryParse(text, out var parsed))
            {
                rootSessionId = parsed;
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {PendingPermissionColumns} FROM AgentPendingPermissionRequests WHERE Status IN ('Pending', 'Claimed') AND (SessionId = $sessionId OR RootSessionId = $rootSessionId OR ParentSessionId = $sessionId) ORDER BY CreatedAtUtc DESC;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$rootSessionId", rootSessionId.ToString());
        using var reader = command.ExecuteReader();
        var requests = new List<AgentPendingPermissionRequestRecord>();
        while (reader.Read())
        {
            requests.Add(ReadPendingPermissionRequest(reader));
        }

        return requests;
    }

    private static AgentSessionRecord? ResolveSelectedRootSession(
        IReadOnlyList<AgentSessionRecord> workspaceSessions,
        IReadOnlyList<AgentSessionRecord> rootSessions,
        Guid? selectedSessionId)
    {
        var selected = selectedSessionId is null
            ? null
            : workspaceSessions.FirstOrDefault(session => session.SessionId == selectedSessionId.Value);
        if (selected?.ParentSessionId is not null)
        {
            var rootSessionId = selected.RootSessionId ?? selected.ParentSessionId.Value;
            selected = rootSessions.FirstOrDefault(session => session.SessionId == rootSessionId);
        }

        return selected is { ParentSessionId: null }
            ? selected
            : rootSessions.FirstOrDefault();
    }

    private static IReadOnlyList<AgentSessionRecord> SelectChatSessions(
        IReadOnlyList<AgentSessionRecord> rootSessions,
        IReadOnlyList<AgentSessionRecord> workspaceSessions,
        AgentSessionRecord? selectedSession)
    {
        var selectedRoots = SelectBoundedIncluding(
            rootSessions,
            selectedSession,
            ChatSnapshotRootSessionLimit,
            static session => session.SessionId,
            EqualityComparer<Guid>.Default);
        if (selectedSession is null)
        {
            return selectedRoots;
        }

        var children = workspaceSessions
            .Where(session => session.ParentSessionId is not null
                              && (session.RootSessionId == selectedSession.SessionId
                                  || session.ParentSessionId == selectedSession.SessionId))
            .OrderBy(session => session.CreatedAtUtc)
            .Take(ChatSnapshotChildSessionLimit);
        return selectedRoots.Concat(children).ToArray();
    }

    private static T? FindById<T>(IReadOnlyList<T> items, string? preferredId)
        where T : class
    {
        if (typeof(T) == typeof(AgentProfileRecord))
        {
            return (T?)(object?)((IReadOnlyList<AgentProfileRecord>)(object)items)
                .FirstOrDefault(profile => string.Equals(
                    profile.ProfileId,
                    preferredId,
                    StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault();
        }

        return (T?)(object?)((IReadOnlyList<AgentWorkspaceRecord>)(object)items)
            .FirstOrDefault(workspace => string.Equals(
                workspace.WorkspaceId,
                preferredId,
                StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault();
    }

    private static IReadOnlyList<T> SelectBoundedIncluding<T, TKey>(
        IReadOnlyList<T> items,
        T? required,
        int limit,
        Func<T, TKey> keySelector,
        IEqualityComparer<TKey> comparer)
        where T : class
    {
        var selected = items.Take(limit).ToList();
        if (required is null || selected.Any(item => comparer.Equals(
                keySelector(item),
                keySelector(required))))
        {
            return selected;
        }

        if (selected.Count == limit)
        {
            selected.RemoveAt(selected.Count - 1);
        }
        selected.Add(required);
        return selected;
    }

    private static AgentProfileRecord SanitizeProfile(AgentProfileRecord profile)
        => profile with
        {
            DisplayName = Truncate(profile.DisplayName, 256),
            Description = null,
            Instructions = null,
            EmbeddingProviderId = null,
            EmbeddingModelId = null,
            ModelBindings = (profile.ModelBindings ?? [])
                .Where(binding => string.Equals(
                    binding.CapabilityKind,
                    AgentModelCapabilityKinds.Chat,
                    StringComparison.OrdinalIgnoreCase))
                .Take(1)
                .Select(binding => binding with
                {
                    SettingsJson = null,
                })
                .ToArray(),
            SelectableCapabilityAssignments = [],
            BehaviorLoopSourceId = null,
            BehaviorLoopSettingsJson = null,
        };

    private static AgentWorkspaceRecord SanitizeWorkspace(
        AgentWorkspaceRecord workspace,
        bool includePaths)
        => workspace with
        {
            DisplayName = Truncate(workspace.DisplayName, 256),
            Description = TruncateNullable(workspace.Description, 1024),
            Paths = includePaths
                ? workspace.Paths
                    .Take(ChatSnapshotWorkspacePathLimit)
                    .Select(path => path with
                    {
                        HostPath = Truncate(path.HostPath, 1024),
                    })
                    .ToArray()
                : [],
            Documents = [],
        };

    private static AgentWorkspaceBindingRecord SanitizeWorkspaceBinding(
        AgentWorkspaceBindingRecord binding)
        => binding;

    private static AgentSessionRecord SanitizeSession(AgentSessionRecord session)
        => session with
        {
            Title = Truncate(session.Title, 512),
            AgentKind = TruncateNullable(session.AgentKind, 128),
        };

    private static AgentRunCheckpointRecord? SanitizeCheckpoint(AgentRunCheckpointRecord? checkpoint)
        => checkpoint is null
            ? null
            : checkpoint with { Summary = TruncateNullable(checkpoint.Summary, 2048) };

    private static AgentTurnRecord SanitizeTurn(AgentTurnRecord turn)
        => turn with
        {
            Items = turn.Items
                .OrderBy(item => item.SequenceNumber)
                .Take(ChatSnapshotTurnItemLimit)
                .Select(item => item with
                {
                    ArgumentsJson = BoundJson(item.ArgumentsJson, 4096),
                    ResultSummary = TruncateNullable(item.ResultSummary, 4096),
                    StructuredPayloadJson = BoundJson(item.StructuredPayloadJson, 4096),
                    SourcesJson = BoundJson(item.SourcesJson, 4096),
                    ErrorCode = TruncateNullable(item.ErrorCode, 256),
                    PresentationPayloadJson = BoundJson(item.PresentationPayloadJson, 4096),
                    WasTruncated = item.WasTruncated
                                   || turn.Items.Count > ChatSnapshotTurnItemLimit
                                   || WasTruncated(item.ArgumentsJson, 4096)
                                   || WasTruncated(item.StructuredPayloadJson, 4096)
                                   || WasTruncated(item.SourcesJson, 4096)
                                   || WasTruncated(item.PresentationPayloadJson, 4096),
                })
                .ToArray(),
        };

    private static AgentPendingPermissionRequestRecord SanitizePermissionRequest(
        AgentPendingPermissionRequestRecord request)
        => request with
        {
            UserMessage = string.Empty,
            Summary = Truncate(request.Summary, 4096),
            ArgumentsJson = "{}",
            Command = TruncateNullable(request.Command, 2048),
            Path = TruncateNullable(request.Path, 1024),
            ResourceDisplayName = TruncateNullable(request.ResourceDisplayName, 512),
            ResourceReference = TruncateNullable(request.ResourceReference, 1024),
            ClaimToken = null,
            DecisionSummary = null,
            ExecutionFingerprint = string.Empty,
            ContinuationToken = null,
            ExecutionSnapshotJson = string.Empty,
        };

    private static bool WasTruncated(string? value, int maximumLength)
        => value?.Length > maximumLength;

    private static string Truncate(string value, int maximumLength)
        => value.Length <= maximumLength ? value : value[..maximumLength];

    private static string? TruncateNullable(string? value, int maximumLength)
        => value is null ? null : Truncate(value, maximumLength);

    private static string? BoundJson(string? value, int maximumLength)
        => value?.Length <= maximumLength ? value : null;
}

internal static class AgentChatSnapshotPayload
{
    internal const int RuntimeMaximumBytes = AgentRuntimePayloadLimits.RuntimeMaximumResponseBytes;
    internal const int MaximumSerializedBytes = RuntimeMaximumBytes - (64 * 1024);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static int GetSerializedByteCount(AgentChatSnapshotProjection snapshot)
        => JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions).Length;

    internal static AgentChatSnapshotProjection RoundTrip(AgentChatSnapshotProjection snapshot)
        => JsonSerializer.Deserialize<AgentChatSnapshotProjection>(
               JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions),
               JsonOptions)
           ?? throw new InvalidOperationException("Agent Chat snapshot JSON could not be deserialized.");

    internal static AgentChatSnapshotProjection Fit(AgentChatSnapshotProjection snapshot)
    {
        if (snapshot.InitialTranscript.Turns.FirstOrDefault() is { } oldestTurn)
        {
            snapshot = snapshot with
            {
                InitialTranscript = snapshot.InitialTranscript with
                {
                    Continuation = snapshot.InitialTranscript.Continuation
                                   ?? TranscriptPageCursor.FromTurn(oldestTurn),
                },
            };
        }

        var initialTranscriptProjected = false;
        var serializedBytes = GetSerializedByteCount(snapshot);
        while (serializedBytes > MaximumSerializedBytes)
        {
            if (snapshot.InitialTranscript.Turns.Count > 1)
            {
                var turnCount = snapshot.InitialTranscript.Turns.Count;
                var removeCount = Math.Clamp(
                    (int)Math.Ceiling(
                        turnCount * (serializedBytes - MaximumSerializedBytes) / (double)serializedBytes),
                    1,
                    turnCount - 1);
                var retainedTurns = snapshot.InitialTranscript.Turns.Skip(removeCount).ToArray();
                snapshot = snapshot with
                {
                    InitialTranscript = snapshot.InitialTranscript with
                    {
                        Turns = retainedTurns,
                        HasMore = true,
                        Continuation = TranscriptPageCursor.FromTurn(retainedTurns[0]),
                    },
                };
                serializedBytes = GetSerializedByteCount(snapshot);
                continue;
            }

            if (!initialTranscriptProjected
                && snapshot.InitialTranscript.Turns.FirstOrDefault() is { } retainedTurn)
            {
                var characterBudget = AgentRuntimePayloadLimits.InitialProjectedTurnCharacterBudget;
                while (true)
                {
                    snapshot = snapshot with
                    {
                        InitialTranscript = snapshot.InitialTranscript with
                        {
                            Turns =
                            [
                                TranscriptTurnTransportProjection.Project(
                                    retainedTurn,
                                    characterBudget,
                                    AgentRuntimePayloadLimits.MaximumProjectedTurnItems),
                            ],
                            HasMore = true,
                            Continuation = TranscriptPageCursor.FromTurn(retainedTurn),
                        },
                    };
                    serializedBytes = GetSerializedByteCount(snapshot);
                    if (serializedBytes <= MaximumSerializedBytes)
                    {
                        return snapshot;
                    }

                    if (characterBudget == 0)
                    {
                        break;
                    }
                    characterBudget /= 2;
                }

                initialTranscriptProjected = true;
                continue;
            }

            var removableSession = snapshot.WorkspaceSessions
                .LastOrDefault(item => item.Session.SessionId != snapshot.SelectedSession?.Session.SessionId);
            if (removableSession is not null)
            {
                snapshot = snapshot with
                {
                    WorkspaceSessions = snapshot.WorkspaceSessions
                        .Where(item => item.Session.SessionId != removableSession.Session.SessionId)
                        .ToArray(),
                };
                serializedBytes = GetSerializedByteCount(snapshot);
                continue;
            }

            if (snapshot.Permissions.PendingRequests.Count > 0)
            {
                snapshot = snapshot with
                {
                    Permissions = snapshot.Permissions with
                    {
                        PendingRequests = snapshot.Permissions.PendingRequests.SkipLast(1).ToArray(),
                    },
                };
                serializedBytes = GetSerializedByteCount(snapshot);
                continue;
            }

            var removableProfile = snapshot.Profiles.LastOrDefault(profile => !string.Equals(
                profile.ProfileId,
                snapshot.SelectedProfile?.ProfileId,
                StringComparison.OrdinalIgnoreCase));
            if (removableProfile is not null)
            {
                snapshot = snapshot with
                {
                    Profiles = snapshot.Profiles
                        .Where(profile => !string.Equals(
                            profile.ProfileId,
                            removableProfile.ProfileId,
                            StringComparison.OrdinalIgnoreCase))
                        .ToArray(),
                };
                serializedBytes = GetSerializedByteCount(snapshot);
                continue;
            }

            var removableWorkspace = snapshot.Workspaces.LastOrDefault(workspace => !string.Equals(
                workspace.WorkspaceId,
                snapshot.SelectedWorkspace?.WorkspaceId,
                StringComparison.OrdinalIgnoreCase));
            if (removableWorkspace is not null)
            {
                snapshot = snapshot with
                {
                    Workspaces = snapshot.Workspaces
                        .Where(workspace => !string.Equals(
                            workspace.WorkspaceId,
                            removableWorkspace.WorkspaceId,
                            StringComparison.OrdinalIgnoreCase))
                        .ToArray(),
                };
                serializedBytes = GetSerializedByteCount(snapshot);
                continue;
            }

            if (snapshot.SelectedWorkspace is { Paths.Count: > 0 } selectedWorkspace)
            {
                var reducedWorkspace = selectedWorkspace with
                {
                    Paths = selectedWorkspace.Paths.SkipLast(1).ToArray(),
                };
                snapshot = snapshot with
                {
                    SelectedWorkspace = reducedWorkspace,
                    Workspaces = snapshot.Workspaces
                        .Select(workspace => string.Equals(
                            workspace.WorkspaceId,
                            reducedWorkspace.WorkspaceId,
                            StringComparison.OrdinalIgnoreCase)
                            ? reducedWorkspace
                            : workspace)
                        .ToArray(),
                };
                serializedBytes = GetSerializedByteCount(snapshot);
                continue;
            }

            throw new InvalidOperationException(
                "Agent Chat startup data exceeds the Runtime response limit after applying payload bounds.");
        }

        return snapshot;
    }
}
