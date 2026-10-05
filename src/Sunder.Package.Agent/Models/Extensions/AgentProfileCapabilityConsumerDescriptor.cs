namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes one profile-level model capability consumed by an installed feature.
/// </summary>
/// <remarks>
/// This is presentation and dependency metadata returned by
/// <see cref="Contracts.IAgentProfileCapabilityConsumer"/>. It neither selects a provider nor grants access to
/// provider credentials. Instances are call snapshots; strings should be stable where identified and must not
/// contain secrets or user content.
/// </remarks>
/// <param name="CapabilityKind">
/// The stable capability-kind identifier matched ordinally without regard to case, such as a model capability
/// kind defined by the Agent contracts.
/// </param>
/// <param name="DisplayName">The non-sensitive human-readable capability name shown in profile UI.</param>
/// <param name="Description">
/// A concise explanation of why the feature consumes the capability. This is display text, not policy or
/// authorization logic.
/// </param>
public sealed record AgentProfileCapabilityConsumerDescriptor(
    string CapabilityKind,
    string DisplayName,
    string Description);
