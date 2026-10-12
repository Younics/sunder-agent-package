namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Declares how a supplementary prompt-context block may be used by the model.</summary>
public enum AgentPromptContextUsage
{
    /// <summary>Reference data that does not independently direct model behavior.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("reference")] Reference = 0,

    /// <summary>Untrusted instructions that may guide work only inside their explicitly declared directory subtree.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("scoped-instruction")] ScopedInstruction = 1,

    /// <summary>User-authored standing instructions selected through the host's profile workflow.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("standing-instruction")] StandingInstruction = 2,
}
