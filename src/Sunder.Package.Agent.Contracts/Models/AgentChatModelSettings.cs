namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentChatModelSettings(
    string? ReasoningVariantId = null,
    string? SpeedOptionId = null,
    string? ModeOptionId = null);
