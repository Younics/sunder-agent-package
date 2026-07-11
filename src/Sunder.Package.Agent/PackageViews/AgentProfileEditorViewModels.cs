namespace Sunder.Package.Agent.PackageViews;

public enum AgentProfileStatusKind
{
    None = 0,
    Success,
    Warning,
    Error,
}

public sealed record BehaviorLoopOption(
    string LoopId,
    string? SourceId,
    string Label,
    string Description
);
