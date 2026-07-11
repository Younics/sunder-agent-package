namespace Sunder.Package.Agent.Provider.Shared;

internal static class ProviderModelId
{
    public static string RemovePrefix(string modelId, string providerId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return modelId;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var prefix = providerId.TrimEnd('/') + "/";
        return modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[prefix.Length..]
            : modelId;
    }
}
