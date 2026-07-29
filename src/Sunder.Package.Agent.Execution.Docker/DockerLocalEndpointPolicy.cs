using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Execution.Docker;

internal static class DockerLocalEndpointPolicy
{
    private const int MaximumConfigBytes = 256 * 1024;

    public static string GetIdentity()
    {
        var configuredContext = Environment.GetEnvironmentVariable("DOCKER_CONTEXT");
        var context = string.IsNullOrWhiteSpace(configuredContext)
            ? ReadCurrentContext()
            : configuredContext.Trim();
        if (string.IsNullOrWhiteSpace(context))
        {
            context = "default";
        }
        var host = Environment.GetEnvironmentVariable("DOCKER_HOST")?.Trim() ?? string.Empty;
        return GetIdentity(context, host);
    }

    public static string? GetConfiguredEndpoint()
    {
        ValidateConfiguredContext();
        var host = Environment.GetEnvironmentVariable("DOCKER_HOST")?.Trim() ?? string.Empty;
        return string.IsNullOrEmpty(host) ? null : NormalizeEndpointReportedByDocker(host);
    }

    public static void ValidateConfiguredContext()
    {
        var configuredContext = Environment.GetEnvironmentVariable("DOCKER_CONTEXT");
        var context = string.IsNullOrWhiteSpace(configuredContext)
            ? ReadCurrentContext()
            : configuredContext.Trim();
        if (string.IsNullOrWhiteSpace(context))
        {
            context = "default";
        }
        _ = GetIdentity(context, string.Empty);
    }

    internal static string GetIdentity(string context, string host)
    {
        if (!string.Equals(context, "default", StringComparison.Ordinal)
            && !string.Equals(context, "desktop-linux", StringComparison.Ordinal))
        {
            throw new DockerExecutionDomainException(
                "docker.endpoint.context-unsupported",
                "Docker execution requires the default or Docker Desktop local context.");
        }

        if (!string.IsNullOrEmpty(host)
            && !host.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
            && !host.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase))
        {
            throw new DockerExecutionDomainException(
                "docker.endpoint.remote-unsupported",
                "Docker execution requires a local unix or named-pipe daemon endpoint.");
        }
        return ComputeHash(context + "\n" + host);
    }

    public static void ValidateEndpointReportedByDocker(string endpoint)
        => _ = NormalizeEndpointReportedByDocker(endpoint);

    public static string NormalizeEndpointReportedByDocker(string endpoint)
    {
        endpoint = endpoint.Trim();
        if (string.IsNullOrEmpty(endpoint)
            || (!endpoint.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
                && !endpoint.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase)))
        {
            throw new DockerExecutionDomainException(
                "docker.endpoint.invalid",
                "Docker execution requires a local unix or named-pipe daemon endpoint.");
        }
        return endpoint;
    }

    private static string ReadCurrentContext()
    {
        var dockerConfig = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
        var directory = string.IsNullOrWhiteSpace(dockerConfig)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(dockerConfig.Trim()));
        var path = Path.Combine(directory, "config.json");
        if (!File.Exists(path))
        {
            return "default";
        }
        var info = new FileInfo(path);
        if (info.Length > MaximumConfigBytes)
        {
            throw new DockerExecutionDomainException(
                "docker.endpoint.config-too-large",
                "Docker CLI configuration exceeds the secure parsing limit.");
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("currentContext", out var property)
                   && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? "default"
                : "default";
        }
        catch (JsonException ex)
        {
            throw new DockerExecutionDomainException(
                "docker.endpoint.config-malformed",
                "Docker CLI configuration is not valid JSON.",
                innerException: ex);
        }
    }

    private static string ComputeHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
