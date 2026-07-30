namespace Sunder.Package.Agent.Execution.Docker;

internal sealed class DockerImageCatalogCoordinator
{
    private readonly Dictionary<string, long> _committedStatusGenerations = new(StringComparer.OrdinalIgnoreCase);
    private long _nextStatusRevision;
    private long _allStatusesInvalidatedAt;

    internal SemaphoreSlim MutationGate { get; } = new(1, 1);

    internal SemaphoreSlim MigrationGate { get; } = new(1, 1);

    internal long BeginStatus(string imageReference)
        => checked(++_nextStatusRevision);

    internal bool CanCommitStatus(string imageReference, long generation)
        => generation >= GetAuthoritativeGeneration(imageReference);

    internal void CommitStatus(string imageReference, long generation)
    {
        if (!CanCommitStatus(imageReference, generation))
        {
            throw new InvalidOperationException("A stale Docker image status cannot become authoritative.");
        }
        _committedStatusGenerations[imageReference] = generation;
    }

    internal void InvalidateStatus(string imageReference)
        => _committedStatusGenerations[imageReference] = checked(++_nextStatusRevision);

    internal void InvalidateAllStatuses()
    {
        _allStatusesInvalidatedAt = checked(++_nextStatusRevision);
        _committedStatusGenerations.Clear();
    }

    private long GetAuthoritativeGeneration(string imageReference)
        => _committedStatusGenerations.TryGetValue(imageReference, out var committed)
            ? Math.Max(_allStatusesInvalidatedAt, committed)
            : _allStatusesInvalidatedAt;
}

internal sealed record DockerImageStatusOperation(
    string ImageReference,
    long Revision,
    DockerImageDefinition Image);
