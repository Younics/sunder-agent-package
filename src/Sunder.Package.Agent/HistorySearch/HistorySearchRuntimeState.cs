using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed class HistorySearchRuntimeState
{
    private readonly HistorySearchStore _store;
    private readonly object _lock = new();
    private HistorySearchStatus _status;
    private long _revision;

    internal HistorySearchRuntimeState(
        HistorySearchStore store,
        AgentRuntimeChangeHub runtimeChanges)
    {
        _store = store;
        var snapshot = store.GetSnapshot();
        var configuration = store.GetConfiguration();
        _status = new HistorySearchStatus(
            0,
            store.IsAvailable ? HistorySearchAvailability.Starting : HistorySearchAvailability.Unavailable,
            LexicalEnabled: store.IsAvailable,
            configuration.SemanticEnabled,
            snapshot.SemanticReady,
            configuration.EmbeddingProviderPackageId,
            configuration.EmbeddingProviderId,
            configuration.EmbeddingModelId,
            configuration.Revision,
            ProjectionRevision: 0,
            snapshot.ActiveTextGenerationId,
            snapshot.ActiveEmbeddingGenerationId,
            snapshot.DocumentCount,
            snapshot.EmbeddingCount,
            PendingChanges: 0,
            ProgressCompleted: 0,
            ProgressTotal: null,
            snapshot.LastReconciledAtUtc,
            store.FailureCode,
            store.IsAvailable ? null : "History search is temporarily unavailable.",
            runtimeChanges.InstanceId);
    }

    internal event Action<HistorySearchStatus>? Changed;

    internal HistorySearchStatus Current
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    internal HistorySearchStatus Publish(Func<HistorySearchStatus, HistorySearchStatus> update)
        => PublishCore(update, projectionChanged: false);

    internal HistorySearchStatus PublishProjectionChanged(
        Func<HistorySearchStatus, HistorySearchStatus> update)
        => PublishCore(update, projectionChanged: true);

    private HistorySearchStatus PublishCore(
        Func<HistorySearchStatus, HistorySearchStatus> update,
        bool projectionChanged)
    {
        HistorySearchStatus next;
        var changed = false;
        lock (_lock)
        {
            var snapshot = _store.GetSnapshot();
            var configuration = _store.GetConfiguration();
            next = update(_status) with
            {
                Revision = _status.Revision,
                ProjectionRevision = projectionChanged
                    ? checked(_status.ProjectionRevision + 1)
                    : _status.ProjectionRevision,
                TextGeneration = snapshot.ActiveTextGenerationId,
                EmbeddingGeneration = snapshot.ActiveEmbeddingGenerationId,
                IndexedDocuments = snapshot.DocumentCount,
                EmbeddedDocuments = snapshot.EmbeddingCount,
                LastReconciledAtUtc = snapshot.LastReconciledAtUtc,
                SemanticEnabled = configuration.SemanticEnabled,
                SemanticReady = snapshot.SemanticReady,
                EmbeddingProviderPackageId = configuration.EmbeddingProviderPackageId,
                EmbeddingProviderId = configuration.EmbeddingProviderId,
                EmbeddingModelId = configuration.EmbeddingModelId,
                SemanticConfigurationRevision = snapshot.ConfigurationRevision,
            };
            if (next == _status)
            {
                return _status;
            }
            next = next with { Revision = ++_revision };
            _status = next;
            changed = true;
        }
        if (changed)
        {
            Changed?.Invoke(next);
        }
        return next;
    }

    internal HistorySearchStatus Refresh()
        => Publish(static status => status);

    internal HistorySearchStatus RefreshProjection()
        => PublishProjectionChanged(static status => status);
}
