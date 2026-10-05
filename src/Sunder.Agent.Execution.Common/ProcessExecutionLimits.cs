namespace Sunder.Agent.Execution.Common;

/// <summary>Bounds shared by process request validation and target-owned execution.</summary>
public static class ProcessExecutionLimits
{
    public const int MaximumTimeoutSeconds = 86_400;
    public const int MaximumOutputLength = 10 * 1024 * 1024;
}
