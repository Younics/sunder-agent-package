using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public sealed class BuilderProjectStore(IPackageContext packageContext)
{
    private const string ProjectsKey = "builder.projects.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await packageContext.Storage.State.GetValueAsync(ProjectsKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<BuilderProjectRecord[]>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task SaveAsync(IReadOnlyList<BuilderProjectRecord> projects, CancellationToken cancellationToken = default)
        => await packageContext.Storage.State.SetValueAsync(
            ProjectsKey,
            JsonSerializer.Serialize(projects.OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase), JsonOptions),
            cancellationToken);
}
