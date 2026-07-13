namespace Sunder.Package.Agent.Provider.Shared;

internal static class ProviderModelId
{
    public static string EnsurePrefix(string modelId, string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var trimmed = modelId.Trim();
        return trimmed.StartsWith(providerId.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{providerId.TrimEnd('/')}/{trimmed}";
    }

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
