using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Execution.Docker;

public static class DockerExecutionConfiguration
{
    public const string TimeoutKey = "docker.timeoutSeconds.default";
    public const string DefaultTimeoutSeconds = "300";

    public static PackageConfigurationSchema Schema { get; } = new(
        "sunder.package.agent.execution.docker",
        "Sunder Agent Execution Docker",
        "Configure Docker images and defaults for Docker-backed execution workspaces.",
        [
            new PackageConfigurationSection(
                "docker",
                "Docker Execution",
                "Docker image management is available in the package settings view.",
                [
                    new PackageConfigurationField(
                        TimeoutKey,
                        "Default shell timeout",
                        PackageConfigurationFieldKind.Text,
                        Description: "Default timeout in seconds for Docker shell commands.",
                        DefaultValue: DefaultTimeoutSeconds,
                        Placeholder: DefaultTimeoutSeconds),
                    new PackageConfigurationField(
                        DockerCli.ExecutablePathConfigurationKey,
                        "Docker CLI path",
                        PackageConfigurationFieldKind.Text,
                        Description: "Optional path to the Docker CLI. Leave empty to auto-detect Docker Desktop, Homebrew, or system Docker installs.",
                        Placeholder: "Auto-detect")
                ])
        ]);
}
