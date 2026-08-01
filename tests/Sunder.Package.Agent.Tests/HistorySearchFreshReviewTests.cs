using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class HistorySearchFreshReviewTests
{
    [Fact]
    public async Task CompleteStateAndHitSerialization_RedactsEveryDisplayCanary()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope);
        var workspaces = services.GetRequiredService<AgentWorkspaceService>();
        var profiles = services.GetRequiredService<AgentProfileService>();
        var sessions = services.GetRequiredService<AgentSessionService>();
        var workspace = workspaces.CreateWorkspace("Useful Workspace password=workspace-label-canary");
        var profile = await profiles.CreateProfileAsync("Useful Profile token: profile-label-canary");
        var session = sessions.CreateSession(
            "Useful Session client_secret='session-label-canary'",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.User,
            "serialized-output-anchor authorization: Basic body-output-canary");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var runtimeState = services.GetRequiredService<HistorySearchRuntimeState>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);
        const string providerPackageId = "useful-package-password=provider-e\u0301";
        const string providerId = "useful-provider-token=provider-e\u0301";
        const string modelId = "useful-model-api_key=model-e\u0301";
        var configuration = projection.ChangeConfiguration(
            semanticEnabled: true,
            providerPackageId,
            providerId,
            modelId,
            spaceFingerprint: new string('f', 64));
        services.GetRequiredService<HistorySemanticOperationFence>().Activate(configuration.Revision);
        runtimeState.Publish(status => status with
        {
            FailureCode = "useful-failure-code",
            FailureMessage = "Useful failure secret=status-message-canary",
        });

        var response = await search.SearchAsync(new HistorySearchRequest("serialized-output-anchor"));
        var state = await search.GetStateAsync(new HistorySearchStateRequest(
            IncludeAdvancedFilters: true,
            Limit: HistorySearchLimits.MaximumStateOptions));
        var hit = Assert.Single(response.Results);
        var serialized = JsonSerializer.Serialize(new { Hit = hit, State = state });
        var canaries = new[]
        {
            "workspace-label-canary",
            "profile-label-canary",
            "session-label-canary",
            "body-output-canary",
            "status-message-canary",
        };

        Assert.All(canaries, canary => Assert.DoesNotContain(canary, serialized, StringComparison.Ordinal));
        Assert.Contains("Useful Workspace", hit.WorkspaceName, StringComparison.Ordinal);
        Assert.Contains("Useful Session", hit.SessionTitle, StringComparison.Ordinal);
        Assert.Contains(state.Workspaces, option => option.DisplayName.Contains("Useful Workspace", StringComparison.Ordinal));
        Assert.Contains(state.Profiles, option => option.DisplayName.Contains("Useful Profile", StringComparison.Ordinal));
        Assert.Contains(state.Sessions, option => option.DisplayName.Contains("Useful Session", StringComparison.Ordinal));
        Assert.Contains("Useful failure", state.Status.FailureMessage, StringComparison.Ordinal);
        Assert.Equal(providerPackageId, state.Status.EmbeddingProviderPackageId);
        Assert.Equal(providerId, state.Status.EmbeddingProviderId);
        Assert.Equal(modelId, state.Status.EmbeddingModelId);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task ReplacementOverflow_ReconcilesMoreThan4096PostScanMutationsLosslessly()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope);
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Overflow");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var createdSessions = Enumerable.Range(0, 102)
            .Select(index => sessions.CreateSession($"Session {index:D3}", workspaceId: workspace.WorkspaceId))
            .OrderBy(static session => session.SessionId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        var updateTurn = sessions.AppendTextTurn(
            createdSessions[0].SessionId,
            AgentMessageRole.User,
            "overflowoldmarker");
        sessions.AppendTextTurn(
            createdSessions[1].SessionId,
            AgentMessageRole.User,
            "overflowdeletekeep");
        var rollbackAnchor = sessions.AppendTextTurn(
            createdSessions[1].SessionId,
            AgentMessageRole.User,
            "overflowdeleteanchor");
        sessions.AppendTextTurn(
            createdSessions[1].SessionId,
            AgentMessageRole.Assistant,
            "overflowstaledelete");
        foreach (var session in createdSessions.Skip(2))
        {
            sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, $"baseline {session.SessionId:N}");
        }

        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var runtimeState = services.GetRequiredService<HistorySearchRuntimeState>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(
            () => projection.GetSnapshot().DocumentCount == 104,
            TimeSpan.FromMinutes(2));
        var oldGeneration = projection.GetSnapshot().ActiveTextGenerationId;
        var mutationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? mutationFailure = null;
        var mutationStarted = 0;
        void MutateAfterTwoScanned(HistorySearchStatus status)
        {
            if (status.Availability != HistorySearchAvailability.Rebuilding
                || status.ProgressCompleted != 2
                || Interlocked.CompareExchange(ref mutationStarted, 1, 0) != 0)
            {
                return;
            }
            try
            {
                for (var index = 0; index < 4_097; index++)
                {
                    sessions.AppendTextTurn(
                        createdSessions[0].SessionId,
                        AgentMessageRole.Assistant,
                        $"overflowunique{index:D4}");
                }
                sessions.UpdateTextTurn(updateTurn.TurnId, "overflownewmarker");
                sessions.RollbackTranscript(createdSessions[1].SessionId, rollbackAnchor.TurnId);
            }
            catch (Exception exception)
            {
                mutationFailure = exception;
            }
            finally
            {
                mutationCompleted.TrySetResult();
            }
        }

        runtimeState.Changed += MutateAfterTwoScanned;
        indexer.RequestRebuild();
        await mutationCompleted.Task.WaitAsync(TimeSpan.FromMinutes(10));
        runtimeState.Changed -= MutateAfterTwoScanned;
        if (mutationFailure is not null)
        {
            throw mutationFailure;
        }

        const int expectedDocuments = 4_199;
        await WaitUntilAsync(
            () => projection.GetSnapshot().ActiveTextGenerationId != oldGeneration
                  && projection.GetSnapshot().DocumentCount == expectedDocuments
                  && runtimeState.Current.PendingChanges == 0,
            TimeSpan.FromMinutes(10));

        Assert.Single((await search.SearchAsync(new HistorySearchRequest("overflowunique4096"))).Results);
        Assert.Single((await search.SearchAsync(new HistorySearchRequest("overflownewmarker"))).Results);
        Assert.Empty((await search.SearchAsync(new HistorySearchRequest("overflowoldmarker"))).Results);
        Assert.Single((await search.SearchAsync(new HistorySearchRequest("overflowdeletekeep"))).Results);
        Assert.Empty((await search.SearchAsync(new HistorySearchRequest("overflowdeleteanchor"))).Results);
        Assert.Empty((await search.SearchAsync(new HistorySearchRequest("overflowstaledelete"))).Results);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task WorkspaceRename_ReindexesStoredHitLabelsImmediately()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope);
        var workspaces = services.GetRequiredService<AgentWorkspaceService>();
        var workspace = workspaces.CreateWorkspace("Workspace Before");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Rename", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "workspace-rename-anchor");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);
        Assert.Equal(
            "Workspace Before",
            Assert.Single((await search.SearchAsync(new HistorySearchRequest("workspace-rename-anchor"))).Results).WorkspaceName);

        workspaces.SaveWorkspace(workspace.WorkspaceId, "Workspace After", description: null);
        var renamed = await WaitForHitAsync(
            search,
            "workspace-rename-anchor",
            static hit => hit.WorkspaceName == "Workspace After");

        Assert.Equal("Workspace After", renamed.WorkspaceName);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task AdvancedStateContinuation_UsesInsertionHighWaterMarkWhenLateSessionHasOlderTimestamp()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope);
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Snapshot");
        var sessions = services.GetRequiredService<AgentSessionService>();
        for (var index = 0; index < 25; index++)
        {
            sessions.CreateSession($"Snapshot {index:D2}", workspaceId: workspace.WorkspaceId);
        }
        var search = services.GetRequiredService<HistorySearchService>();
        var first = await search.GetStateAsync(new HistorySearchStateRequest(
            Limit: 10,
            IncludeAdvancedFilters: true));
        Assert.NotNull(first.Continuation);
        var late = sessions.CreateSession("Late session", workspaceId: workspace.WorkspaceId);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                   $"Data Source={services.GetRequiredService<Sunder.Package.Agent.Storage.AgentLocalStore>().DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE AgentSessions
                SET CreatedAtUtc = (SELECT MIN(CreatedAtUtc) FROM AgentSessions)
                WHERE SessionId = $sessionId;
                """;
            command.Parameters.AddWithValue("$sessionId", late.SessionId.ToString("D"));
            command.ExecuteNonQuery();
        }

        var snapshottedIds = await ReadAllStateSessionIdsAsync(search, first, limit: 10);
        var fresh = await search.GetStateAsync(new HistorySearchStateRequest(
            Limit: 10,
            IncludeAdvancedFilters: true));
        var freshIds = await ReadAllStateSessionIdsAsync(search, fresh, limit: 10);

        Assert.DoesNotContain(late.SessionId.ToString("D"), snapshottedIds);
        Assert.Contains(late.SessionId.ToString("D"), freshIds);
    }

    private static ServiceProvider CreateRuntime(RegressionTestPackageScope scope)
    {
        var services = new ServiceCollection();
        services.AddSingleton(scope.Context);
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton<Sunder.Package.Agent.Protocol.AgentRpcCatalog>(new RegressionTestExtensionCatalog());
        services.AddSingleton<IBackgroundProcessQueue, CompositionBackgroundProcessQueue>();
        new PackageModule().ConfigureRuntimeServices(services, scope.Context);
        return services.BuildServiceProvider();
    }

    private static async Task<IReadOnlySet<string>> ReadAllStateSessionIdsAsync(
        HistorySearchService search,
        HistorySearchState first,
        int limit)
    {
        var ids = first.Sessions.Select(static option => option.Id).ToHashSet(StringComparer.Ordinal);
        var continuation = first.Continuation;
        var pages = 1;
        while (continuation is not null)
        {
            Assert.True(pages++ < HistorySearchLimits.MaximumAdvancedFilterPages);
            var page = await search.GetStateAsync(new HistorySearchStateRequest(
                Continuation: continuation,
                Limit: limit,
                IncludeAdvancedFilters: true));
            ids.UnionWith(page.Sessions.Select(static option => option.Id));
            continuation = page.Continuation;
        }
        return ids;
    }

    private static async Task<HistorySearchHit> WaitForHitAsync(
        HistorySearchService search,
        string query,
        Func<HistorySearchHit, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (true)
        {
            var hit = (await search.SearchAsync(new HistorySearchRequest(query))).Results.SingleOrDefault();
            if (hit is not null && predicate(hit))
            {
                return hit;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for a refreshed History Search hit.");
            }
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for History Search state.");
            }
            await Task.Delay(20);
        }
    }
}
