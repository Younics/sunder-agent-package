namespace Sunder.Package.Agent.Mcp;

public sealed record ConfiguredMcpServerRecord
{
    public string ServerId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Description { get; init; }

    public bool IsEnabled { get; init; } = true;

    public ConfiguredMcpTransportType TransportType { get; init; }

    public string[] CommandParts { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public string? EndpointUrl { get; init; }

    public int? TimeoutMilliseconds { get; init; }

    public string[] HeaderNames { get; init; } = [];

    public string[] EnvironmentVariableNames { get; init; } = [];

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }
}
