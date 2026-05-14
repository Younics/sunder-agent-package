using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Execution.Docker;

public static class DockerExecutionConfiguration
{
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
                        "docker.timeoutSeconds.default",
                        "Default shell timeout",
                        PackageConfigurationFieldKind.Text,
                        Description: "Default timeout in seconds for Docker shell commands.",
                        DefaultValue: "300",
                        Placeholder: "300")
                ])
        ]);
}
