using System.Text;

namespace Sunder.Package.Agent.Builder;

/// <summary>Activation-owned jobs. GetJob renews the caller's liveness lease; disposing drains every driver.</summary>
public sealed class BuilderJobManager : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private readonly Queue<string> completed = new();
    private readonly Dictionary<string, string> retired = new(StringComparer.Ordinal);
    private readonly Queue<string> retiredOrder = new();
    private readonly IBuilderJobDriver driver;
    private readonly BuilderJobOptions options;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task monitor;
    private Task? disposal;
    private bool stopping;

    public BuilderJobManager(IBuilderJobDriver driver, BuilderJobOptions? options = null, TimeProvider? timeProvider = null)
    {
        this.driver = driver;
        this.options = options ?? new();
        clock = timeProvider ?? TimeProvider.System;
        if (this.options.MaximumConcurrentJobs is < 1 or > 16 || this.options.CompletedJobLimit is < 1 or > 64 ||
            this.options.MaximumOutputCharacters is < 1024 or > 128 * 1024 || this.options.OrphanTimeout <= TimeSpan.Zero ||
            this.options.HeartbeatInterval <= TimeSpan.Zero || this.options.HeartbeatInterval > this.options.OrphanTimeout ||
            this.options.FiniteOperationTimeout <= TimeSpan.Zero || this.options.FiniteOperationTimeout > TimeSpan.FromHours(2))
            throw new ArgumentException("Invalid Builder job limits.", nameof(options));
        monitor = Task.Run(MonitorAsync);
    }

    public string Start(BuilderJobRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request = BuilderJobValidation.Normalize(request);
        var fingerprint = BuilderJobValidation.Fingerprint(request);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (jobs.TryGetValue(request.Id!, out var existing))
            {
                if (existing.Fingerprint != fingerprint) throw new ArgumentException("This job ID was already used for a different request.");
                existing.Heartbeat = clock.GetTimestamp();
                return existing.Request.Id!;
            }
            if (retired.ContainsKey(request.Id!)) throw new BuilderJobNotFoundException("This completed job has expired from the bounded cache. Inspect the project before starting a new operation with a new ID.");
            var running = jobs.Values.Where(job => !job.Finished).ToArray();
            if (running.Length >= options.MaximumConcurrentJobs) throw new BuilderJobBusyException("Builder is at its concurrent job limit. Finish or cancel another job, then retry.");
            var project = ProjectKey(request);
            if (project is not null && running.Any(job => ProjectKey(job.Request) is { } other && BuilderJobValidation.Overlaps(project, other)))
                throw new BuilderJobBusyException("A build, watch, setup, or metadata operation already owns this project. Stop it before starting another project mutation.");
            var job = new Job(request, fingerprint, clock.GetTimestamp(), options.MaximumOutputCharacters);
            jobs.Add(request.Id!, job);
            // The RPC cancellation token only controls acceptance. Accepted jobs are governed by
            // CancelJob, their deadline, activation shutdown and the renewable heartbeat lease.
            job.Work = Task.Run(() => RunAsync(job));
            return request.Id!;
        }
    }

    public BuilderJobSnapshot GetJob(string jobId)
    {
        BuilderJobValidation.ValidateId(jobId);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (!jobs.TryGetValue(jobId, out var job)) throw new BuilderJobNotFoundException("Builder job was not found or has expired from the completed-job cache.");
            job.Heartbeat = clock.GetTimestamp();
            return job.Snapshot();
        }
    }

    public ValueTask CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BuilderJobValidation.ValidateId(jobId);
        Job? job;
        lock (sync)
        {
            if (!jobs.TryGetValue(jobId, out job))
            {
                if (retired.ContainsKey(jobId)) return ValueTask.CompletedTask;
                throw new BuilderJobNotFoundException("Builder job was not found.");
            }
            if (job.Finished) return ValueTask.CompletedTask;
            job.CancelReason ??= "Cancelled by the caller.";
        }
        // Cancellation callbacks can kill process trees; do not execute them on the RPC dispatcher.
        _ = job.RequestCancellationAsync();
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync(Job job)
    {
        var state = "succeeded";
        string? failure = null;
        try
        {
            await driver.ExecuteAsync(job.Request, job, job.Cancel.Token).ConfigureAwait(false);
            job.Cancel.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (job.Cancel.IsCancellationRequested)
        { state = "cancelled"; failure = job.CancelReason ?? "The operation was cancelled."; }
        catch (Exception error)
        {
            state = job.Cancel.IsCancellationRequested ? "cancelled" : "failed";
            failure = job.Cancel.IsCancellationRequested ? job.CancelReason ?? "The operation was cancelled." : error.Message;
        }
        finally
        {
            lock (sync)
            {
                job.Complete(state, failure);
                job.Finished = true;
                completed.Enqueue(job.Request.Id!);
                while (completed.Count > options.CompletedJobLimit)
                {
                    var id = completed.Dequeue();
                    if (jobs.Remove(id, out var removed))
                    {
                        retired[id] = removed.Fingerprint;
                        retiredOrder.Enqueue(id);
                        removed.Cancel.Dispose();
                    }
                }
                while (retiredOrder.Count > 256) retired.Remove(retiredOrder.Dequeue());
            }
        }
    }

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(options.HeartbeatInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                Job[] expired;
                lock (sync)
                {
                    var now = clock.GetTimestamp();
                    expired = jobs.Values.Where(job => !job.Finished && !job.Cancel.IsCancellationRequested &&
                        (clock.GetElapsedTime(job.Heartbeat, now) >= options.OrphanTimeout ||
                         job.Request.Operation != "watch" && clock.GetElapsedTime(job.Started, now) >= options.FiniteOperationTimeout)).ToArray();
                    foreach (var job in expired) job.CancelReason ??= clock.GetElapsedTime(job.Heartbeat, now) >= options.OrphanTimeout
                        ? "Cancelled because Builder stopped receiving job heartbeats. Reopen the project and retry."
                        : "Cancelled because the finite operation exceeded its deadline. Inspect the output and retry.";
                }
                foreach (var job in expired) await job.RequestCancellationAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposal is not null) return new(disposal);
            stopping = true;
            var active = jobs.Values.ToArray();
            foreach (var job in active.Where(job => !job.Finished)) job.CancelReason ??= "Cancelled because the Builder activation stopped.";
            disposal = Task.Run(async () =>
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
                foreach (var job in active) await job.RequestCancellationAsync().ConfigureAwait(false);
                await Task.WhenAll(active.Select(job => job.Work).Append(monitor)).ConfigureAwait(false);
                foreach (var job in active) job.Cancel.Dispose();
                lifetime.Dispose();
            });
            return new(disposal);
        }
    }

    private static string? ProjectKey(BuilderJobRequest request) => request.Operation is "create" or "setup" or "build" or "archive" or "watch" or "save-metadata"
        ? request.ProjectFolder : null;

    private sealed class Job(BuilderJobRequest request, string fingerprint, long timestamp, int maximumOutput) : IBuilderJobReporter
    {
        private readonly object sync = new();
        private readonly StringBuilder output = new();
        private BuilderJobSnapshot snapshot = new() { Id = request.Id!, Operation = request.Operation, State = "running", Message = InitialMessage(request), Output = "" };
        internal BuilderJobRequest Request { get; } = request;
        internal string Fingerprint { get; } = fingerprint;
        internal long Started { get; } = timestamp;
        internal long Heartbeat { get; set; } = timestamp;
        internal bool Finished { get; set; }
        internal string? CancelReason { get; set; }
        internal CancellationTokenSource Cancel { get; } = new();
        internal Task Work { get; set; } = Task.CompletedTask;

        internal async Task RequestCancellationAsync()
        {
            try { await Cancel.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (AggregateException error) { Report("\nCancellation callback failed: " + error.Message + "\n"); }
        }

        public void Report(string value)
        {
            lock (sync)
            {
                if (value.Length >= maximumOutput) { output.Clear(); output.Append(value.AsSpan(value.Length - maximumOutput)); }
                else { output.Append(value); if (output.Length > maximumOutput) output.Remove(0, output.Length - maximumOutput); }
            }
        }
        public void Update(BuilderJobUpdate update)
        {
            lock (sync) snapshot = snapshot with
            {
                Phase = update.Phase ?? snapshot.Phase,
                LastSourceChangeAtUtc = update.LastSourceChangeAtUtc ?? snapshot.LastSourceChangeAtUtc,
                LastSuccessfulBuildAtUtc = update.LastSuccessfulBuildAtUtc ?? snapshot.LastSuccessfulBuildAtUtc,
                Message = update.Message ?? snapshot.Message, Project = update.Project ?? snapshot.Project,
                DevelopmentFolder = update.DevelopmentFolder ?? snapshot.DevelopmentFolder,
                Installation = update.Installation ?? snapshot.Installation, ArchivePath = update.ArchivePath ?? snapshot.ArchivePath,
            };
        }
        internal void Complete(string state, string? error)
        {
            error = error is null ? null : BuilderJobText.Safe(error, 8192);
            lock (sync)
            {
                var incomplete = snapshot.Project is not null && Request.Operation is "create" or "setup"
                    ? "Project source is saved; setup is incomplete. Retry setup to restore and build." : null;
                snapshot = snapshot with { State = state, Error = error, Message = state switch
                {
                    "succeeded" => snapshot.Message,
                    "cancelled" => incomplete is null ? "Operation cancelled." : "Operation cancelled. " + incomplete,
                    _ => incomplete ?? "Operation failed.",
                } };
            }
        }
        private static string InitialMessage(BuilderJobRequest request) => request.Operation switch
        {
            "ensure-sdk" => request.Kind == BuilderProjectKind.Dotnet ? "Checking the .NET SDK…" : "Checking Node.js and npm…",
            "repair-sdk" => "Preparing SDK repair…",
            "remove-sdk" => "Removing the managed SDK…",
            "create" => "Preparing to create your project…",
            "setup" => "Preparing the project…",
            "build" => "Preparing to build the project…",
            "archive" => "Preparing a package archive…",
            "watch" => "Starting source watch…",
            "save-metadata" => "Preparing metadata changes…",
            _ => "Starting operation…",
        };
        internal BuilderJobSnapshot Snapshot()
        {
            lock (sync) return snapshot with { Output = BuilderJobText.Safe(output.ToString(), maximumOutput),
                Message = BuilderJobText.Safe(snapshot.Message, 4096), Error = snapshot.Error is null ? null : BuilderJobText.Safe(snapshot.Error, 8192) };
        }
    }
}
