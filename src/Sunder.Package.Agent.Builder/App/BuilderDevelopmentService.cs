using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Generated.Packages.Younics_Sunder_Package_Maker_Services;

namespace Sunder.Package.Agent.Builder;

/// <summary>The bounded, owner-only Worker-to-App bridge. It does not depend on the Maker page or its view model.</summary>
[SunderPackageCodeRole(SunderPackageCodeRole.App)]
public sealed partial class BuilderDevelopmentService(BuilderPackageOperations operations, BuilderProjectApplicationService application,
    IServiceProviders<IBuilderRuntimeService> runtimes) : IPackageAppActivationService, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _disposal;
    private bool _stopping;

    public async ValueTask<SunderContent> GetInfoAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        var request = ReadRequest(requestJson);
        var project = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
        var session = operations.GetSession(project.EntryPoint);
        return BuilderContent.Create(new { project = new { project.PackageId, project.DisplayName, project.Kind, project.ProjectFolder,
            project.EntryPoint, project.SourceRevision }, development = session?.Status,
            reloadPending = session?.PendingHotreload is not null, pendingOperationId = session?.PendingHotreload?.Id });
    }

    public async ValueTask<SunderContent> GetLogsAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        using var json = JsonDocument.Parse(requestJson);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("project", out var source) || !root.TryGetProperty("query", out var selection))
            throw Error("maker.development.invalid-request", "A scoped project and bounded log query are required.");
        var query = selection.Deserialize<PackageDevelopmentLogQuery>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw Error("maker.development.invalid-request", "A log query is required.");
        query.Validate();
        var project = await InspectAsync(ReadRequest(source.GetRawText()), cancellationToken).ConfigureAwait(false);
        var logs = await operations.GetLogsAsync(project, query, cancellationToken).ConfigureAwait(false);
        return BuilderContent.Create(logs);
    }

    public async ValueTask<SunderContent> StartOperationAsync(string operationId, string operation, string requestJson, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(operationId, "N", out _) || operation is not ("build" or "hotreload" or "interact-view"))
            throw Error("maker.development.invalid-request", "A correlated finite package operation is required.");
        var view = operation == "interact-view" ? ReadViewRequest(requestJson, requireInput: true) : null;
        if (view is not null && view.Input!.OperationId != operationId)
            throw Error("maker.development.invalid-request", "The view input must use its original operation identity.");
        var request = view?.Project ?? ReadRequest(requestJson);
        if (string.IsNullOrWhiteSpace(request.ExpectedSourceRevision))
            throw Error("maker.development.invalid-request", "A mutation requires the exact source revision selected during authorized preflight.");
        // Repeated admission resolves the exact recorded operation before touching
        // mutable source again. The same id can never name different inputs.
        lock (_sync)
        {
            if (_operations.TryGetValue(operationId, out var existing))
            {
                RequireSame(existing, operation, requestJson);
                return BuilderContent.Create(Render(existing.Snapshot));
            }
        }
        var project = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopping || _lifetime.IsCancellationRequested, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_operations.TryGetValue(operationId, out var existing))
            {
                RequireSame(existing, operation, requestJson);
                return BuilderContent.Create(Render(existing.Snapshot));
            }
            if (_operations.Count >= 64)
            {
                // Forgetting a completed receipt would let the same delayed RPC
                // admission execute again. Capacity exhaustion is not replay authority.
                throw Error("maker.development.unavailable", "Maker has reached its bounded operation capacity; retained mutation receipts cannot be evicted.");
            }
            if (view is not null) RequireViewSelection(project, view);
            var accepted = new Operation(operationId, operation, requestJson, project) { ViewInput = view?.Input };
            _operations.Add(operationId, accepted);
            // Admission is a short RPC. Accepted work belongs to the App activation,
            // not the requesting RPC's ambient lineage, deadline, or caller token.
            if (ExecutionContext.IsFlowSuppressed()) accepted.Work = Task.Run(() => RunAsync(accepted));
            else using (ExecutionContext.SuppressFlow()) accepted.Work = Task.Run(() => RunAsync(accepted));
            return BuilderContent.Create(Render(accepted.Snapshot));
        }
    }

    public ValueTask<SunderContent> GetOperationAsync(string operationId, string requestJson, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_toolsetOperations.TryGetValue(operationId, out var toolset)) return ObserveToolsetOperationAsync(toolset, requestJson, cancellationToken);
            if (!_operations.TryGetValue(operationId, out var operation))
                throw Error("maker.development.operation-not-found", "This App activation has no record of that operation. Do not replay its mutation against a replacement activation.");
            if (operation.RequestJson != requestJson) throw Error("maker.development.invalid-request", "The operation belongs to different authorized source inputs.");
            return ValueTask.FromResult(BuilderContent.Create(Render(operation.Snapshot)));
        }
    }

    private async Task RunAsync(Operation operation)
    {
        try
        {
            Task Update(BuilderJobSnapshot job)
            {
                lock (_sync) operation.Snapshot = operation.Snapshot with { Job = job };
                return Task.CompletedTask;
            }
            if (operation.Kind == "interact-view")
            {
                var result = await operations.InteractViewAsync(operation.Project, operation.ViewInput!, _lifetime.Token).ConfigureAwait(false);
                lock (_sync) operation.Snapshot = operation.Snapshot with { State = result.State, OutcomeUnknown = result.OutcomeUnknown, ViewInput = result };
            }
            else if (operation.Kind == "build")
            {
                var result = await operations.RunJobAsync(new() { Id = operation.Id, Operation = "build", Kind = operation.Project.Kind,
                    EntryPoint = operation.Project.EntryPoint, ProjectFolder = operation.Project.ProjectFolder, PackageId = operation.Project.PackageId,
                    DisplayName = operation.Project.DisplayName, Expected = operation.Project }, Update, _lifetime.Token).ConfigureAwait(false);
                lock (_sync) operation.Snapshot = operation.Snapshot with { State = result.State, Job = result, Error = result.Error };
            }
            else
            {
                var revision = await operations.ReloadBuiltOutputAsync(operation.Project, operation.Id, _lifetime.Token).ConfigureAwait(false);
                lock (_sync) operation.Snapshot = operation.Snapshot with { State = "succeeded", AppliedRevision = revision };
            }
        }
        catch (OperationCanceledException error) when (error.CancellationToken == _lifetime.Token && _lifetime.IsCancellationRequested)
        {
            lock (_sync) operation.Snapshot = operation.Snapshot with { State = "cancelled",
                Error = "Maker activation stopped; query recorded cleanup before another mutation.", OutcomeUnknown = IsOutcomeUnknown(operation) };
        }
        catch (Exception error)
        {
            lock (_sync) operation.Snapshot = operation.Snapshot with { State = "failed", Error = BuilderJobText.Safe(error.Message, 4096),
                OutcomeUnknown = IsOutcomeUnknown(operation) };
        }
    }

    private bool IsOutcomeUnknown(Operation operation) => operation.Kind == "interact-view" || application.HasUnconfirmedJob(operation.Project.ProjectFolder)
        || operations.GetSession(operation.Project.EntryPoint)?.PendingHotreload is not null;

    private async Task<BuilderProjectInspection> InspectAsync(BuilderOperationRequest request, CancellationToken token)
    {
        var paths = new BuilderPathService();
        string? entry = null;
        foreach (var root in request.Roots)
        {
            try { entry = paths.ResolveContainedHostPath(request.EntryPoint, root); break; }
            catch (InvalidOperationException) { }
        }
        if (entry is null) throw Error("maker.development.invalid-request", "The project resolves outside the selected local workspace roots.");
        if (request.ExpectedSourceRevision is not null && !BuilderPathService.PathComparer.Equals(Path.GetFullPath(request.EntryPoint), entry))
            throw Error("maker.development.invalid-request", "The original physical project path was redirected after authorized preflight.");
        var project = await application.InspectAsync(entry, token).ConfigureAwait(false);
        if (request.ExpectedSourceRevision is not null && request.ExpectedSourceRevision != project.SourceRevision)
            throw Error("maker.development.invalid-request", "The project changed after authorized preflight. Inspect the current source before retrying.");
        return project;
    }

    private static BuilderOperationRequest ReadRequest(string json)
    {
        BuilderContent.ValidateRequest(json);
        try
        {
            var request = JsonSerializer.Deserialize<BuilderOperationRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 });
            if (request is null || !Path.IsPathFullyQualified(request.EntryPoint) || request.Roots is not { Count: > 0 and <= 32 }
                || request.Roots.Any(root => string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))) throw new JsonException();
            return request;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw Error("maker.development.invalid-request", "A bounded absolute project entry point and selected workspace roots are required."); }
    }
    private static void RequireSame(Operation operation, string kind, string request)
    {
        if (operation.Kind != kind || operation.RequestJson != request)
            throw Error("maker.development.invalid-request", "An operation id cannot be reused for different inputs.");
    }
    private static BuilderOperationSnapshot Render(BuilderOperationSnapshot snapshot)
        => snapshot.Job is not { } job ? snapshot : snapshot with
        { Job = job with { Output = BuilderJobText.Safe(job.Output, 16 * 1024) }, OutputTruncated = job.Output.Length > 16 * 1024 };
    private static SunderRpcException Error(string code, string message) => new(new(SunderRpcErrorKind.Domain, code, message));
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task[] work;
        lock (_sync)
        {
            if (_disposal is not null) return new(_disposal);
            _stopping = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
            work = _operations.Values.Select(operation => operation.Work).Concat(_toolsetOperations.Values.Select(operation => operation.Work)).Append(_consumer).ToArray();
        }
        _ = StopCoreAsync(work, completion);
        return new(completion.Task);
    }
    private async Task StopCoreAsync(Task[] work, TaskCompletionSource completion)
    {
        List<Exception> failures = [];
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        try { await Task.WhenAll(work).ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        _activationStop.Unregister();
        if (failures.Count == 0) completion.TrySetResult();
        else completion.TrySetException(new AggregateException("Maker App bridge cleanup failed.", failures));
    }
    private sealed class Operation(string id, string kind, string requestJson, BuilderProjectInspection project)
    {
        internal string Id { get; } = id;
        internal string Kind { get; } = kind;
        internal string RequestJson { get; } = requestJson;
        internal BuilderProjectInspection Project { get; } = project;
        internal PackageDevelopmentViewInput? ViewInput { get; init; }
        internal BuilderOperationSnapshot Snapshot { get; set; } = new(id, kind, "running", project, null, null, null);
        internal Task Work { get; set; } = Task.CompletedTask;
    }
    private sealed record BuilderOperationSnapshot(string OperationId, string Operation, string State, BuilderProjectInspection Project,
        BuilderJobSnapshot? Job, PackageDevelopmentRevision? AppliedRevision, string? Error)
    { public bool OutcomeUnknown { get; init; } public bool OutputTruncated { get; init; } public PackageDevelopmentViewInputResult? ViewInput { get; init; } }
    private sealed record BuilderOperationRequest(string EntryPoint, IReadOnlyList<string> Roots, string? ExpectedSourceRevision);
}
