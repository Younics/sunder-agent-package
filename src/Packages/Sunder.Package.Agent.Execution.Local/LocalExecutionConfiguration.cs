using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Execution.Local;

public static class LocalExecutionConfiguration
{
    public static PackageConfigurationSchema Schema { get; } = new(
        "sunder.package.agent.execution.local",
        "Sunder Agent Execution Local",
        "Configure local-machine execution defaults. Workspaces still define allowed roots before tools can use this target.",
        [
            new PackageConfigurationSection(
                "shell",
                "Shell",
                "Defaults used when a workspace runs commands on the local machine.",
                [
                    new PackageConfigurationField(
                        "shell.timeoutSeconds.default",
                        "Default shell timeout",
                        PackageConfigurationFieldKind.Text,
                        Description: "Default timeout in seconds for local shell commands when the request does not specify one.",
                        DefaultValue: "300",
                        Placeholder: "300"),
                ])
        ]);
}
