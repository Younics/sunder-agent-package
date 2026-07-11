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
    private const int CurrentDocumentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await packageContext.Storage.State.GetValueAsync(ProjectsKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<BuilderProjectRecord[]>(json, JsonOptions) ?? [];
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("version", out var versionElement)
            || !versionElement.TryGetInt32(out var version))
        {
            throw new JsonException("Builder project state is not a recognized document.");
        }

        if (version != CurrentDocumentVersion)
        {
            throw new InvalidOperationException($"Builder project state version {version} is not supported by this package version.");
        }

        var persisted = JsonSerializer.Deserialize<BuilderProjectDocument>(json, JsonOptions)
                        ?? throw new JsonException("Builder project state could not be read.");
        return persisted.Projects ?? throw new JsonException("Builder project state does not contain a projects collection.");
    }

    public async Task SaveAsync(IReadOnlyList<BuilderProjectRecord> projects, CancellationToken cancellationToken = default)
        => await packageContext.Storage.State.SetValueAsync(
            ProjectsKey,
            JsonSerializer.Serialize(
                new BuilderProjectDocument(
                    CurrentDocumentVersion,
                    projects.OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray()),
                JsonOptions),
            cancellationToken);

    private sealed record BuilderProjectDocument(int Version, IReadOnlyList<BuilderProjectRecord>? Projects);
}
