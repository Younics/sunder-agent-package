using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Contracts;

/// <summary>
/// Defines the typed package extension points published and consumed by the Sunder Agent package family.
/// </summary>
/// <remarks>
/// <para>
/// App and Runtime extension catalogs are separate. A contribution registered in one role is not visible in the
/// other; a dual-role extension must register a role-appropriate instance in each catalog. Catalog queries return
/// zero or more host-owned activation-scoped instances in deterministic host registration order and do not
/// implicitly deduplicate logical identities.
/// </para>
/// <para>
/// Consumers must not dispose contribution instances or cache them past catalog removal. Unless an extension point
/// states a stronger guarantee, implementations can be called concurrently, receive no thread affinity, and must
/// honor cancellation exposed by their contract. Owning-package provenance is available through
/// <see cref="IPackageExtensionCatalog.GetExtensionContributions{TContract}(PackageExtensionPoint{TContract})"/>;
/// self-declared ids and display text are not proof of ownership or authorization.
/// </para>
/// </remarks>
public static class PackageExtensionPoints
{
    /// <summary>
    /// Identifies Runtime chat-provider contributions consumed to discover models and create chat clients.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: active Runtime packages contribute providers; the Agent Runtime consumes them. Cardinality is zero or more, with one provider selected for a run.</para>
    /// <para>Identity, ordering, and deduplication: <c>Descriptor.ProviderId</c> is matched ordinally without regard to case. Provider lists are presented by display name, but run resolution uses the first catalog match. Registrations are not deduplicated, so provider ids must be unique across active packages.</para>
    /// <para>Ownership, threading, cancellation, and failure: the host owns activation-scoped instances, which can serve concurrent discovery and runs. Async provider methods must honor their tokens. There is no extension-point-level failure isolation; run-critical failures propagate to the calling operation, although an individual caller may treat optional metadata discovery as unavailable.</para>
    /// <para>Trust, provenance, and security: catalog ownership identifies the provider package; descriptor ids do not. Providers receive sensitive prompts and credentials and can contact external services. They must protect and minimize transmitted data, avoid logging secrets, validate model ids and settings, and treat model responses and tool-call arguments as untrusted.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentChatProvider> ChatProviders =
        new("sunder.package.agent:chat-providers");

    /// <summary>
    /// Identifies Runtime behavior-loop contributions that orchestrate provider and tool cycles.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime packages contribute loops; the Agent Runtime consumes one loop selected by the active profile. Cardinality is zero or more, with the built-in loop used as fallback.</para>
    /// <para>Identity, ordering, and deduplication: the effective identity is the descriptor's source id and loop id, compared ordinally without regard to case. Profile presentation is ordered by display name; resolution uses the first matching catalog entry and does not deduplicate registrations.</para>
    /// <para>Ownership, threading, cancellation, and failure: loops are host-owned for package activation and can run concurrently for different sessions. <see cref="IAgentBehaviorLoop.RunAsync"/> must honor run cancellation. Exceptions are not isolated at the extension point and can fail the run.</para>
    /// <para>Trust, provenance, and security: the catalog supplies package provenance, while descriptor source ids are self-declared. A loop receives a high-authority Runtime facade but must preserve durable run revisions, permission gates, advertised-tool checks, workspace scope, cancellation, and transcript trust boundaries; model, user, and tool content remains untrusted.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentBehaviorLoop> BehaviorLoops =
        new("sunder.package.agent:behavior-loops");

    /// <summary>
    /// Identifies Runtime embedding-provider contributions consumed for model discovery and vector generation.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime packages contribute embedding providers; Agent features consume the provider selected by a profile. Cardinality is zero or more.</para>
    /// <para>Identity, ordering, and deduplication: <c>Descriptor.ProviderId</c> is compared ordinally without regard to case. Presentation is ordered by display name and resolution uses a matching catalog entry; no registration deduplication is performed, so ids must be globally unique among active providers.</para>
    /// <para>Ownership, threading, cancellation, and failure: activation-scoped instances are host-owned and can receive concurrent batch or single-item requests. Async methods must honor their tokens. There is no general provider-failure isolation; exceptions can fail discovery, indexing, or recall work according to the caller.</para>
    /// <para>Trust, provenance, and security: package ownership is authoritative provenance, not the descriptor id. Input text can contain private transcript, memory, or workspace data; providers must minimize disclosure, protect credentials, enforce service policy and limits, and never treat an embedding or nearest-neighbor result as trusted instruction or authorization.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentEmbeddingProvider> EmbeddingProviders =
        new("sunder.package.agent:embedding-providers");

    /// <summary>
    /// Identifies Runtime declarations of profile-level model capabilities consumed by installed features.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime packages contribute consumer declarations; the Agent Runtime consumes them to decide which profile capability selectors are relevant. Cardinality is zero or more consumers, each declaring zero or more capability kinds.</para>
    /// <para>Identity, ordering, and deduplication: <see cref="IAgentProfileCapabilityConsumer.ConsumerId"/> identifies the feature, and capability-kind strings identify declarations. Neither registrations nor returned descriptors are deduplicated or ordered; current queries use case-insensitive matching and require only one match.</para>
    /// <para>Ownership, threading, cancellation, and failure: host-owned activation instances can be queried repeatedly or concurrently. Discovery is synchronous, has no cancellation channel, and has no per-consumer failure isolation, so it must be fast and free of I/O.</para>
    /// <para>Trust, provenance, and security: owning-package metadata, not self-declared ids, establishes provenance. A declaration is presentation/dependency metadata only; it does not grant credentials, authorize provider use, or make package display text trusted. Returned text must not include secrets.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentProfileCapabilityConsumer> ProfileCapabilityConsumers =
        new("sunder.package.agent:profile-capability-consumers");

    /// <summary>
    /// Identifies Runtime providers of capabilities that users can assign to profiles and subagents.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime packages contribute providers; the Agent Runtime consumes every provider for editing and assignment reconciliation. Cardinality is zero or more providers, each returning zero or more descriptors.</para>
    /// <para>Identity, ordering, and deduplication: provider invocation is by display name using ordinal case-insensitive ordering. Valid descriptors are deduplicated case-insensitively by (kind, source id, capability id), first wins, then ordered by display name. Provider registrations are not deduplicated; <see cref="IAgentProfileSelectableCapabilityProvider.ProviderId"/> should be globally package-scoped and used as source id.</para>
    /// <para>Ownership, threading, cancellation, and failure: host-owned activation instances can be listed concurrently and may emit change notifications. Listing must honor cancellation. Provider exceptions and cancellation propagate and can fail the aggregate query; there is no per-provider isolation.</para>
    /// <para>Trust, provenance, and security: catalog ownership establishes package provenance, while descriptor source and status strings are untrusted metadata. Selection is not readiness or authorization. Providers and consumers must re-resolve capabilities at use time, enforce permissions and scope, and avoid exposing secrets in profile UI.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentProfileSelectableCapabilityProvider> ProfileSelectableCapabilityProviders =
        new("sunder.package.agent:profile-selectable-capability-providers");

    /// <summary>
    /// Identifies the Runtime catalog exported by the base Agent package for dependent Runtime packages to consume.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: the base Agent Runtime contributes its read-only catalog facade; dependent Runtime packages consume it. Normal cardinality is exactly one while Agent is active and zero when unavailable.</para>
    /// <para>Identity, ordering, and deduplication: the contract has no instance id. Registrations are not deduplicated and dependent callers select the first catalog entry, so multiple registrations are unsupported and catalog order must not be used for priority.</para>
    /// <para>Ownership, threading, cancellation, and failure: the base package and host own the activation-scoped instance. Its synchronous reads and events can be used from concurrent Runtime work and expose no cancellation. Exceptions propagate to the consumer; event subscribers manage their own lifetime and thread marshalling.</para>
    /// <para>Trust, provenance, and security: extension-contribution ownership identifies the base package. Returned records represent authoritative local Agent state but can contain sensitive user, transcript, profile, and workspace data. Consumers receive read access only and must minimize retention and disclosure, validate stale identities, and never infer broader mutation authority.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentRuntimeCatalog> RuntimeCatalogs =
        new("sunder.package.agent:runtime-catalogs");

    /// <summary>
    /// Identifies the base Agent Runtime service that resolves a workspace to its active execution target.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: the base Agent Runtime contributes the resolver; dependent Runtime packages such as Builder consume it. Normal cardinality is one while Agent is active and zero otherwise.</para>
    /// <para>Identity, ordering, and deduplication: the contract has no instance identity. Registrations are not deduplicated and consumers use the first catalog entry, making multiple resolvers unsupported.</para>
    /// <para>Ownership, threading, cancellation, and failure: the host owns the activation-scoped resolver, which can be called concurrently. Resolution must honor its token; synchronous workspace listing has no cancellation. Missing workspaces, unavailable targets, cancellation, and resolver exceptions propagate to the calling operation without point-level isolation.</para>
    /// <para>Trust, provenance, and security: owning-package provenance comes from the catalog. A resolution exposes workspace paths and an execution target with file/process authority; consumers must preserve the selected binding, path scope, permission checks, payload limits, and cancellation rather than treating resolution as unrestricted access.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentWorkspaceExecutionResolver> WorkspaceExecutionResolvers =
        new("sunder.package.agent:workspace-execution-resolvers");

    /// <summary>
    /// Identifies the base Agent Runtime executor consumed to create durable child agent runs.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: the base Agent Runtime contributes the executor; subagent Runtime packages consume it. Normal cardinality is one while Agent is active and zero otherwise.</para>
    /// <para>Identity, ordering, and deduplication: the contract has no instance id. Registrations are not deduplicated and consumers select the first catalog entry, so multiple executors are unsupported.</para>
    /// <para>Ownership, threading, cancellation, and failure: the host owns the activation-scoped executor and can run independent child requests concurrently. Execution must honor the supplied token. Exceptions and cancellation propagate into the child-run workflow; no extension-point-level isolation is promised.</para>
    /// <para>Trust, provenance, and security: catalog ownership identifies the base package, but child task text, inherited profile settings, model output, and tool arguments remain untrusted. Consumers and implementations must preserve parent/root identities, durable revisions, workspace scope, capability assignments, permission inheritance, and cancellation.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentChildRunExecutor> ChildRunExecutors =
        new("sunder.package.agent:child-run-executors");

    /// <summary>
    /// Identifies Runtime cleanup contributions for package-owned data associated with deleted Agent sessions.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime packages contribute cleaners; the Agent Runtime consumes every cleaner after deleting a session tree. Cardinality is zero or more cleaners, each invoked once per deleted session id in the cleanup attempt.</para>
    /// <para>Identity, ordering, and deduplication: <see cref="IAgentSessionDataCleaner.CleanerId"/> identifies diagnostics only. Registrations are not deduplicated and cleaners run in catalog order, so cleanup must be idempotent and must not rely on another cleaner's position.</para>
    /// <para>Ownership, threading, cancellation, and failure: cleaners are host-owned activation instances and may see concurrent deletion workflows. Cleanup is synchronous and has no cancellation channel. The Runtime catches each exception, continues other cleaners and sessions, and reports collected failures to the deletion workflow.</para>
    /// <para>Trust, provenance, and security: catalog ownership establishes cleaner provenance. The session id authorizes no broad storage access; a cleaner must delete only data its package owns for that exact session, avoid following links or user-controlled paths without validation, tolerate absent data, and avoid logging retained content or secrets.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentSessionDataCleaner> SessionDataCleaners =
        new("sunder.package.agent:session-data-cleaners");

    /// <summary>
    /// Identifies Runtime contributors of trusted package instructions for the privileged system prompt.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime packages contribute instruction sources; the Agent Runtime consumes all of them for each model request. Cardinality is zero or more contributors returning zero or more blocks.</para>
    /// <para>Identity, ordering, and deduplication: contributors are invoked by display name using ordinal case-insensitive ordering and registrations are not deduplicated by contributor id. Blocks are deduplicated case-insensitively by (source id, block id), selecting required then highest-priority content, and rendered by required status, descending priority, and textual tie-breakers.</para>
    /// <para>Ownership, threading, cancellation, and failure: host-owned activation instances can be invoked concurrently across runs. Contributors must honor cancellation. The Runtime propagates cancellation and suppresses other contributor exceptions so optional instructions cannot fail the base chat flow.</para>
    /// <para>Trust, provenance, and security: package ownership establishes code provenance, but request fields can contain untrusted user, transcript, model, tool, workspace, and remote data. Only package-controlled policy belongs in system blocks. Never elevate request content, disclose secrets, or bypass permission and execution boundaries.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentSystemPromptContributor> SystemPromptContributors =
        new("sunder.package.agent:system-prompt-contributors");

    /// <summary>
    /// Identifies Runtime contributions of individually installed agent tools.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime packages contribute tools; the Agent Runtime wraps and consumes them through its installed-package tool source. Cardinality is zero or more tools.</para>
    /// <para>Identity, ordering, and deduplication: <c>Descriptor.ToolId</c> is matched ordinally without regard to case. Tools are presented by priority and display name, while execution resolves the first matching tool after source ordering. Registrations are not deduplicated, so tool ids must be unique or explicitly source-scoped where the consuming contract supports it.</para>
    /// <para>Ownership, threading, cancellation, and failure: host-owned activation instances can be queried and executed concurrently. Readiness and execution must honor cancellation. The run wrapper converts non-cancellation execution exceptions into tool-error results, but discovery/readiness failures can still fail prompt preparation; cancellation normally propagates.</para>
    /// <para>Trust, provenance, and security: catalog ownership identifies the tool package, not the safety of model-supplied arguments or returned content. Tools must validate schemas and paths, enforce workspace and network scope, request durable permission for mutations, avoid secret leakage, and return external output as untrusted data.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentTool> Tools =
        new("sunder.package.agent:tools");

    /// <summary>
    /// Identifies Runtime sources that dynamically list, resolve, and execute groups of agent tools.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime packages contribute dynamic sources; the Agent Runtime consumes them after its built-in installed-package source. Cardinality is zero or more sources, each exposing zero or more context-dependent tools.</para>
    /// <para>Identity, ordering, and deduplication: <see cref="IAgentToolSource.SourceId"/> identifies the source and combines with each tool id for advertised identity. Extension sources are traversed by display name using ordinal case-insensitive ordering; resolution uses the first matching advertised source/tool and registrations are not deduplicated.</para>
    /// <para>Ownership, threading, cancellation, and failure: host-owned activation instances can list, check, and execute concurrently for different contexts. All async work must honor cancellation. Execution exceptions are generally converted to tool-error results by the run wrapper, while listing, schema, and readiness failures can abort discovery; no blanket source isolation exists.</para>
    /// <para>Trust, provenance, and security: package ownership and source id record different provenance layers. Dynamic catalogs, remote metadata, model arguments, and results are untrusted. Sources must re-resolve the exact advertised tool at execution, prevent confused-deputy source substitution, validate all input, enforce permissions and scope, and protect credentials and transport endpoints.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentToolSource> ToolSources =
        new("sunder.package.agent:tool-sources");

    /// <summary>
    /// Identifies Runtime backends that perform shell and file operations for bound Agent workspaces.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime packages contribute execution targets; the Agent Runtime and dependent tools consume them. Cardinality is zero or more targets, with one primary target resolved for a workspace binding.</para>
    /// <para>Identity, ordering, and deduplication: descriptor target id is the preferred identity and target kind is a compatibility fallback, both matched ordinally without regard to case. Presentation is ordered by display name, kind, and id; resolution takes the first catalog match and registrations are not deduplicated.</para>
    /// <para>Ownership, threading, cancellation, and failure: host-owned activation instances can serve concurrent readiness, shell, and file calls. Every async method must honor cancellation. Exceptions propagate through the calling Runtime operation or tool, whose higher-level policy may convert them to an error; the extension point itself provides no isolation.</para>
    /// <para>Trust, provenance, and security: catalog ownership identifies the backend package, while workspace bindings and paths remain data requiring validation. Targets hold high-impact process and filesystem authority and must enforce path mapping, configured roots, command limits, permission decisions, safe argument handling, output limits, and separation between host and target paths.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentExecutionTarget> ExecutionTargets =
        new("sunder.package.agent:execution-targets");

    /// <summary>
    /// Identifies App and Runtime contributors of workspace-target editor sections.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: execution packages contribute a Runtime editor that owns configuration and typically a separate App presentation proxy; the Agent workspace UI consumes App contributions and Runtime operations consume Runtime contributions. Each role has zero or more contributors, and registration in one role does not cross to the other.</para>
    /// <para>Identity, ordering, and deduplication: contributor id identifies the implementation, section ids are scoped to it, and field ids are scoped to a section. The App filters with <c>CanEdit</c>, traverses contributors and returned sections in catalog/list order, and performs no contributor or section deduplication. Saves route back to the producing contributor and stop at the first unsuccessful result.</para>
    /// <para>Ownership, threading, cancellation, and failure: each host owns its activation-scoped instance. Calls can originate from UI refresh, save, or Runtime transport and implementations must be thread-safe and honor tokens when supplied. One contributor exception can abort aggregate discovery or save and surface as an editor error; there is no per-contributor isolation.</para>
    /// <para>Trust, provenance, and security: catalog ownership establishes package provenance, but labels, actions, option values, save values, paths, package ids, and action parameters cross trust boundaries. Folder pickers and select lists do not authorize values. Apps must validate actions; Runtime contributors must validate section/field ids, paths, scope, and permissions and must never return or display secrets.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentWorkspaceEditorContributor> WorkspaceEditorContributors =
        new("sunder.package.agent:workspace-editor-contributors");

    /// <summary>
    /// Identifies Runtime contributors that migrate package-owned legacy workspace paths into Agent workspace records.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime execution packages contribute migrators; the Agent Runtime consumes all applicable migrators during workspace initialization. Cardinality is zero or more, and each can contribute zero or more paths for a workspace with no current paths.</para>
    /// <para>Identity, ordering, and deduplication: contributor id is diagnostic identity only. Registrations are not deduplicated and run in catalog order. Returned migration items are combined before persistence; contributors must not rely on ordering to resolve conflicts and should return stable, idempotent data.</para>
    /// <para>Ownership, threading, cancellation, and failure: the host owns activation-scoped migrators. Initialization invokes them sequentially and supplies a cancellation token, but treats migration as optional and catches discovery and completion failures so workspace loading continues; implementations must still observe cancellation and return promptly rather than relying on it escaping the isolation boundary.</para>
    /// <para>Trust, provenance, and security: catalog ownership identifies the migration package. Legacy configuration and path strings are untrusted. A migrator must handle only bindings it owns, normalize and validate paths without broad filesystem traversal, avoid copying secrets, and make completion cleanup idempotent because persistence can succeed even when cleanup fails.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentWorkspacePathMigrationContributor> WorkspacePathMigrationContributors =
        new("sunder.package.agent:workspace-path-migration-contributors");

    /// <summary>
    /// Identifies Runtime declarations of permission actions and boundaries used by agent tools and sources.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime packages contribute permission surfaces; the Agent Runtime consumes them when presenting and evaluating permission policy. Cardinality is zero or more surfaces, each declaring zero or more actions and boundaries.</para>
    /// <para>Identity, ordering, and deduplication: surface id identifies the contributor, while action id and boundary id identify policy entries. Surface registrations are not deduplicated. Actions are deduplicated case-insensitively by action id with the first catalog declaration winning, then presented by display name.</para>
    /// <para>Ownership, threading, cancellation, and failure: host-owned activation instances can be queried repeatedly or concurrently. Listing is synchronous, has no cancellation channel, and has no per-surface exception isolation, so implementations must return immutable cached metadata without I/O.</para>
    /// <para>Trust, provenance, and security: catalog ownership establishes package provenance, but declarations are policy metadata rather than grants. Surfaces must use stable narrowly scoped ids, safe default decisions, and non-sensitive text. Tool execution must still bind the evaluated action to the exact session, workspace, resource, tool, arguments, and mutation being approved; unknown policy must fail safely.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentPermissionSurface> PermissionSurfaces =
        new("sunder.package.agent:permission-surfaces");

    /// <summary>
    /// Identifies Runtime contributors of lower-trust supplementary context for model requests.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime packages contribute context sources; the Agent Runtime consumes optional registrations when the context plan permits it. Host-reserved profile and first-party scoped safety instructions are standing context, not recall, and use separate eligibility and bounds. Cardinality is zero or more contributors.</para>
    /// <para>Identity, ordering, and deduplication: contributors are invoked by display name using ordinal case-insensitive ordering; contributor ids do not deduplicate registrations. Returned blocks have no identity, are not deduplicated, and are rendered by descending priority then title subject to aggregate count and size limits.</para>
    /// <para>Ownership, threading, cancellation, and failure: host-owned activation instances can be invoked concurrently across runs. Contributors must honor cancellation. The Runtime propagates cancellation and suppresses other optional-reference failures. Owner-verified required scoped-instruction discovery and acknowledgment fail prompt preparation closed.</para>
    /// <para>Trust, provenance, and security: every block must label immediate provenance, user-authority trust, and usage. All blocks remain user-role data. Ordinary reference blocks cannot direct behavior. Behavioral authority is assigned only by host-controlled profile provenance or the owner-verified first-party Files source with structured canonical scope; self-declared enums and ids are not authority. No block grants permission or wider scope.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentPromptContextContributor> PromptContextContributors =
        new("sunder.package.agent:prompt-context-contributors");

    /// <summary>
    /// Identifies Runtime observers of committed agent lifecycle transitions.
    /// </summary>
    /// <remarks>
    /// <para>Role and direction: Runtime packages contribute compatibility observers; the Agent Runtime projects the six run-scoped lifecycle kinds from its durable outbox. Cardinality is zero or more observers and there is no returned mutation of Agent-owned state.</para>
    /// <para>Identity, ordering, and deduplication: package id plus stable observer id identifies a persisted subscription. Retained events are incrementally backfilled and serialized by subscription and ordering scope. Delivery can repeat across recovery or retry paths, so side effects must be idempotent by event id.</para>
    /// <para>Ownership, threading, cancellation, and failure: the host owns activation instances and serializes calls per subscription. Cancellation requests dispatcher shutdown. Other failures cannot change source state, but are retried and can become poison ordering barriers.</para>
    /// <para>Trust, provenance, and security: package ownership identifies observer code; event kind and message roles identify content provenance. Transcript, assistant, tool, summary, and checkpoint text remains sensitive and untrusted. Observers must not elevate non-user claims, mutate Agent-owned state, evade retention and permission policy, or disclose content to external storage without authorization.</para>
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentLifecycleObserver> LifecycleObservers =
        new("sunder.package.agent:lifecycle-observers");

    /// <summary>
    /// Identifies Runtime observers that consume ordered, durable Agent lifecycle envelopes.
    /// </summary>
    /// <remarks>
    /// Durable observers use a stable observer id as a persisted subscription identity. Delivery is ordered and
    /// at least once, incrementally backfills retained history from a persisted watermark, can resume after package
    /// absence or process failure, and includes rollback and deletion events in addition to the six compatibility
    /// lifecycle events. Session/workspace deletion atomically replaces prior source payloads with content-erasure
    /// receipts; new subscriptions do not replay erased rows. Implementations must make side effects idempotent by
    /// event id, acknowledge erasure receipts as no-ops, and treat all payload text as sensitive, untrusted data.
    /// </remarks>
    public static readonly PackageExtensionPoint<IAgentDurableLifecycleObserver> DurableLifecycleObservers =
        new("sunder.package.agent:durable-lifecycle-observers");

}
