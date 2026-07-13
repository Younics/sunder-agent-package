using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Provider.Shared;

internal static class ProviderJson
{
    public static bool TryParseObjectArguments(
        string? argumentsJson,
        out IDictionary<string, object?> arguments)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            return true;
        }

        if (!AgentToolArgumentObject.TryParse(argumentsJson, out var parsed, out _))
        {
            arguments = null!;
            return false;
        }

        arguments = new Dictionary<string, object?>(parsed!.ToDictionary(), StringComparer.Ordinal);
        return true;
    }
}
