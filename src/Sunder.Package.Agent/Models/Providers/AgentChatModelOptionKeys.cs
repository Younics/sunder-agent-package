namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines reserved <c>ChatOptions.AdditionalProperties</c> keys used to pass selected model options to provider adapters.
/// </summary>
/// <remarks>The values associated with these keys are provider-defined option identifiers, never credential values.</remarks>
public static class AgentChatModelOptionKeys
{
    /// <summary>Identifies the additional property whose string value selects an <see cref="AgentModelSpeedOptionDescriptor" />.</summary>
    public const string SpeedOptionId = "sunder.agent.speedOptionId";

    /// <summary>Identifies the additional property whose string value selects an <see cref="AgentModelModeOptionDescriptor" />.</summary>
    public const string ModeOptionId = "sunder.agent.modeOptionId";
}
