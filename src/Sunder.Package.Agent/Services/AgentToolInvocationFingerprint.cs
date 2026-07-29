using System.Security.Cryptography;
using System.Text;

namespace Sunder.Package.Agent.Services;

internal static class AgentToolInvocationFingerprint
{
    private const string Version = "agent-tool-invocation-v1";

    public static string Create(string toolId, string? argumentsJson)
    {
        var normalizedToolId = toolId.Trim().ToLowerInvariant();
        var normalizedArguments = AgentPermissionFingerprint.NormalizeJson(argumentsJson);
        var material = string.Concat(
            Version,
            "\n",
            normalizedToolId.Length,
            ":",
            normalizedToolId,
            normalizedArguments.Length,
            ":",
            normalizedArguments);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
