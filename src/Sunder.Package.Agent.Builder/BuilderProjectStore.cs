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
    // Keep the storage key so an older installation's bookmarks are discovered. The document is versioned.
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
        var version = 1;
        JsonElement projects;
        if (root.ValueKind == JsonValueKind.Array) projects = root;
        else
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var value) || !value.TryGetInt32(out version))
                throw new JsonException("Sunder Package Maker project state is not a recognized document.");
            if (version is not (1 or CurrentDocumentVersion))
                throw new InvalidOperationException($"Sunder Package Maker project state version {version} is not supported by this package version.");
            if (!root.TryGetProperty("projects", out projects) || projects.ValueKind != JsonValueKind.Array)
                throw new JsonException("Sunder Package Maker project state does not contain a projects collection.");
        }

        var result = version == CurrentDocumentVersion
            ? projects.Deserialize<BuilderProjectRecord[]>(JsonOptions) ?? throw new JsonException("Project state is empty.")
            : projects.EnumerateArray().Select(MigrateBookmark).ToArray();
        if (result.Any(row => row is null || string.IsNullOrWhiteSpace(row.Id) || row.DisplayName is null
                || row.PackageId is null || row.ProjectFolder is null || row.Kind is { } kind && !Enum.IsDefined(kind))
            || result.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new JsonException("Sunder Package Maker bookmarks are invalid or contain duplicate row identities.");
        return result;
    }

    private static BuilderProjectRecord MigrateBookmark(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid legacy project bookmark.");
        string Text(string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : string.Empty;
        DateTimeOffset Date(string name) => row.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var date)
            ? date : DateTimeOffset.UnixEpoch;
        // Execution paths belonged to remote targets. Only the saved host path is a local bookmark.
        // Resolution is deferred to source-only backend discovery; unresolved rows remain locatable.
        return new(Text("id"), Text("displayName"), Text("packageId"), Text("projectFolder"), null, null,
            Date("createdAtUtc"), Date("updatedAtUtc"));
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
