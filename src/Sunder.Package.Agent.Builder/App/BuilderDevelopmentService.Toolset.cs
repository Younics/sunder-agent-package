using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderDevelopmentService
{
    private readonly Dictionary<string, ToolsetOperation> _toolsetOperations = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions ToolsetJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    private async Task<(JsonObject Root, BuilderProjectInspection Project)> ReadToolsetProjectAsync(string json, CancellationToken token)
    {
        BuilderContent.ValidateRequest(json);
        var root = JsonNode.Parse(json, documentOptions: new() { MaxDepth = 6 })?.AsObject() ?? throw new JsonException();
        var source = root["project"] ?? throw new JsonException();
        return (root, await InspectAsync(ReadRequest(source.ToJsonString()), token).ConfigureAwait(false));
    }

    private async ValueTask<SunderContent> QueryPackageAsync(string json, CancellationToken token)
    {
        var (root, project) = await ReadToolsetProjectAsync(json, token).ConfigureAwait(false);
        var session = operations.GetSession(project.EntryPoint);
        var query = root["query"]?.GetValue<string>() ?? "status";
        if (query == "outputs") return BuilderContent.Create(new { outputs = BuilderPackageOperations.BuiltOutputs(project).Select(folder => new { folder, name = project.Kind == BuilderProjectKind.Node ? "node" : new DirectoryInfo(folder).Parent?.Parent?.Name }).ToArray() });
        if (query == "views")
        {
            RequireFeedbackAvailable(project);
            var views = await operations.GetViewsAsync(project, token).ConfigureAwait(false);
            if (session is null || !ReferenceEquals(session, operations.GetSession(project.EntryPoint))) throw Error("maker.development.unavailable", "The view owner changed; query current package views again.");
            return BuilderContent.Create(new { ownerId = session.Identity, views = views.Select(view => new { view.ViewId, view.Name, open = view.IsOpen, capture = view.CanCapture }).ToArray() });
        }
        var status = session is { HasHandle: true, ReleaseStarted: false } ? await session.Handle.GetStatusAsync(token).ConfigureAwait(false) : null;
        var diagnostic = status?.ErrorMessage is { } message ? status.HostState != PackageDevelopmentHostState.Ready ? "Development Host is not ready."
            : !status.IsLoaded ? "This development package is not loaded." : BuilderJobText.Safe(message, 512) : null;
        if (query == "runtime") return BuilderContent.Create(new { runtime = status?.RuntimeReadiness.ToString().ToLowerInvariant() ?? "notloaded", error = diagnostic });
        string? pendingId;
        lock (_sync) pendingId = _toolsetOperations.Values.FirstOrDefault(operation => operation.Project.EntryPoint == project.EntryPoint
            && (operation.Result["state"]?.GetValue<string>() == "running" || operation.Result["outcomeUnknown"]?.GetValue<bool>() == true))?.Id;
        return BuilderContent.Create(new { name = project.DisplayName, id = project.PackageId,
            loaded = status?.IsLoaded ?? false, runtime = status?.RuntimeReadiness.ToString().ToLowerInvariant() ?? "notloaded",
            app = status?.AppReadiness.ToString().ToLowerInvariant() ?? "unavailable",
            pendingOperationId = pendingId ?? session?.PendingHotreload?.Id, phase = session?.ReloadProgress?.Phase,
            operationError = session?.ReloadProgress?.Error, error = diagnostic });
    }

    private async ValueTask<SunderContent> QueryViewAsync(string json, CancellationToken token)
    {
        var (root, project) = await ReadToolsetProjectAsync(json, token).ConfigureAwait(false);
        RequireFeedbackAvailable(project);
        var view = root["viewId"]!.GetValue<string>();
        var query = root["query"]!.Deserialize<PackageDevelopmentViewQuery>(ToolsetJson)!;
        query.Validate();
        var result = await operations.QueryViewAsync(project, view, query, token, RequireOwner(root)).ConfigureAwait(false);
        lock (_sync)
        {
            while (!_viewSelections.ContainsKey(result.Snapshot.SnapshotId) && _viewSelections.Count >= 8) _viewSelections.Remove(_viewSelections.First().Key);
            _viewSelections[result.Snapshot.SnapshotId] = (project.EntryPoint, view);
        }
        return BuilderContent.Create(new { snapshot = result.Snapshot, partial = result.Partial, nextCursor = result.NextCursor });
    }

    private async ValueTask<SunderContent> ScreenshotAsync(string json, CancellationToken token)
    {
        var (root, project) = await ReadToolsetProjectAsync(json, token).ConfigureAwait(false);
        RequireFeedbackAvailable(project);
        var query = root["query"]!.Deserialize<PackageDevelopmentViewQuery>(ToolsetJson)!;
        var edge = root["detail"]?.GetValue<string>() == "high" ? 2048 : 1280;
        await using var content = await operations.CaptureViewAsync(project, root["viewId"]!.GetValue<string>(), query, edge, token, RequireOwner(root)).ConfigureAwait(false);
        if (content.MediaType != "image/png" || content.Length is not { } length || length is < 33 or > 2 * 1024 * 1024)
            throw new InvalidDataException("Invalid bounded package screenshot.");
        await using var stream = await content.OpenReadAsync(token).ConfigureAwait(false);
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0
            || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != content.Sha256)
            throw new InvalidDataException("Package image length/hash mismatch.");
        return BuilderContent.Create(new { type = "sunder.tool-image.v1", mediaType = "image/png", sha256 = content.Sha256, bytes });
    }

    private async ValueTask<SunderContent> StartToolsetOperationAsync(string id, string kind, string json, CancellationToken token)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new JsonException();
        lock (_sync)
        {
            if (_toolsetOperations.TryGetValue(id, out var prior))
            {
                if (prior.Kind != kind || prior.Request != json) throw Error("maker.development.invalid-request", "Operation identity belongs to different inputs.");
                return BuilderContent.Create(prior.Result);
            }
        }
        var (root, project) = await ReadToolsetProjectAsync(json, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(root["project"]?["expectedSourceRevision"]?.GetValue<string>())) throw Error("maker.development.invalid-request", "A mutation requires its original authorized source revision.");
        RequireFeedbackAvailable(project);
        var selected = kind == "apply" ? await operations.SelectApplyAsync(project, root["output"]?.GetValue<string>(), token).ConfigureAwait(false) : null;
        if (kind != "apply")
        {
            if (operations.GetSession(project.EntryPoint)?.Identity != RequireOwner(root)) throw Error("maker.development.unavailable", "The selected development owner was replaced. Query package views again.");
            var viewId = root["viewId"]!.GetValue<string>();
            if (kind == "open-view")
            {
                if (!(await operations.GetViewsAsync(project, token).ConfigureAwait(false)).Any(view => view.ViewId == viewId))
                    throw Error("maker.development.invalid-request", "Choose a view returned by the package views query.");
            }
            else
            {
                var input = root["input"]!.Deserialize<PackageDevelopmentViewInput>(ToolsetJson)!;
                if (input.OperationId != id) throw new JsonException();
                lock (_sync) RequireViewSelection(project, new(ReadRequest(root["project"]!.ToJsonString()), viewId, input));
            }
        }
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_toolsetOperations.TryGetValue(id, out var prior))
            {
                if (prior.Kind != kind || prior.Request != json) throw new JsonException();
                return BuilderContent.Create(prior.Result);
            }
            if (_toolsetOperations.Values.Any(existing => existing.Project.EntryPoint == project.EntryPoint
                && (existing.Result["state"]?.GetValue<string>() == "running" || existing.Result["outcomeUnknown"]?.GetValue<bool>() == true)))
                throw Error("maker.busy", "Observe this project's pending operation before another mutation.");
            if (_toolsetOperations.Count + _operations.Count >= 64) throw Error("maker.development.unavailable", "Resolve existing Maker operations; retained receipts cannot be evicted.");
            var operation = new ToolsetOperation(id, kind, json, project, root, selected);
            _toolsetOperations.Add(id, operation);
            if (ExecutionContext.IsFlowSuppressed()) operation.Work = Task.Run(() => RunToolsetAsync(operation));
            else using (ExecutionContext.SuppressFlow()) operation.Work = Task.Run(() => RunToolsetAsync(operation));
            return BuilderContent.Create(operation.Result);
        }
    }

    private async Task RunToolsetAsync(ToolsetOperation operation)
    {
        var result = new JsonObject();
        try
        {
            if (operation.Selection is { } selected)
            {
                var applied = await operations.ApplyBuiltAsync(selected, operation.Id, _lifetime.Token).ConfigureAwait(false);
                result["state"] = "succeeded"; result["effect"] = applied.Effect;
                result["phase"] = selected.AppliedOwner?.ReloadProgress?.Phase;
            }
            else if (operation.Kind == "open-view")
            {
                await operations.OpenViewAsync(operation.Project, operation.Root["viewId"]!.GetValue<string>(), _lifetime.Token, RequireOwner(operation.Root)).ConfigureAwait(false);
                result["state"] = "opened";
            }
            else
            {
                var input = operation.Root["input"]!.Deserialize<PackageDevelopmentViewInput>(ToolsetJson)!;
                var dispatch = await operations.InputViewAsync(operation.Project, input, RequireOwner(operation.Root), _lifetime.Token).ConfigureAwait(false);
                result["state"] = dispatch.State; result["outcomeUnknown"] = dispatch.OutcomeUnknown;
            }
            if (operation.Root["verify"]?.GetValue<string>() is { } text && result["outcomeUnknown"]?.GetValue<bool>() != true)
            {
                try
                {
                    var page = await operations.QueryViewAsync(operation.Project, operation.Root["viewId"]!.GetValue<string>(), new() { Text = text, Limit = 1 }, _lifetime.Token, RequireOwner(operation.Root)).ConfigureAwait(false);
                    result["verification"] = page.Snapshot.Elements.Count > 0 ? "matched" : page.Partial ? "incomplete" : "not-observed";
                }
                catch (Exception error) when (error is InvalidOperationException or IOException or NotSupportedException)
                { result["verification"] = "unavailable"; }
            }
        }
        catch (Exception error)
        {
            result["state"] = _lifetime.IsCancellationRequested ? "cancelled" : "failed";
            result["error"] = BuilderJobText.Safe(error.Message, 512);
            result["outcomeUnknown"] = operation.Kind == "input-view" && error is not InvalidOperationException
                || operation.Selection is { } selected && selected.AppliedOwner?.ReloadProgress?.State != "failed" && (selected.InitialLoadAttempted || error is TimeoutException
                    || operations.GetSession(operation.Project.EntryPoint)?.PendingHotreload is not null);
            result["phase"] = operation.Selection?.AppliedOwner?.ReloadProgress?.Phase;
        }
        lock (_sync) operation.Result = result;
    }

    private async ValueTask<SunderContent> ObserveToolsetOperationAsync(ToolsetOperation operation, string request, CancellationToken token)
    {
        if (operation.Request != request) throw Error("maker.development.invalid-request", "Operation belongs to different authorized inputs.");
        JsonObject result; lock (_sync) result = operation.Result.DeepClone().AsObject();
        if (operation.Selection?.AppliedOwner?.ReloadProgress is { } progress && progress.OperationId == operation.Id)
            result["phase"] = progress.Phase;
        if (result["outcomeUnknown"]?.GetValue<bool>() == true && operation.Selection is { } selected)
        {
            try
            {
                if (await operations.ObserveApplyAsync(selected, operation.Id, token).ConfigureAwait(false) is not null)
                {
                    result = new() { ["state"] = "succeeded", ["effect"] = selected.Owner is null ? "loaded" : "reloaded", ["phase"] = "applied" };
                    lock (_sync) operation.Result = result;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
            {
                // Keep the original operation failure; an unavailable observation
                // is separate evidence and cannot overwrite it or replay the action.
                result["observationError"] = BuilderJobText.Safe(error.Message, 512);
            }
        }
        return BuilderContent.Create(result);
    }

    private sealed class ToolsetOperation(string id, string kind, string request, BuilderProjectInspection project, JsonObject root, BuilderApplySelection? selection)
    {
        internal string Id { get; } = id;
        internal string Kind { get; } = kind;
        internal string Request { get; } = request;
        internal BuilderProjectInspection Project { get; } = project;
        internal JsonObject Root { get; } = root;
        internal BuilderApplySelection? Selection { get; } = selection;
        internal JsonObject Result { get; set; } = new() { ["state"] = "running" };
        internal Task Work { get; set; } = Task.CompletedTask;
    }

    private static string RequireOwner(JsonObject root)
        => root["ownerId"]?.GetValue<string>() is { } owner && Guid.TryParseExact(owner, "N", out _) ? owner
            : throw Error("maker.development.invalid-request", "Select an original package view reference.");

    private void RequireFeedbackAvailable(BuilderProjectInspection project)
    {
        lock (_sync)
            if (_toolsetOperations.Values.Any(operation => operation.Project.EntryPoint == project.EntryPoint
                && operation.Result["state"]?.GetValue<string>() == "running"))
                throw Error("maker.busy", "Observe the pending package operation before querying its view or requesting another mutation.");
    }
}
