namespace Sunder.Package.Agent.Services;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentExecutionTargetWarmupResult(
    AgentExecutionTargetWarmupStatus Status,
    string Message)
{
    public static AgentExecutionTargetWarmupResult Ready(string message) => new(AgentExecutionTargetWarmupStatus.Ready, message);

    public static AgentExecutionTargetWarmupResult Skipped(string message) => new(AgentExecutionTargetWarmupStatus.Skipped, message);

    public static AgentExecutionTargetWarmupResult Failed(string message) => new(AgentExecutionTargetWarmupStatus.Failed, message);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentExecutionTargetWarmupStatus
{
    Ready = 0,
    Skipped = 1,
    Failed = 2,
}
