using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Execution.Local;

public static class LocalExecutionConfiguration
{
    public const string TimeoutKey = "shell.timeoutSeconds.default";
    public const string DefaultTimeoutSeconds = "300";

    internal static async ValueTask<int> ResolveDefaultTimeoutSecondsAsync(
        IPackageContext packageContext,
        CancellationToken cancellationToken = default)
        => BoundedValue.ParseInt32(
            await packageContext.Settings.GetValueAsync(TimeoutKey, cancellationToken),
            int.Parse(DefaultTimeoutSeconds, System.Globalization.CultureInfo.InvariantCulture),
            minimum: 1,
            maximum: BoundedProcessRunner.MaximumTimeoutSeconds);

    public static PackageSettingsSchema Schema { get; } = new(
        "Configure local-machine execution defaults. Workspaces define paths before tools can use this target.",
        [
            new PackageSettingsSection(
                "shell",
                "Shell",
                "Defaults used when a workspace runs commands on the local machine.",
                [
                    new PackageSettingsField(
                        TimeoutKey,
                        "Default shell timeout",
                        PackageSettingsFieldKind.Text,
                        description: "Default timeout in seconds for local shell commands when the request does not specify one.",
                        defaultValue: DefaultTimeoutSeconds,
                        placeholder: DefaultTimeoutSeconds),
                ])
        ]);
}
