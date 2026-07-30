using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentSessionContinuityTests
{
    private static readonly AgentProviderRunCapabilities DefaultCapabilities = new(
        SupportsNativeToolCalling: true,
        SupportsStreamingToolCalls: true,
        SupportsMultipleToolCalls: true,
        Summary: "test",
        ContextWindowTokens: 128_000,
        MaxOutputTokens: 8_192);

    [Fact]
    public async Task Projection_UsesContiguousPrefix_WhenToolPairCrossesInitialBoundary()
    {
        using var fixture = ContinuityFixture.Create();
        var turns = new List<AgentTurnRecord>();
        for (var index = 0; index < 20; index++)
        {
            turns.Add(index switch
            {
                3 => fixture.Store.AppendToolCallTurn(
                    fixture.SessionId,
                    AgentMessageRole.Assistant,
                    "crossing-call",
                    "read",
                    "{\"path\":\"src/Crossing.cs\"}"),
                5 => fixture.Store.AppendToolResultTurn(
                    fixture.SessionId,
                    "crossing-call",
                    "read",
                    null,
                    "file data",
                    "read complete",
                    null,
                    null,
                    wasTruncated: false,
                    isError: false,
                    errorCode: null,
                    backendId: null),
                _ => fixture.Store.AppendTextTurn(
                    fixture.SessionId,
                    index % 2 == 0 ? AgentMessageRole.User : AgentMessageRole.Assistant,
                    $"history-{index:00}"),
            });
        }
        fixture.StartRun();

        var projection = await fixture.BuildProjectionAsync();

        Assert.Equal(6, projection.OmittedHistoricalTurnCount);
        Assert.Equal(turns[5].TurnId, projection.ContextCheckpoint!.LastOmittedTurnId);
        Assert.Equal(turns.Skip(6).Select(turn => turn.TurnId).Append(fixture.ActiveUserTurnId),
            projection.PromptTurns.Select(turn => turn.TurnId));
    }

    [Fact]
    public async Task Projection_ComposesExactAnchorAndImmediateRawTail_WithoutOverlapOrGap()
    {
        using var fixture = ContinuityFixture.Create();
        fixture.AppendAlternatingHistory(36);
        fixture.StartRun();
        var durableBefore = fixture.Store.ListTurns(fixture.SessionId);

        var projection = await fixture.BuildProjectionAsync();

        var checkpoint = Assert.IsType<AgentSessionContextCheckpointRecord>(projection.ContextCheckpoint);
        var omitted = durableBefore.Take(projection.OmittedHistoricalTurnCount).ToArray();
        var raw = durableBefore.Skip(projection.OmittedHistoricalTurnCount).ToArray();
        Assert.Equal(omitted[0].TurnId, checkpoint.FirstOmittedTurnId);
        Assert.Equal(omitted[^1].TurnId, checkpoint.LastOmittedTurnId);
        Assert.Equal(raw.Select(turn => turn.TurnId), projection.PromptTurns.Select(turn => turn.TurnId));
        Assert.Empty(omitted.Select(turn => turn.TurnId).Intersect(projection.PromptTurns.Select(turn => turn.TurnId)));
    }

    [Fact]
    public async Task Projection_ActiveAnchorNeverRetreats_WhenPromptOverheadDrops()
    {
        using var fixture = ContinuityFixture.Create();
        for (var index = 0; index < 50; index++)
        {
            fixture.Store.AppendTextTurn(
                fixture.SessionId,
                index % 2 == 0 ? AgentMessageRole.User : AgentMessageRole.Assistant,
                $"history-{index:00}-" + new string('x', 1_500));
        }
        fixture.StartRun();
        var constrained = DefaultCapabilities with { ContextWindowTokens = 12_000, MaxOutputTokens = 1_000 };

        var first = await fixture.BuildProjectionAsync(constrained, promptOverheadTokens: 4_000);
        var second = await fixture.BuildProjectionAsync(constrained, promptOverheadTokens: 0);

        Assert.True(first.OmittedHistoricalTurnCount > 34);
        Assert.Equal(first.OmittedHistoricalTurnCount, second.OmittedHistoricalTurnCount);
        Assert.Equal(first.ContextCheckpoint!.LastOmittedTurnId, second.ContextCheckpoint!.LastOmittedTurnId);
    }

    [Fact]
    public async Task Projection_UsesUtf8TokenEstimateForNonAsciiHistory()
    {
        using var fixture = ContinuityFixture.Create();
        for (var index = 0; index < 36; index++)
        {
            fixture.Store.AppendTextTurn(
                fixture.SessionId,
                index % 2 == 0 ? AgentMessageRole.User : AgentMessageRole.Assistant,
                $"history-{index:00}-" + new string('\u754c', 1_000));
        }
        fixture.StartRun();
        var capabilities = DefaultCapabilities with { ContextWindowTokens = 12_000, MaxOutputTokens = 1_000 };
        var snapshot = fixture.ReadSnapshot();
        var historicalTurns = snapshot.Turns.Where(turn => turn.TurnId != fixture.ActiveUserTurnId).ToArray();
        var details = AgentSessionContinuitySummaryBuilder.BuildDeterministic(null, historicalTurns);
        var renderedSummary = AgentSessionContinuitySummaryBuilder.RenderSummary(
            details,
            historicalTurns.Length,
            historicalTurns[^1]);
        var detailsJson = AgentSessionContinuitySummaryBuilder.SerializeDetails(details);

        Assert.True(renderedSummary.Length <= 12_000, $"Summary length: {renderedSummary.Length}");
        Assert.True(detailsJson.Length <= 64_000, $"Details length: {detailsJson.Length}");

        var projection = await fixture.BuildProjectionAsync(capabilities);
        var limits = AgentProviderRequestLimits.Resolve(capabilities);
        var projectedTokens = projection.PromptTurns.Sum(AgentProviderRequestBudget.EstimateTurnTokens);

        Assert.NotNull(projection.ContextCheckpoint);
        Assert.True(projectedTokens <= limits.ProactiveInputLimitTokens);
    }

    [Fact]
    public void CheckpointCas_RejectsHistoricalAppendBeforeAnchor()
    {
        using var fixture = ContinuityFixture.CreateStartedWithHistory(24);
        var snapshot = fixture.ReadSnapshot();
        var request = CreateSaveRequest(snapshot, 8);
        var inserted = fixture.Store.AppendTextTurn(fixture.SessionId, AgentMessageRole.User, "historically inserted");
        SetTurnCreatedAt(fixture.Store.DatabasePath, inserted.TurnId, snapshot.Turns[0].CreatedAtUtc.AddMinutes(-1));

        Assert.Null(fixture.Store.TrySaveAnchoredSessionContextCheckpoint(request));
    }

    [Fact]
    public void CheckpointCas_AllowsStreamingTailAppendAfterAnchor()
    {
        using var fixture = ContinuityFixture.CreateStartedWithHistory(24);
        var snapshot = fixture.ReadSnapshot();
        var request = CreateSaveRequest(snapshot, 8);

        var tail = fixture.Store.TryAppendTextTurn(
            snapshot.SourceRun,
            snapshot.SourceRunEpoch,
            AgentMessageRole.Assistant,
            "streaming tail");

        Assert.NotNull(tail);
        Assert.NotNull(fixture.Store.TrySaveAnchoredSessionContextCheckpoint(request));
    }

    [Fact]
    public async Task Projection_IncludesStreamingTailAppendedDuringRefinement()
    {
        ContinuityFixture? fixture = null;
        var refiner = new CallbackRefiner(() =>
        {
            var snapshot = fixture!.ReadSnapshot();
            var tail = fixture.Store.TryAppendTextTurn(
                snapshot.SourceRun,
                snapshot.SourceRunEpoch,
                AgentMessageRole.Assistant,
                "concurrent streaming tail");
            Assert.NotNull(tail);
            return null;
        });
        using (fixture = ContinuityFixture.CreateStartedWithHistory(24, refiner))
        {
            var projection = await fixture.BuildProjectionAsync();

            Assert.Contains(
                projection.PromptTurns,
                turn => turn.Items.Any(item => item.TextContent == "concurrent streaming tail"));
        }
    }

    [Fact]
    public async Task Projection_ReloadsTranscriptWhenRefinedCheckpointLosesCas()
    {
        ContinuityFixture? fixture = null;
        Guid replacedTurnId = default;
        var refiner = new CallbackRefiner(() =>
        {
            fixture!.Store.UpdateTextTurn(replacedTurnId, "replacement after deterministic checkpoint");
            return CreateRefinement();
        });
        using (fixture = ContinuityFixture.CreateStartedWithHistory(24, refiner))
        {
            replacedTurnId = fixture.Store.ListTurns(fixture.SessionId)[0].TurnId;

            var projection = await fixture.BuildProjectionAsync();

            Assert.Contains(
                projection.PromptTurns,
                turn => turn.Items.Any(item => item.TextContent == "replacement after deterministic checkpoint"));
            Assert.DoesNotContain(
                projection.PromptTurns,
                turn => turn.Items.Any(item => item.TextContent == "history-000"));
        }
    }

    [Fact]
    public async Task Projection_RejectsWhenActiveUserTurnIsRemovedDuringRefinement()
    {
        ContinuityFixture? fixture = null;
        var refiner = new CallbackRefiner(() =>
        {
            fixture!.Store.RollbackTranscript(fixture.SessionId, fixture.ActiveUserTurnId);
            return CreateRefinement();
        });
        using (fixture = ContinuityFixture.CreateStartedWithHistory(24, refiner))
        {
            await Assert.ThrowsAsync<AgentRunTranscriptWriteRejectedException>(
                () => fixture.BuildProjectionAsync());
        }
    }

    [Fact]
    public void CheckpointCas_RejectsCoveredReplacement()
    {
        using var fixture = ContinuityFixture.CreateStartedWithHistory(24);
        var snapshot = fixture.ReadSnapshot();
        var request = CreateSaveRequest(snapshot, 8);

        fixture.Store.UpdateTextTurn(snapshot.Turns[0].TurnId, "replacement");

        Assert.Null(fixture.Store.TrySaveAnchoredSessionContextCheckpoint(request));
    }

    [Fact]
    public void CheckpointCas_RejectsRollback()
    {
        using var fixture = ContinuityFixture.CreateStartedWithHistory(24);
        var snapshot = fixture.ReadSnapshot();
        var request = CreateSaveRequest(snapshot, 8);
        var rollbackAnchor = snapshot.Turns.First(turn =>
            turn.Role == AgentMessageRole.User && snapshot.Turns.IndexOf(turn) >= 10);

        fixture.Store.RollbackTranscript(fixture.SessionId, rollbackAnchor.TurnId);

        Assert.Null(fixture.Store.TrySaveAnchoredSessionContextCheckpoint(request));
    }

    [Fact]
    public void CheckpointCas_RejectsRunOwnershipLoss()
    {
        using var fixture = ContinuityFixture.CreateStartedWithHistory(24);
        var snapshot = fixture.ReadSnapshot();
        var request = CreateSaveRequest(snapshot, 8);

        fixture.Store.ReserveRun(fixture.SessionId, "profile.test", "new owner");

        Assert.Null(fixture.Store.TrySaveAnchoredSessionContextCheckpoint(request));
    }

    [Fact]
    public void CheckpointCas_RejectsLosingActiveGeneration()
    {
        using var fixture = ContinuityFixture.CreateStartedWithHistory(24);
        var snapshot = fixture.ReadSnapshot();
        var request = CreateSaveRequest(snapshot, 8);

        Assert.NotNull(fixture.Store.TrySaveAnchoredSessionContextCheckpoint(request));

        Assert.Null(fixture.Store.TrySaveAnchoredSessionContextCheckpoint(request));
    }

    [Fact]
    public void GeneratedCheckpointHistory_IsBoundedWithoutDeletingLegacyRows()
    {
        using var fixture = ContinuityFixture.CreateStartedWithHistory(24);
        fixture.Store.SaveSessionContextCheckpoint(
            fixture.SessionId,
            null,
            null,
            0,
            "legacy row",
            null);
        for (var generation = 0; generation < 20; generation++)
        {
            var snapshot = fixture.ReadSnapshot();
            Assert.NotNull(fixture.Store.TrySaveAnchoredSessionContextCheckpoint(
                CreateSaveRequest(snapshot, 8)));
        }

        Assert.Equal(12, CountCheckpointsByKind(fixture.Store.DatabasePath, fixture.SessionId, legacy: false));
        Assert.Equal(1, CountCheckpointsByKind(fixture.Store.DatabasePath, fixture.SessionId, legacy: true));
    }

    [Fact]
    public async Task Rollback_AdvancesTranscriptEpochAndDeactivatesCheckpointAtomically()
    {
        using var fixture = ContinuityFixture.CreateStartedWithHistory(30);
        var projection = await fixture.BuildProjectionAsync();
        Assert.NotNull(projection.ContextCheckpoint);
        var epoch = fixture.Store.GetTranscriptEpoch(fixture.SessionId);
        var rollbackAnchor = fixture.Store.ListTurns(fixture.SessionId)
            .Where(turn => turn.Role == AgentMessageRole.User)
            .Skip(6)
            .First();

        fixture.Store.RollbackTranscript(fixture.SessionId, rollbackAnchor.TurnId);

        Assert.Equal(epoch + 1, fixture.Store.GetTranscriptEpoch(fixture.SessionId));
        Assert.Null(fixture.Store.GetLatestSessionContextCheckpoint(fixture.SessionId));
        Assert.True(CountRows(
            fixture.Store.DatabasePath,
            "AgentSessionContextCheckpoints",
            fixture.SessionId) > 0);
    }

    [Fact]
    public void LegacyCheckpoint_RemainsStoredButIsNotActiveOrInjectable()
    {
        using var fixture = ContinuityFixture.Create();
        fixture.AppendAlternatingHistory(4);
        var turns = fixture.Store.ListTurns(fixture.SessionId);

        fixture.Store.SaveSessionContextCheckpoint(
            fixture.SessionId,
            turns[0].TurnId,
            turns[^1].TurnId,
            turns.Count,
            "legacy summary",
            null);

        Assert.Null(fixture.Store.GetLatestSessionContextCheckpoint(fixture.SessionId));
        Assert.Equal("Legacy", ReadScalarString(
            fixture.Store.DatabasePath,
            "SELECT CheckpointKind FROM AgentSessionContextCheckpoints WHERE SessionId = $sessionId;",
            fixture.SessionId));
    }

    [Fact]
    public async Task GenerationFailure_PreservesDeterministicCheckpoint()
    {
        using var fixture = ContinuityFixture.CreateStartedWithHistory(
            30,
            new ThrowingRefiner(new InvalidOperationException("provider unavailable")));

        var projection = await fixture.BuildProjectionAsync();

        Assert.NotNull(projection.ContextCheckpoint);
        Assert.Contains("## Goal", projection.ContextCheckpoint!.SummaryText, StringComparison.Ordinal);
        Assert.Equal("Deterministic", ReadActiveCheckpointKind(fixture));
    }

    [Fact]
    public async Task DeterministicGeneration_DoesNotSummarizeSystemTurns()
    {
        using var fixture = ContinuityFixture.Create();
        fixture.Store.AppendTextTurn(
            fixture.SessionId,
            AgentMessageRole.System,
            "hidden-system-content-must-not-be-projected");
        fixture.AppendAlternatingHistory(24);
        fixture.StartRun();

        var projection = await fixture.BuildProjectionAsync();

        Assert.NotNull(projection.ContextCheckpoint);
        Assert.DoesNotContain(
            "hidden-system-content-must-not-be-projected",
            projection.ContextCheckpoint!.SummaryText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "hidden-system-content-must-not-be-projected",
            projection.ContextCheckpoint.DetailsJson ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAfterPersistingDeterministicCheckpoint()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = ContinuityFixture.CreateStartedWithHistory(
            30,
            new CancelingRefiner(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.BuildProjectionAsync(cancellationToken: cancellation.Token));

        Assert.Equal("Deterministic", ReadActiveCheckpointKind(fixture));
    }

    [Fact]
    public async Task IndependentUtilityCancellation_PreservesDeterministicCheckpoint()
    {
        using var fixture = ContinuityFixture.CreateStartedWithHistory(
            30,
            new ThrowingRefiner(new OperationCanceledException("independent utility timeout")));

        var projection = await fixture.BuildProjectionAsync();

        Assert.NotNull(projection.ContextCheckpoint);
        Assert.Equal("Deterministic", ReadActiveCheckpointKind(fixture));
    }

    [Fact]
    public async Task ModelRefinement_ReplacesTheSameAnchoredGeneration()
    {
        var refinedDetails = new AgentContinuitySummaryDocument(
            ["Refined goal"],
            ["Refined decision"],
            ["Refined completed work"],
            ["Refined active work"],
            [],
            ["Refined next action"],
            ["src/Refined.cs"]);
        using var fixture = ContinuityFixture.CreateStartedWithHistory(
            30,
            new StaticRefiner(new AgentContinuityModelRefinement(
                refinedDetails,
                "utility-provider",
                "utility-model")));

        var projection = await fixture.BuildProjectionAsync();

        Assert.Contains("Refined goal", projection.ContextCheckpoint!.SummaryText, StringComparison.Ordinal);
        Assert.Equal("ModelRefined", ReadActiveCheckpointKind(fixture));
        Assert.Equal("utility-provider", ReadActiveCheckpointColumn(fixture, "ProviderId"));
        Assert.Equal("utility-model", ReadActiveCheckpointColumn(fixture, "ModelId"));
        Assert.Equal(2L, ReadActiveCheckpointInt64(fixture, "Generation"));
    }

    [Fact]
    public async Task Projection_ReportsCompactionRunningThenCompletedWithoutTranscriptMutation()
    {
        var refiner = new BlockingRefiner();
        using var fixture = ContinuityFixture.CreateStartedWithHistory(30, refiner);
        var before = JsonSerializer.Serialize(fixture.Store.ListTurns(fixture.SessionId));
        var updates = new List<AgentRunActivityUpdate>();
        fixture.SessionService.RunActivityChanged += (_, update) => updates.Add(update);

        var projectionTask = fixture.BuildProjectionAsync();
        await refiner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var running = Assert.Single(updates);
        Assert.Equal(AgentRunActivityKind.Processing, running.Kind);
        Assert.Equal("Running session compaction", running.Text);

        refiner.Release.TrySetResult();
        var projection = await projectionTask;

        Assert.True(projection.SummaryUpdated);
        Assert.Equal(
            ["Running session compaction", "Session compaction completed"],
            updates.Select(update => update.Text));
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Store.ListTurns(fixture.SessionId)));

        await fixture.BuildProjectionAsync();
        Assert.Equal(2, updates.Count);
    }

    [Fact]
    public async Task ActiveCheckpoint_PersistsAcrossStoreRestart()
    {
        using var fixture = ContinuityFixture.CreateStartedWithHistory(30);
        var projection = await fixture.BuildProjectionAsync();

        var restarted = new AgentLocalStore(fixture.Context);
        var restored = restarted.GetLatestSessionContextCheckpoint(fixture.SessionId);

        Assert.NotNull(restored);
        Assert.Equal(projection.ContextCheckpoint!.ContextCheckpointId, restored!.ContextCheckpointId);
        Assert.Equal(projection.ContextCheckpoint.LastOmittedTurnId, restored.LastOmittedTurnId);
    }

    [Fact]
    public void TranscriptOrdering_UsesCreatedAtThenTurnIdTextOrdinal_ForPagingAndRollback()
    {
        using var fixture = ContinuityFixture.Create();
        var inserted = Enumerable.Range(0, 6)
            .Select(index => fixture.Store.AppendTextTurn(
                fixture.SessionId,
                AgentMessageRole.User,
                $"tie-{index}"))
            .ToArray();
        var tiedAt = DateTimeOffset.Parse("2026-07-23T12:00:00.0000000+00:00");
        foreach (var turn in inserted)
        {
            SetTurnCreatedAt(fixture.Store.DatabasePath, turn.TurnId, tiedAt);
        }
        var expected = inserted.OrderBy(
            turn => turn.TurnId.ToString("D"),
            StringComparer.Ordinal).ToArray();

        Assert.Equal(expected.Select(turn => turn.TurnId),
            fixture.Store.ListTurns(fixture.SessionId).Select(turn => turn.TurnId));
        Assert.Equal(expected.Take(3).Select(turn => turn.TurnId),
            fixture.Store.ListTurnsBefore(fixture.SessionId, tiedAt, expected[3].TurnId, 10)
                .Select(turn => turn.TurnId));
        Assert.Equal(expected.Skip(4).Select(turn => turn.TurnId),
            fixture.Store.ListTurnsAfter(fixture.SessionId, tiedAt, expected[3].TurnId, 10)
                .Select(turn => turn.TurnId));

        fixture.Store.RollbackTranscript(fixture.SessionId, expected[3].TurnId);

        Assert.Equal(expected.Take(3).Select(turn => turn.TurnId),
            fixture.Store.ListTurns(fixture.SessionId).Select(turn => turn.TurnId));
    }

    [Fact]
    public async Task Projection_DoesNotMutateOrPruneStoredTurnsItemsArgumentsOrOutputs()
    {
        using var fixture = ContinuityFixture.Create();
        fixture.AppendAlternatingHistory(20);
        fixture.Store.AppendToolCallTurn(
            fixture.SessionId,
            AgentMessageRole.Assistant,
            "durable-call",
            "read",
            "{\"path\":\"src/Durable.cs\",\"keep\":\"all arguments\"}");
        fixture.Store.AppendToolResultTurn(
            fixture.SessionId,
            "durable-call",
            "read",
            "{\"path\":\"src/Durable.cs\"}",
            new string('o', 8_000),
            "durable output",
            "{\"full\":true}",
            "[\"source\"]",
            wasTruncated: false,
            isError: false,
            errorCode: null,
            backendId: "test");
        fixture.AppendAlternatingHistory(20);
        fixture.StartRun();
        var before = JsonSerializer.Serialize(fixture.Store.ListTurns(fixture.SessionId));
        var turnCount = CountRows(fixture.Store.DatabasePath, "AgentTurns", fixture.SessionId);
        var itemCount = CountTurnItems(fixture.Store.DatabasePath, fixture.SessionId);

        await fixture.BuildProjectionAsync(
            DefaultCapabilities with { ContextWindowTokens = 10_000, MaxOutputTokens = 1_000 },
            promptOverheadTokens: 2_000);

        Assert.Equal(before, JsonSerializer.Serialize(fixture.Store.ListTurns(fixture.SessionId)));
        Assert.Equal(turnCount, CountRows(fixture.Store.DatabasePath, "AgentTurns", fixture.SessionId));
        Assert.Equal(itemCount, CountTurnItems(fixture.Store.DatabasePath, fixture.SessionId));
    }

    private static AgentSessionContextCheckpointSaveRequest CreateSaveRequest(
        AgentSessionContinuitySnapshot snapshot,
        int prefixCount)
    {
        var prefix = snapshot.Turns.Take(prefixCount).ToArray();
        var details = AgentSessionContinuitySummaryBuilder.BuildDeterministic(null, prefix);
        var anchor = prefix[^1];
        return new AgentSessionContextCheckpointSaveRequest(
            snapshot.SessionId,
            snapshot.TranscriptEpoch,
            snapshot.ActiveContextCheckpointId,
            snapshot.ActiveContextGeneration,
            snapshot.SourceRun,
            snapshot.SourceRunEpoch,
            prefix[0].TurnId,
            anchor.TurnId,
            prefix.Length,
            anchor.CreatedAtUtc,
            anchor.ContentRevision,
            AgentSessionContextCheckpointKind.Deterministic,
            AgentSessionContinuitySummaryBuilder.RenderSummary(details, prefix.Length, anchor),
            AgentSessionContinuitySummaryBuilder.SerializeDetails(details),
            AgentSessionContinuitySummaryBuilder.GeneratorVersion);
    }

    private static AgentContinuityModelRefinement CreateRefinement()
        => new(
            new AgentContinuitySummaryDocument(
                ["Refined goal"],
                [],
                [],
                [],
                [],
                ["Continue"],
                []),
            "utility-provider",
            "utility-model");

    private static string ReadActiveCheckpointKind(ContinuityFixture fixture)
        => ReadActiveCheckpointColumn(fixture, "CheckpointKind");

    private static string ReadActiveCheckpointColumn(ContinuityFixture fixture, string column)
        => ReadScalarString(
            fixture.Store.DatabasePath,
            $"SELECT checkpoint.{column} FROM AgentSessions session INNER JOIN AgentSessionContextCheckpoints checkpoint ON checkpoint.ContextCheckpointId = session.ActiveContextCheckpointId WHERE session.SessionId = $sessionId;",
            fixture.SessionId);

    private static long ReadActiveCheckpointInt64(ContinuityFixture fixture, string column)
    {
        using var connection = OpenDatabase(fixture.Store.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT checkpoint.{column} FROM AgentSessions session INNER JOIN AgentSessionContextCheckpoints checkpoint ON checkpoint.ContextCheckpointId = session.ActiveContextCheckpointId WHERE session.SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", fixture.SessionId.ToString());
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ReadScalarString(string databasePath, string sql, Guid sessionId)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static long CountRows(string databasePath, string table, Guid sessionId)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long CountTurnItems(string databasePath, Guid sessionId)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM AgentTurnItems WHERE TurnId IN (SELECT TurnId FROM AgentTurns WHERE SessionId = $sessionId);";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long CountCheckpointsByKind(string databasePath, Guid sessionId, bool legacy)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = legacy
            ? "SELECT COUNT(*) FROM AgentSessionContextCheckpoints WHERE SessionId = $sessionId AND CheckpointKind = 'Legacy';"
            : "SELECT COUNT(*) FROM AgentSessionContextCheckpoints WHERE SessionId = $sessionId AND CheckpointKind <> 'Legacy';";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void SetTurnCreatedAt(string databasePath, Guid turnId, DateTimeOffset createdAtUtc)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AgentTurns SET CreatedAtUtc = $createdAtUtc WHERE TurnId = $turnId;";
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$turnId", turnId.ToString());
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenDatabase(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class ContinuityFixture : IDisposable
    {
        private readonly RegressionTestPackageScope _scope;
        private readonly IAgentSessionContinuityModelRefiner? _refiner;
        private AgentDurableRunRecord? _sourceRun;

        private ContinuityFixture(
            RegressionTestPackageScope scope,
            IAgentSessionContinuityModelRefiner? refiner)
        {
            _scope = scope;
            _refiner = refiner;
            Context = scope.Context;
            Store = new AgentLocalStore(Context);
            SessionService = new AgentSessionService(Store);
            SessionId = Store.CreateSession("Continuity test", workspaceId: "workspace.test").SessionId;
        }

        public Sunder.Sdk.Abstractions.IPackageContext Context { get; }

        public AgentLocalStore Store { get; }

        public AgentSessionService SessionService { get; }

        public Guid SessionId { get; }

        public Guid ActiveUserTurnId { get; private set; }

        public static ContinuityFixture Create(IAgentSessionContinuityModelRefiner? refiner = null)
            => new(RegressionTestPackageScope.Create(), refiner);

        public static ContinuityFixture CreateStartedWithHistory(
            int count,
            IAgentSessionContinuityModelRefiner? refiner = null)
        {
            var fixture = Create(refiner);
            fixture.AppendAlternatingHistory(count);
            fixture.StartRun();
            return fixture;
        }

        public void AppendAlternatingHistory(int count)
        {
            var existing = Store.ListTurns(SessionId).Count;
            for (var index = 0; index < count; index++)
            {
                Store.AppendTextTurn(
                    SessionId,
                    (existing + index) % 2 == 0 ? AgentMessageRole.User : AgentMessageRole.Assistant,
                    $"history-{existing + index:000}");
            }
        }

        public void StartRun()
        {
            var reserved = Store.ReserveRun(SessionId, "profile.test", "active request");
            var started = Store.TryStartRun(
                reserved.Key,
                reserved.Epoch,
                "active request",
                [],
                rollbackAnchorTurnId: null,
                "running")!;
            _sourceRun = started.Transition.Run;
            ActiveUserTurnId = started.UserTurn.TurnId;
        }

        public AgentSessionContinuitySnapshot ReadSnapshot()
            => Store.ReadSessionContinuitySnapshot(
                SessionId,
                _sourceRun!.Key.RunId,
                _sourceRun.Key.RunRevision)!;

        public Task<AgentSessionPromptProjection> BuildProjectionAsync(
            AgentProviderRunCapabilities? capabilities = null,
            int promptOverheadTokens = 0,
            CancellationToken cancellationToken = default)
        {
            var service = new AgentSessionContextProjectionService(SessionService, _refiner);
            return service.BuildProjectionAsync(
                SessionId,
                ActiveUserTurnId,
                capabilities ?? DefaultCapabilities,
                CreateProfile(),
                _sourceRun!.Key.RunId,
                _sourceRun.Key.RunRevision,
                promptOverheadTokens,
                cancellationToken);
        }

        public void Dispose() => _scope.Dispose();

        private static AgentProfileRecord CreateProfile()
        {
            var now = DateTimeOffset.UtcNow;
            return new AgentProfileRecord(
                "profile.test",
                "Test profile",
                null,
                null,
                null,
                null,
                null,
                null,
                now,
                now);
        }
    }

    private sealed class ThrowingRefiner(Exception exception) : IAgentSessionContinuityModelRefiner
    {
        public Task<AgentContinuityModelRefinement?> RefineAsync(
            AgentProfileRecord profile,
            string? priorSummary,
            AgentContinuitySummaryDocument deterministicSummary,
            IReadOnlyList<AgentTurnRecord> newlyCoveredTurns,
            CancellationToken cancellationToken)
            => Task.FromException<AgentContinuityModelRefinement?>(exception);
    }

    private sealed class StaticRefiner(AgentContinuityModelRefinement refinement) : IAgentSessionContinuityModelRefiner
    {
        public Task<AgentContinuityModelRefinement?> RefineAsync(
            AgentProfileRecord profile,
            string? priorSummary,
            AgentContinuitySummaryDocument deterministicSummary,
            IReadOnlyList<AgentTurnRecord> newlyCoveredTurns,
            CancellationToken cancellationToken)
            => Task.FromResult<AgentContinuityModelRefinement?>(refinement);
    }

    private sealed class CancelingRefiner(CancellationTokenSource cancellation)
        : IAgentSessionContinuityModelRefiner
    {
        public Task<AgentContinuityModelRefinement?> RefineAsync(
            AgentProfileRecord profile,
            string? priorSummary,
            AgentContinuitySummaryDocument deterministicSummary,
            IReadOnlyList<AgentTurnRecord> newlyCoveredTurns,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The canceled token should have thrown.");
        }
    }

    private sealed class CallbackRefiner(Func<AgentContinuityModelRefinement?> callback)
        : IAgentSessionContinuityModelRefiner
    {
        public Task<AgentContinuityModelRefinement?> RefineAsync(
            AgentProfileRecord profile,
            string? priorSummary,
            AgentContinuitySummaryDocument deterministicSummary,
            IReadOnlyList<AgentTurnRecord> newlyCoveredTurns,
            CancellationToken cancellationToken)
            => Task.FromResult(callback());
    }

    private sealed class BlockingRefiner : IAgentSessionContinuityModelRefiner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentContinuityModelRefinement?> RefineAsync(
            AgentProfileRecord profile,
            string? priorSummary,
            AgentContinuitySummaryDocument deterministicSummary,
            IReadOnlyList<AgentTurnRecord> newlyCoveredTurns,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return null;
        }
    }
}

internal static class ContinuityTestListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> items, T value)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(items[index], value))
            {
                return index;
            }
        }
        return -1;
    }
}
