using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Execution.Docker;

public static class DockerExecutionConfiguration
{
    public static PackageConfigurationSchema Schema { get; } = new(
        "sunder.package.agent.execution.docker",
        "Sunder Agent Execution Docker",
        "Configure the Docker container used by Docker-backed execution workspaces.",
        [
            new PackageConfigurationSection(
                "docker",
                "Docker Target",
                "The first Docker execution target uses one configured container id or name.",
                [
                    new PackageConfigurationField(
                        "docker.container",
                        "Container id or name",
                        PackageConfigurationFieldKind.Text,
                        Description: "Existing running container used for shell and file operations.",
                        Placeholder: "my-container"),
                    new PackageConfigurationField(
                        "docker.defaultWorkingDirectory",
                        "Default working directory",
                        PackageConfigurationFieldKind.Text,
                        Description: "Working directory inside the container when the workspace does not specify one.",
                        DefaultValue: "/workspace",
                        Placeholder: "/workspace"),
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
