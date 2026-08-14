namespace Sunder.Package.Agent.Services;

public sealed record AgentExecutionTargetWarmupResult(
    AgentExecutionTargetWarmupStatus Status,
    string Message)
{
    public static AgentExecutionTargetWarmupResult Ready(string message) => new(AgentExecutionTargetWarmupStatus.Ready, message);

    public static AgentExecutionTargetWarmupResult Skipped(string message) => new(AgentExecutionTargetWarmupStatus.Skipped, message);

    public static AgentExecutionTargetWarmupResult Failed(string message) => new(AgentExecutionTargetWarmupStatus.Failed, message);
}

public enum AgentExecutionTargetWarmupStatus
{
    Ready = 0,
    Skipped = 1,
    Failed = 2,
}
