# Static And Dynamic Tools

Agent tools are Worker services implementing the selected `IToolSourceService` API.
Fixed and dynamic catalogs share this contract. Package-local helper interfaces may
organize business code but are not dependencies that external packages reference.

## Static Tools

Select `younics.ai.agent.tool.source` and bind the implementation with
`SunderServiceProvider`, as in the [quickstart](extension-quickstart.md). For a fixed
tool, return a stable single-entry list. Sunder generates registration and dispatch.

An implementation provides:

- One stable `AgentToolDescriptor`.
- Cheap, cancellation-aware readiness through `GetReadinessAsync`.
- Validated execution through `ExecuteAsync`.
- `PermissionAsync` for a specific approval request, or null when none is required.
- `PresentationAsync` for deterministic transcript presentation, or null for default rendering.

A fixed, read-only static tool is the smallest implementation of this contract.

## Dynamic Tool Sources

For a changing catalog, derive `ListToolsAsync` from `AgentToolSourceContext`.
`DescribeAsync` supplies stable source metadata. The
[service declaration](../src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs) owns the
complete list, readiness, preflight, execution, permission and presentation signatures.

`AgentToolSourceContext` supplies the current session, profile, workspace, and selected execution binding. `AgentToolExecutionContext` additionally supplies durable correlation, the compatibility outside-scope flag, and opaque process-local resource leases approved for that exact invocation. The flag alone never authorizes outside-scope file access.

Agent derives model tool declarations from the public descriptors and JSON Schema.
Framework-native tool objects stay process-local; the generated service boundary
transfers business values, not AI framework instances.

Agent re-lists and re-resolves a tool before preparing an invocation. New calls require a ready tool assigned to the profile. Prepared calls retain their exact package owners and advertised descriptor; profile saves, assignment edits and model changes do not revoke an already pending action. Execution still checks the original arguments, descriptor, workspace/binding, target configuration and resource claims, and honors current deny policies. A missing continuation model does not prevent the approved action from executing: its result is recorded first, then the current model is selected for continuation. This prevents stale or replaced tools from executing without making approval depend on a profile revision.

Permission descriptions must be deterministic before authorized preflight. Do not depend on a cached preflight plan to decide whether to return a permission request; preflight may resolve resources only after authorization. A tool result acknowledging UI input is presented as **Input dispatched**. It does not establish that the application effect was applied; verify that separately.

Use `PreflightAsync` for bounded checks after permission resolution and before the
durable execution ledger is marked started, including approved resume. Return null
to dispatch normally; a non-null result must be a safe error recorded without execution.

## Descriptor Contract

| Property | Requirement |
| --- | --- |
| `ToolId` | Stable, non-empty, globally collision-resistant, and case-insensitively unique. Do not change it for display-name edits. |
| `DisplayName` / `Description` | User/model-facing text. Keep descriptions factual and bounded. |
| `IsReadOnly` | Security-sensitive. Set `false` if any successful path can mutate local, remote, account, process, or durable state. |
| `RequiresNetwork` | Declare network dependence. It is descriptive and does not itself authorize traffic. |
| `ArgumentsJsonSchema` | JSON object schema. Set `additionalProperties: false` unless unknown fields are intentionally supported. |
| `SourceKind`, `SourceId`, `SourceDisplayName` | Stable source identity. Agent fills missing fields from the source but explicit values are clearer. |
| `Aliases` | Compatibility lookup names only. Canonical results must use `ToolId`. Avoid ambiguous aliases. |
| `SelectionScope` | `Tool` for individual selection or `Group` for all tools selected through `SelectionGroupId`. |
| `ActivationRequirement` | Optional required profile capability kind/source/id. Use it when tool availability depends on another selected capability. |
| `RuntimeInstructions` | Trusted package-authored instructions added to the system prompt. Never place discovered server text, user content, tool output, or workspace data here. |
| `Priority` | Higher-priority tools are advertised first when several tools fit. It does not bypass profile selection or permissions. |
| `ConcurrencyMode` | Default `Sequential`. Use `ParallelSafe` only for independent, side-effect-free or concurrency-safe calls. |

Profiles explicitly select tools, tool groups, skills, and subagents. A tool being installed does not guarantee it is advertised to every profile. A profile with no matching assignments receives no unconditionally selectable tools.

## Arguments And Results

Use `AgentToolArgumentObject.TryParse` or equivalent strict parsing. It enforces shared JSON depth, property-count, and case-collision policy. Runtime's `ISunderRpcClient.ResourceProfile` is authoritative for RPC payload size. Validate required fields, ranges, enum values, paths, URLs, and cross-field constraints before any side effect.

Expected failures should return an `AgentToolResult` with:

- The canonical `ToolId`.
- A short `Summary` safe for transcript and model consumption.
- Bounded `Content` and optional structured JSON.
- `IsError = true` and a stable package-scoped `ErrorCode`.
- `WasTruncated = true` when output was bounded.
- `BackendId` when it helps identify the selected executor/server without exposing secrets.
- `RequiresPromptContextRefresh = true` when the next provider cycle must rebuild supplementary context before continuing.

Do not return secrets, raw authorization responses, unrestricted local paths, or unbounded command/network output. Unexpected source/tool exceptions are converted by Agent to `tool-execution-exception`; caller cancellation is preserved.

## Permission Pipeline

Permissions authorize a described Agent action; they are not a sandbox.

1. Agent confirms the tool was advertised, assigned, and ready for the current run.
2. `PermissionAsync` may build an `AgentPermissionRequest`.
3. Agent resolves `ActionId` and `BoundaryId` against selected permission-surface services.
4. A configured override, session-tree approval, or inherited Unrestricted Mode may change an `Ask` decision.
5. `Deny` records a denied outcome. `Ask` durably suspends the run. `Allow` supplies the approved context to preflight and execution.
6. Approval resumes only the correlated session/run revision/tool call. Stale requests do not execute under a newer run.

If a read-only tool returns no permission request, it runs after advertisement checks. If a mutating tool returns no request, Agent uses the generic `agent.tool.mutate:provider-requested-mutation` boundary, whose default is `Ask`. If durable correlation or source/workspace identity is insufficient, that mutating tool is denied instead of approved ephemerally.

### Define A Surface

For an issue-creation tool, a surface could declare action `acme.issue.create`,
boundary `selected-project`, a factual description and default decision `Ask`.
Expose the actions in the descriptor returned by `IPermissionSurfaceService.DescribeAsync`,
selected from `younics.ai.agent.permission.surface` and bound with `SunderServiceProvider`.
Action and boundary IDs are persisted preference keys; changing them changes policy identity.

Build the corresponding request from parsed, canonicalized arguments. Include the
exact action/boundary, current tool/workspace/binding correlation, a bounded display
summary, canonical resource identity and `IsMutation=true`. Use the imported model's
properties; do not infer authority from a display name or a previous prompt.

Unknown action/boundary pairs default to `Ask`; an empty action id is denied. Never choose an `Allow` default for an unclassified or outside-scope resource.

## Path And Command Permissions

File sources should ask the selected execution target to resolve a canonical resource and use one of the shared boundary ids:

- `configured-scope`
- `outside-configured-scope`
- `selected-execution-target`
- `unknown`

Classify first with scope widening allowed only for inspection. Populate `ResourceClaims` for every path in a multi-file operation and keep `ResourceReferences` only as stable compatibility correlation data. A claim records the logical path, configured root, no-follow root-chain and target expectations, workspace/binding generations, call/action/resource index, and exact tool/target package owners. Claims are data, not authority: Runtime fingerprints and persists them, then propagates them through preflight and execution. `AllowOutsideConfiguredScope` is set only for an allowed `outside-configured-scope` request.

Transient authority is separate. First-party Local planning issues random `local-resource-authority-v4` capabilities only for outside-scope claims. They are JSON-ignored, process-local, expire after 30 minutes, bind one exact activation/run/revision/call/action/resource/owner tuple, own one retained no-follow authority chain, and redeem once. Preflight checks them without redemption; access transfers the retained authority, and completion revokes unused capabilities after scoped post-access work. Outside mutations also compare a fresh no-follow chain before dispatch but operate only through the retained authority. Configured Local operations and all Docker host-bind operations issue no transient capability and acquire fresh strict authority at execution. An unchanged configured claim can therefore resume after process restart by reacquiring the exact persisted package owners; an outside Local claim cannot restore its capability and returns `permission-reapproval-required`. Legacy `local-resource-v3` and `docker-resource-v3` pending references never auto-execute.

`apply_patch` accepts at most 64 operations and 64 distinct paths. Permission planning checks those bounds before resource resolution, requests at most two outside capabilities per path, and never transfers more than 128 capabilities into one invocation. Multi-path planning owns issued capabilities transactionally: a later resolution failure, cancellation, capability overflow, or failed durable permission suspension revokes every capability already issued. After each successful patch mutation, Local and Docker can return a JSON-ignored post-mutation resource receipt. Compensation replaces the pre-state claim with that exact created/deleted-state claim; outside Local compensation additionally receives one capability backed by the published target handle or retained missing-target parent. Successful completion and compensation cleanup revoke unused receipt authority. Cancellation is rethrown only after every observed mutation is restored and the interrupted operation is proven unchanged or restored. A rollback conflict, unavailable probe, or other uncertain state returns `patch-partial-application` with the exact completed, failed, and uncertain rollback paths.

Shell permission includes a bounded command summary and the selected binding. Structured process invocation is safer than caller-authored shell quoting but still requires execution permission. Shell commands are not parsed into trustworthy file paths and are outside Local and Docker no-follow structured-files guarantees, so scoped `AGENTS.md` mutation enforcement applies only to structured Files tools. Shell completion refreshes workspace roots and known claims; it does not claim arbitrary command paths.

First-party package composition constructs `FilesToolSource` with `IPackageContext` and resolves targets through the Agent RPC catalog. Local/Docker use also requires `IAgentScopedInstructionDiscoveryTarget` and `IAgentExecutionScopeProvider`. Files `grep`/`glob` require `IAgentStructuredFileSearchExecutionTarget` and never fall back to process or broad shell authorization.

## First-Party File Tools

- `read` uses 1-based file-line or directory-entry offsets, up to 2,000 per page.
  Model-visible output is bounded to 50 KiB, with explicit long-line shortening
  and continuation guidance. Display pages are not complete mutation snapshots.
  Image/PDF reads are not supported by these text tools.
  Directories over the 10,000-entry enumeration bound fail with narrowing guidance
  instead of claiming a complete total or a continuation beyond the captured set.
- `grep` supports literal text and invariant case-insensitive matching as explicit
  options. Regex uses bounded .NET non-backtracking syntax, not ripgrep syntax;
  lookarounds/backreferences are unsupported. `glob` supports explicit hidden-file
  inclusion; wildcard matches otherwise exclude hidden segments.
- Search defaults to 100 results, accepts at most 1,000, and has a 30-second
  deadline. Incomplete results report their limiting bound and narrowing guidance.
  Secure traversal and exact execution-target selection still apply.
- `edit` requires a unique exact/LF-aware match, including overlapping matches.
  Repeated replacements require explicit `replaceAll`. Planning preserves original
  source offsets, UTF-8 BOMs and unchanged mixed line endings. There is no fuzzy
  edit or automatic formatter.
- `apply_patch` supports contextual updates, insertions/deletions, EOF anchors and
  no-final-newline markers. All operations preflight before mutation, and each path
  appears at most once. Strict parsing rejects malformed or trailing patch content.
- Existing-file writes/edits/patches require complete bounded text snapshots and
  compare-and-swap checks. Invalid UTF-8, binary or incomplete snapshots are not
  writable through these tools. A new-path write is create-only. Concurrent changes
  fail without a blind retry; partial patch outcomes retain existing compensation
  and ambiguity reporting.

**Patch moves:** immediately follow `*** Update File: <source>` with
`*** Move to: <destination>`, with optional edit hunks. As in OpenCode, this is a
text-content move: create the destination, then remove the source. It is **not an
atomic native rename**, and filesystem identity/metadata are not guaranteed to
survive. UTF-8 BOMs and unchanged line endings are preserved.

Both paths participate in permission, scope/instruction and 64-path limit checks.
Unlike OpenCode's overwrite behavior, a destination must not exist; publication
is create-only, and source deletion checks its complete preflight content hash.
Source/destination overlap, case-only moves and compound AGENTS.md changes are
rejected. A failed move uses guarded best-effort compensation, not a transaction:
if source restoration or destination ownership cannot be confirmed, recovery
files are retained and the partial/uncertain outcome is reported. Never blindly
retry it. Rejected create/CAS operations are not treated as applied merely because
another writer's content happens to match the planned result.

## Discovery And Presentation

- Return `null` readiness when a source does not own the requested id; return `Failed` for an owned but unavailable tool.
- Cache external discovery only with explicit invalidation and bounded lifetime. A stale cache is never authority to execute.
- Dynamic sources that alter selectable groups should also implement `IAgentProfileSelectableCapabilityProvider` and `IAgentProfileSelectableCapabilityChangeNotifier`.
- `IAgentToolPresentationResolver` must be pure and tolerate malformed historical arguments/results. Presentation failure must not be needed to understand execution security.

Agent and Subsession transcripts default to compact activity. Adjacent safe-status
`read`, `grep` and `glob` calls within one execution scope form an expandable group
between commentary boundaries. Expanding a group reveals original individual call
rows; expanding a call separately loads its existing lazy details. Failures,
ambiguous outcomes, permission controls, child-session links and shell/background
activity remain separate. **Detailed activity** shows the individual rows directly.
This switch changes only presentation, never stored history, correlation or model
context. Group state is bounded by the retained transcript window.

## Security Checklist

- Tool identity remains stable and unique.
- Mutating behavior is never labeled read-only.
- Input and output are bounded.
- URLs reject unsupported schemes and apply SSRF/DNS policies where relevant.
- Structured paths are acquired through target-owned no-follow authority rather than validated and reopened by path.
- Permission requests persist stable resource claims, never transient capability tokens or retained handles.
- Multi-path requests bind every claim to an exact resource index, call, action, workspace/binding generation, and package owners.
- Outside Local access additionally requires an invocation-bound, single-use `local-resource-authority-v4` capability; configured Local and Docker access never does.
- Duplicate dispatch returns the authoritative tool-ledger result rather than reacquiring authority or replaying a side effect.
- Cancellation is honored, but code does not claim cancellation rolled back a completed side effect.
- Parallel-safe tools have no hidden shared mutation.
- Preflight performs no represented tool side effect, returns only error skip results, and is tested before durable start in normal and approved-resume flows.
- Logs and results contain no secrets.

Next: [Execution targets, paths, and security](execution-targets.md) and [Testing extensions](testing-extensions.md).
