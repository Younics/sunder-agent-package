using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

/// <summary>The original accepted callback's authority, passed explicitly across queue and polling boundaries.</summary>
public sealed class BuilderJobScope
{
    private readonly BuilderJobJournal _owner;
    private int _active = 1;
    internal BuilderJobScope(BuilderJobJournal owner, PackageAppStateUpdateContext context)
    { _owner = owner; Context = context; }
    public PackageAppStateUpdateContext Context { get; }
    internal void Close() => Interlocked.Exchange(ref _active, 0);
    internal void RequireActive(BuilderJobJournal owner)
    {
        if (!ReferenceEquals(_owner, owner) || Volatile.Read(ref _active) == 0)
            throw new InvalidOperationException("The original Builder job's accepted journal lifetime has ended or belongs to another journal.");
        Context.CancellationToken.ThrowIfCancellationRequested();
    }
}

public sealed partial class BuilderJobJournal
{
    /// <summary>Owns the original job's journal work through settlement and exact stop acknowledgement.</summary>
    public async Task<BuilderJobSnapshot> RunOwnedAsync(
        Func<BuilderJobScope, Task<BuilderJobSnapshot>> run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();
        BuilderJobSnapshot? result = null;
        // Register BEFORE dispatch, queue waits, or async storage. Core requests
        // settlement before service disposal and waits for this ACTUAL callback.
        // No workspace lock is held while polling or waiting for job cancellation.
        await _updates.RunAsync(UpdateKey, async context =>
        {
            var scope = new BuilderJobScope(this, context);
            try { result = await run(scope).ConfigureAwait(false); }
            finally { scope.Close(); }
        }).ConfigureAwait(false); // Caller/settlement cancel the job, not its accepted cleanup/persistence authority.
        return result ?? throw new InvalidDataException("Builder journal lifetime completed without a job result.");
    }

    private Task RunAcceptedAsync(Func<PackageAppStateUpdateContext, Task> update, CancellationToken token = default,
        BuilderJobScope? scope = null)
    {
        token.ThrowIfCancellationRequested();
        if (scope is null) return _updates.RunAsync(UpdateKey, update, token);
        // Reuse only the still-running ORIGINAL accepted callback's authority.
        // Settlement is not revocation, but escaped/late work cannot regain rights.
        scope.RequireActive(this);
        return update(scope.Context);
    }
}
