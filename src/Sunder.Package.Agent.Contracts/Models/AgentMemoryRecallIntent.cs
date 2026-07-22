namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Classifies why the current turn may require durable-memory retrieval.
/// </summary>
/// <remarks>The intent guides candidate selection and ranking; it does not itself authorize recalled content.</remarks>
public enum AgentMemoryRecallIntent
{
    /// <summary>Disables recall for the current turn.</summary>
    None = 0,

    /// <summary>Looks for context needed to continue or resume earlier work.</summary>
    Continuity = 1,

    /// <summary>Looks for user-authored style or behavior preferences.</summary>
    Preference = 2,

    /// <summary>Looks for previously recorded standing constraints or instructions, which still require trust evaluation.</summary>
    StandingInstruction = 3,

    /// <summary>Looks for facts about the active project, repository, architecture, or dependencies.</summary>
    ProjectFact = 4,

    /// <summary>Looks for facts about the execution environment, machine, paths, or services.</summary>
    EnvironmentFact = 5,

    /// <summary>Looks for facts about a conversation participant.</summary>
    ParticipantFact = 6,

    /// <summary>Looks for prior decisions, explanations, or rationale.</summary>
    Rationale = 7,

    /// <summary>Looks for remembered facts that do not fit a more specific intent.</summary>
    GeneralFact = 8,
}
