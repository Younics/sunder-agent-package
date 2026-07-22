using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Execution.Docker;

public static class DockerExecutionConfiguration
{
    public const string TimeoutKey = "docker.timeoutSeconds.default";
    public const string DefaultTimeoutSeconds = "300";

    public static PackageSettingsSchema Schema { get; } = new(
        "Configure pinned Docker images and defaults for resource-bounded container execution. Docker is not a complete security sandbox.",
        [
            new PackageSettingsSection(
                "docker",
                "Docker Execution",
                "Docker image management is available in the package settings view. Runtime containers use no-new-privileges, drop all capabilities, enforce CPU/memory/PID limits, and have networking disabled.",
                [
                    new PackageSettingsField(
                        TimeoutKey,
                        "Default shell timeout",
                        PackageSettingsFieldKind.Text,
                        description: "Default timeout in seconds for Docker shell commands.",
                        defaultValue: DefaultTimeoutSeconds,
                        placeholder: DefaultTimeoutSeconds),
                    new PackageSettingsField(
                        DockerCli.ExecutablePathConfigurationKey,
                        "Docker CLI path",
                        PackageSettingsFieldKind.Text,
                        description: "Optional path to the Docker CLI. Leave empty to auto-detect Docker Desktop, Homebrew, or system Docker installs.",
                        placeholder: "Auto-detect")
                ])
        ]);
}
