using System.Text.Json;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Subagents.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class SubagentProjectionContent
{
    internal static SunderContent Create<T>(T value, string fileName = "subagents-projection.json")
        => SunderContent.FromBytes(JsonSerializer.SerializeToUtf8Bytes(value, SubagentOwnerJson.Options), "application/json", fileName);
    internal static async ValueTask<T> ReadAsync<T>(ValueTask<SunderContent> result, CancellationToken token) where T : class
    {
        await using var content = await result.ConfigureAwait(false);
        if (!string.Equals(content.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Subagents runtime returned invalid document content.");
        await using var stream = await content.OpenReadAsync(token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, SubagentOwnerJson.Options, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Subagents runtime returned an empty document.");
    }
}
