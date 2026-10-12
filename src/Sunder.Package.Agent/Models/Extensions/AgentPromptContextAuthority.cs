namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Declares the effective authority assigned by the Runtime to final user-role prompt context.</summary>
public enum AgentPromptContextAuthority
{
    /// <summary>Reference data that cannot independently direct behavior.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("reference")] Reference = 0,

    /// <summary>User standing instructions selected through a host-controlled profile workflow.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("standing-instruction")] StandingInstruction = 1,

    /// <summary>Host-verified instructions limited to one canonical directory subtree.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("scoped-instruction")] ScopedInstruction = 2,
}
