using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderDevelopmentService
{
    private readonly Dictionary<string, (string EntryPoint, string ViewId)> _viewSelections = new(StringComparer.Ordinal);

    public async ValueTask<SunderContent> InspectViewAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        var request = ReadViewRequest(requestJson, requireInput: false);
        var project = await InspectAsync(request.Project, cancellationToken).ConfigureAwait(false);
        var snapshot = await operations.InspectViewAsync(project, request.ViewId, cancellationToken).ConfigureAwait(false);
        if (snapshot.PackageId != project.PackageId || snapshot.ViewId != request.ViewId
            || !Guid.TryParseExact(snapshot.SnapshotId, "N", out _) || snapshot.Elements.Count > 128)
            throw new InvalidDataException("View inspection returned a different development package or view.");
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_stopping || _lifetime.IsCancellationRequested, this);
            while (_viewSelections.Count >= 8) _viewSelections.Remove(_viewSelections.First().Key);
            _viewSelections.Add(snapshot.SnapshotId, (project.EntryPoint, request.ViewId));
        }
        return BuilderContent.Create(snapshot);
    }

    private void RequireViewSelection(BuilderProjectInspection project, BuilderViewRequest request)
    {
        if (!_viewSelections.TryGetValue(request.Input!.SnapshotId, out var selected)
            || !BuilderPathService.PathComparer.Equals(selected.EntryPoint, project.EntryPoint) || selected.ViewId != request.ViewId)
            throw Error("maker.development.invalid-request", "Inspect this exact package view before requesting element-scoped input. Old references never follow replacement views.");
    }

    private static BuilderViewRequest ReadViewRequest(string json, bool requireInput)
    {
        BuilderContent.ValidateRequest(json);
        try
        {
            var request = JsonSerializer.Deserialize<BuilderViewRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 5 });
            if (request is null || request.Project is null || string.IsNullOrWhiteSpace(request.ViewId) || request.ViewId.Length > 256
                || requireInput != (request.Input is not null)) throw new JsonException();
            // Reuse the same physical-path and bounded workspace argument contract;
            // nesting a view request must not bypass project authorization.
            _ = ReadRequest(JsonSerializer.Serialize(request.Project, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            if (request.Input is { } input && (!Guid.TryParseExact(input.OperationId, "N", out _)
                || !Guid.TryParseExact(input.SnapshotId, "N", out _) || !Guid.TryParseExact(input.ElementReference, "N", out _)
                || input.Action is not ("invoke" or "set-text" or "toggle" or "scroll-up" or "scroll-down")
                || (input.Action == "set-text" ? input.Text is null || input.Text.Length > 4096 : input.Text is not null))) throw new JsonException();
            return request;
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        { throw Error("maker.development.invalid-request", "Select one bounded package view and an inspected opaque element; arbitrary scripts, selectors and desktop input are not accepted."); }
    }

    private sealed record BuilderViewRequest(BuilderOperationRequest Project, string ViewId, PackageDevelopmentViewInput? Input);
}
