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

    public ValueTask<SunderContent> GetOperationAsync(string operationId, string requestJson, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_toolsetOperations.TryGetValue(operationId, out var operation))
                throw Error("maker.development.operation-not-found", "This App activation has no record of that operation. Do not replay its mutation against a replacement activation.");
            return ObserveToolsetOperationAsync(operation, requestJson, cancellationToken);
        }
    }

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
            work = _toolsetOperations.Values.Select(operation => operation.Work).Append(_consumer).ToArray();
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
    private sealed record BuilderOperationRequest(string EntryPoint, IReadOnlyList<string> Roots, string? ExpectedSourceRevision);
}
