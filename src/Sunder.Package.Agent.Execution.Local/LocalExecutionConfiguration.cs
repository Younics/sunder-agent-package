using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Execution.Local;

public static class LocalExecutionConfiguration
{
    public const string TimeoutKey = "shell.timeoutSeconds.default";
    public const string DefaultTimeoutSeconds = "300";

    public static PackageConfigurationSchema Schema { get; } = new(
        "sunder.package.agent.execution.local",
        "Sunder Agent Execution Local",
        "Configure local-machine execution defaults. Workspaces define paths before tools can use this target.",
        [
            new PackageConfigurationSection(
                "shell",
                "Shell",
                "Defaults used when a workspace runs commands on the local machine.",
                [
                    new PackageConfigurationField(
                        TimeoutKey,
                        "Default shell timeout",
                        PackageConfigurationFieldKind.Text,
                        Description: "Default timeout in seconds for local shell commands when the request does not specify one.",
                        DefaultValue: DefaultTimeoutSeconds,
                        Placeholder: DefaultTimeoutSeconds),
                ])
        ]);
}
