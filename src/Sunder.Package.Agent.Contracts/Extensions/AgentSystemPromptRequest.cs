namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Provides the Runtime snapshot available to a trusted system-prompt contributor for one run.
/// </summary>
/// <remarks>
/// <para>
/// This record and its referenced collections are read-only call snapshots owned by the Runtime. A
/// contributor must not mutate them or retain references after contribution work completes. The same
/// contributor can receive independent request instances concurrently.
/// </para>
/// <para>
/// Availability in this trusted-code request does not confer trusted-content provenance. Profile text,
/// the current user message, transcript turns, tool metadata, workspace data, and provider/model strings
/// can be user-, model-, tool-, package-, or environment-derived. They must not be copied or interpolated
/// into privileged instructions. Contributors must not disclose them, include credentials, broaden tool or
/// workspace scope, or use them to bypass permission checks.
/// </para>
/// </remarks>
/// <param name="Session">
/// The current persisted session snapshot. User-controlled titles and other display values remain untrusted.
/// </param>
/// <param name="Profile">
/// The active profile snapshot. Its free-form description and instructions can be user-authored and must
/// not be elevated by a package contributor.
/// </param>
/// <param name="ProviderId">
/// The stable identifier of the selected chat provider. It is routing metadata, not proof of package identity.
/// </param>
/// <param name="ModelId">
/// The selected provider-defined model identifier. Contributors must tolerate unknown future values.
/// </param>
/// <param name="RunCapabilities">
/// The provider capabilities resolved for this run; use them to tailor package-owned policy, not to weaken
/// safety or permission boundaries.
/// </param>
/// <param name="Workspace">
/// The selected workspace snapshot, or <see langword="null"/> when the session has no workspace.
/// Workspace names and paths are data rather than trusted instructions.
/// </param>
/// <param name="ExecutionBinding">
/// The selected workspace execution binding, or <see langword="null"/> when none is available. Its presence
/// does not itself authorize file, process, or network access.
/// </param>
/// <param name="AvailableTools">
/// The ordered, read-only tool descriptor snapshot advertised for the run. Descriptor text is package data,
/// while actual execution remains subject to readiness, assignment, and permission checks.
/// </param>
/// <param name="Turns">
/// The bounded transcript window selected for the request. Each record retains its role provenance and all
/// transcript content remains non-privileged.
/// </param>
/// <param name="RunId">The unique identity of the current run.</param>
/// <param name="RunRevision">
/// The durable revision of the current run, used with <paramref name="RunId"/> to distinguish retries and
/// superseded execution.
/// </param>
/// <param name="RunStartedAtUtc">The UTC timestamp at which this run revision started.</param>
/// <param name="UserMessage">
/// The current untrusted user-role message. It is supplied for contextual decisions only and must never be
/// copied, summarized, or interpolated into privileged system instructions.
/// </param>
public sealed record AgentSystemPromptRequest(
    AgentSessionRecord Session,
    AgentProfileRecord Profile,
    string ProviderId,
    string ModelId,
    AgentProviderRunCapabilities RunCapabilities,
    AgentWorkspaceRecord? Workspace,
    AgentWorkspaceBindingRecord? ExecutionBinding,
    IReadOnlyList<AgentToolDescriptor> AvailableTools,
    IReadOnlyList<AgentTurnRecord> Turns,
    Guid RunId,
    long RunRevision,
    DateTimeOffset RunStartedAtUtc,
    string UserMessage);
