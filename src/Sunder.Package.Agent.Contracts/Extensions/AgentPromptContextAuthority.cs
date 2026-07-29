namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Declares the effective authority assigned by the Runtime to final user-role prompt context.</summary>
public enum AgentPromptContextAuthority
{
    /// <summary>Reference data that cannot independently direct behavior.</summary>
    Reference = 0,

    /// <summary>User standing instructions selected through a host-controlled profile workflow.</summary>
    StandingInstruction = 1,

    /// <summary>Host-verified instructions limited to one canonical directory subtree.</summary>
    ScopedInstruction = 2,
}
