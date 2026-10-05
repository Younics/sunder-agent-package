namespace Sunder.Package.Agent.Execution.Local;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public static class LocalExecutionPresentationDefaults
{
    public const string TimeoutKey = "shell.timeoutSeconds.default";
    public const string DefaultTimeoutSeconds = "300";
    public const int MaximumTimeoutSeconds = 86_400;

    public static bool TryParseTimeout(string? value, out int timeoutSeconds)
        => int.TryParse(value, out timeoutSeconds)
           && timeoutSeconds is >= 1 and <= MaximumTimeoutSeconds;
}
