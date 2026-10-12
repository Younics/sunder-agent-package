using System.Text.Json;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Skills.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class SkillProjectionContent
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    internal static SunderContent Create(SkillProjection projection)
        => SunderContent.FromBytes(JsonSerializer.SerializeToUtf8Bytes(projection, Json), "application/json", "skills-projection.json");

    internal static async ValueTask<SkillProjection> ReadAsync(ValueTask<SunderContent> result, CancellationToken token)
    {
        await using var content = await result.ConfigureAwait(false);
        if (!string.Equals(content.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Skills runtime returned invalid projection content.");
        await using var stream = await content.OpenReadAsync(token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<SkillProjection>(stream, Json, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Skills runtime returned an empty response.");
    }
}
