using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public interface IBuilderProjectStore
{
    Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<BuilderProjectRecord> projects, CancellationToken cancellationToken = default);
}

public sealed class BuilderProjectStore(IPackageContext packageContext) : IBuilderProjectStore
{
    private const string ProjectsKey = "builder.projects.v1";
    private const int CurrentDocumentVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(CancellationToken cancellationToken = default)
        => Read(await packageContext.Storage.State.GetValueAsync(ProjectsKey, cancellationToken));

    private static IReadOnlyList<BuilderProjectRecord> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var value) || !value.TryGetInt32(out var version))
            throw new JsonException("Sunder Package Maker project state is not a recognized document.");
        if (version != CurrentDocumentVersion)
            throw new InvalidOperationException($"Sunder Package Maker project state version {version} is not supported by this package version.");
        if (!root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array)
            throw new JsonException("Sunder Package Maker project state does not contain a projects collection.");

        var result = projects.Deserialize<BuilderProjectRecord[]>(JsonOptions) ?? throw new JsonException("Project state is empty.");
        if (result.Any(row => row is null || string.IsNullOrWhiteSpace(row.Id) || row.DisplayName is null
                || row.PackageId is null || row.ProjectFolder is null || row.Kind is { } kind && !Enum.IsDefined(kind))
            || result.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new JsonException("Sunder Package Maker bookmarks are invalid or contain duplicate row identities.");
        return result;
    }

    public async Task SaveAsync(IReadOnlyList<BuilderProjectRecord> projects, CancellationToken cancellationToken = default)
    {
        // Refuse to overwrite corruption or a newer document even if a caller skipped/failed loading it.
        _ = Read(await packageContext.Storage.State.GetValueAsync(ProjectsKey, cancellationToken));
        var json = JsonSerializer.Serialize(new BuilderProjectDocument(CurrentDocumentVersion, projects.ToArray()), JsonOptions);
        _ = Read(json);
        await packageContext.Storage.State.SetValueAsync(ProjectsKey, json, cancellationToken);
    }

    private sealed record BuilderProjectDocument(int Version, IReadOnlyList<BuilderProjectRecord> Projects);
}
