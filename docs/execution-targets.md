# Execution Targets, Paths, And Security

An execution target implements shell and file operations for an Agent workspace. Register `IAgentExecutionTarget` through `PackageExtensionPoints.ExecutionTargets` in the Runtime role.

Execution targets are authorities, not presentation adapters. They own path interpretation, process/container/remote lifecycle, output bounds, timeout enforcement, and final scope checks.

## Core Contract

Every target exposes:

- `AgentExecutionTargetDescriptor` with stable `TargetKind`, stable `TargetId`, display metadata, and shell/file support flags.
- `GetReadinessAsync` for the selected workspace and binding.
- `GetShellAsync` describing actual shell syntax and executable.
- `ResolveFileResourceAsync` for canonical permission classification.
- Shell execution and file read/write/delete operations.

Agent workspace bindings persist a contribution id. Resolution uses the first target whose `TargetId` or `TargetKind` matches it, case-insensitively. Keep both values stable; do not publish two active targets with colliding ids or kinds.

Return `NeedsConfiguration` for missing user configuration and `Failed` for an unusable configured backend. A descriptor's `SupportsShell` or `SupportsFiles` must match actual behavior.

## Optional Capabilities

| Interface | Use |
| --- | --- |
| `IAgentProcessExecutionTarget` | Execute a file name plus an ordered argument list without caller-authored shell quoting. |
| `IAgentStructuredFileSearchExecutionTarget` | Execute a bounded structured grep/glob request under target-owned filesystem authority. First-party Local and Docker implement this without launching a process. |
| `IAgentFileSearchExecutionTarget` | Legacy path-bound process-search contract retained for external compatibility. New targets should prefer the structured capability. |
| `IAgentRangedFileExecutionTarget` | Declare that `ReadFileAsync` validates one-based `Offset`/`Limit` and returns line metadata/errors. |
| `IAgentExecutionScopeProvider` | Describe target-visible workspace roots, default working directory, and path syntax. Required by consumers that need a concrete execution workspace. |
| `IAgentExecutionPathMapper` | Map a target-visible path back to a host path inside an allowed root. |
| `IAgentExecutionPathEnvironment` | List and add target-visible executable search paths. Persist changes in binding-scoped target configuration. |
| `IAgentExecutionResourceResolver` | Project host resources into target-visible paths, preserving source, access mode, and metadata. |
| `IAgentScopedInstructionDiscoveryTarget` | Discover exact `AGENTS.md` files on bounded configured-root-to-target ancestor chains in the target's own path namespace. |
| `IAgentWorkspaceEditorContributor` | Supply target-specific workspace fields. Use separate Runtime authority and App presentation/proxy contributions. |
| `IAgentWorkspacePathMigrationContributor` | Migrate legacy target-owned paths into Agent-owned `AgentWorkspaceRecord.Paths`. |

Do not implement an optional interface with partial or misleading behavior. Consumers branch on interface presence.

### Scoped Instruction Discovery

`IAgentScopedInstructionDiscoveryTarget` is an optional filesystem capability used by the first-party Files source. It does not recursively scan a workspace. For each requested file or directory probe, the target must:

1. Resolve in the execution target's filesystem namespace and retain operation-owned authority for every traversed ancestor.
2. Select the most-specific configured workspace root containing the target directory.
3. Inspect only exact, case-sensitive `AGENTS.md` entries on that root-to-target ancestor chain.
4. Return target-visible canonical paths and subtree applicability, never substituted host paths for Docker or remote targets.
5. Omit probes outside configured roots even when the enclosing operation has separate outside-scope approval.
6. Reject requests over 64 probes, chains over 64 directories, and documents over 12,000 complete characters. Never return a partial policy.

Strict no-follow targets reject a linked root, ancestor, final probe, or `AGENTS.md` entry rather than deriving policy through it. First-party Local and Docker read each exact-case document through the pinned directory handle used for discovery. Target and scope fingerprints must change when persisted claims are no longer safe to reuse, such as a different structured namespace, configured root identity, or fingerprint scheme version. Discovery grants no file permission.

## Path Model

Three path domains can exist:

| Domain | Example | Owner |
| --- | --- | --- |
| Host path | `/Users/alex/project` or `C:\Users\alex\project` | Host/execution package |
| Workspace record path | Host path selected by the user | Base Agent workspace store |
| Execution path | `/workspace/project` in a container or remote target path | Execution target |

`AgentExecutionScopeDescriptor.WorkspacePaths` and `DefaultWorkingDirectory` are execution paths. `AgentExecutionPathMapping` explicitly carries execution and host paths plus `IsInsideAllowedRoot`, but is advisory only: it retains no handle and must not be turned into a host-side structured mutation. Never infer equivalence between path domains by string concatenation.

Builder consumes a mapping only as advisory host metadata and delegates project creation/build/publish to the selected target's `dotnet` process. It does not create or replace mapped host paths through BCL file APIs. Those `dotnet` process operations retain general process authority and are explicitly outside the structured no-follow guarantee.

`AgentExecutionResourceDescriptor` allows another package to request mapping for a host resource, including `ReadOnly` or `ReadWrite` access. Return one `AgentResolvedExecutionResource` per accepted descriptor with the same stable resource/source identity. Do not silently upgrade access.

## Canonicalization Requirements

For every file and working-directory operation:

1. Reject empty, malformed, unsupported, or target-incompatible paths.
2. Resolve relative paths against the configured default execution directory.
3. Normalize `.` and `..` segments using target path rules.
4. For strict structured operations, reject every symbolic link, magic link, or reparse point; do not resolve even a contained link.
5. Pin the configured root, existing absolute ancestors, and traversed child directories for the full operation.
6. Reject device/volume transitions and perform mutation relative to the pinned parent.
7. Reject outside-scope access unless the target supports it and Runtime supplied both compatibility context and an exact opaque resource identity for that invocation. First-party Docker never widens structured access beyond configured bind mounts.

`ResolveFileResourceAsync` is used for permission planning and should classify a resolvable path as `configured-scope`, `outside-configured-scope`, or `unknown`. Multi-path operations carry every canonical reference. A target may inspect an outside path for classification, but that does not authorize a later operation. First-party Docker instead rejects paths without configured host-bind mappings as `docker-structured-bind-required`.

Never trust `AgentPermissionRequest.Path` or a previous resolution as the final path. The execution target performs the last check.

`IAgentStructuredFileSearchExecutionTarget` accepts only structured grep/glob fields and returns structured matches. First-party Local and Docker perform managed, handle-relative traversal and never pass a validated path to `rg`, `grep`, or `find`. They reject links and device transitions anywhere traversed, skip binary files without returning partial matches, apply regex timeout/cancellation, and bound expression size/expansion, traversal, matches, and output. Reserved internal entries remain hidden but are charged to the same raw traversal budget before filtering, and every glob/include match in one request consumes one aggregate matcher-state budget. POSIX paths treat backslashes as literal filename characters, not separators. The additive `IAgentFileSearchExecutionTarget` contract remains available only for compatibility with external implementations.

## File Semantics

- Enforce text/binary and full-read size policy. Shared local full-read limit is exposed through `AgentPayloadLimits`.
- A ranged target validates positive one-based offsets/limits, reports `StartLine`, `EndLine`, and `TotalLines`, and uses the shared stable read error codes.
- Preserve `WasTruncated` whenever content or directory entries are bounded.
- Writes replace complete text. If `ExpectedContentHash` is present, compare the lowercase SHA-256 of current UTF-8 text immediately before mutation and fail without writing on mismatch.
- Deletes with `ExpectedContentHash` apply the same compare-and-swap rule to regular files.
- Prefer atomic temp-file plus replace semantics. Cancellation is not a rollback guarantee; report whether mutation completed.
- When `CapturePostMutationResource` is requested, return a JSON-ignored `PostMutationResource` bound to the exact state created by the mutation. Do not implement rollback by resolving the pathname again after mutation.
- Escape control characters in display-only directory listings; structured search paths preserve newline-bearing names as data.
- Return expected failures as `AgentFileReadResult.Failure` or error `AgentFileMutationResult`, not backend exception dumps.

## Process And Shell Semantics

- `AgentProcessCommandRequest.Arguments` are already separated. Do not join them into a shell command unless the backend intrinsically requires it and applies correct target quoting.
- Resolve bare executables against the target's effective `PATH`; reject missing or disallowed executables explicitly.
- Apply bounded positive timeouts, stdout/stderr limits, process-tree cleanup, and non-interactive environment settings.
- Return a bounded combined output, exit code, timeout flag, resolved target working directory, and truncation flag.
- Treat command output as untrusted data that may contain secrets or prompt injection.
- Cancellation should stop owned work where possible without killing unrelated host processes.

## Workspace Editing And Migration

An execution package commonly has three role-separated objects:

- Runtime target implementing execution.
- Runtime editor/configuration service reached through typed package operations.
- App `IAgentWorkspaceEditorContributor` that renders fields and calls the Runtime service.

`AgentWorkspaceEditorContext.ConfigurationId` is binding-scoped. Validate section/field ids and preserve unknown configuration owned by newer versions. Return expected save errors through `AgentEditorSaveResult.Failed`.

The Agent App contains each workspace-editor contributor independently. A Runtime invocation failure, package retirement, or unavailable owner replaces only that owner's affected section with a retryable host card; it does not abort aggregate discovery or remove healthy Local/Docker sections. Retry is bound to the original owner-activation reference and is discarded if the workspace, target, or adaptive editor intent changes. Unexpected contributor exceptions are not treated as recoverable section failures: the App reports them against the exact owning package and applies its normal package-disable policy.

A path migrator runs only when the Agent workspace has no current paths. `GetLegacyWorkspacePathsAsync` should be read-only and idempotent; `CompleteWorkspacePathMigrationAsync` removes or marks legacy data only after Agent persists the migrated paths. Cleanup may be retried.

## Security Boundaries

### Local Execution

The Local custom settings view is hydrated through the hosted navigation-preparation lifecycle before it becomes visible or interactive. Detected shells are read-only and separate from revisioned custom shells. Custom-shell saves carry the catalog revision, reconcile by shell id, and preserve newer field edits, drafts, and selection when a refresh or save response arrives late.

Local structured file operations use an operation-owned secure root. Linux uses `openat2` with no-link, no-magic-link, beneath, and no-cross-device resolution plus descriptor-relative mutation. macOS uses `openat` with `O_NOFOLLOW|O_DIRECTORY|O_CLOEXEC`, descriptor-relative mutation, `fstat`, and `st_dev` checks. Unsupported Unix ABIs and Linux kernels without the required `openat2` guarantees fail closed. Windows opens the local drive root with `CreateFileW`, then uses parent-handle-relative `NtCreateFile` and handle-relative enumeration for secure reads, listings, search, and scoped-instruction discovery. Windows structured writes and deletes are deliberately unavailable until their final publish/delete syscalls have the same proven exact-handle guarantees; they fail before mutation as `strict-platform-mutation-unavailable`.

Configured roots and approved outside resources containing any link/reparse point, hard-linked regular file, or device/volume transition are unsupported, including links that remain inside a configured root. Permission planning emits a durable `local-host-resource-claim-v1` identity record for the exact logical path, configured root, complete opened identity chain, target expectation, invocation scope, generations, and owners. Only the final component may be absent; a missing intermediate directory fails before a claim is emitted. Configured-scope claims carry no transient authority. Outside planning additionally issues distinct random `local-resource-authority-v4` capabilities for the bounded number of authority uses required by the tool. Each capability owns one retained no-follow authority chain, expires after 30 minutes, is process-local and single-use, and is bound to the exact claim and tool activation.

Execution never treats serialized identities as handles. Outside preflight validates capabilities without redeeming them; access then transfers the retained authority from one capability into the operation, so a retargeted alias cannot redirect a read or search. Mutations additionally audit a fresh no-follow chain immediately before dispatch, then mutate only through the retained authority, so changed aliases, parents, or final targets require reapproval and are never touched. Configured operations acquire a fresh chain without a capability and can resume after restart only when every opened path identity, the target expectation, workspace/binding generations, and exact package owners still match. Post-access scoped-instruction discovery omits outside roots and does not redeem file authority; completion revokes unused capabilities. Outside operations after restart require explicit reapproval because their capabilities are deliberately not persisted. Recursive deletion opens identity chains for every configured root in the same operation and rejects a target whose physical identity equals or precedes any root, including case-folded aliases. Legacy v1-v3 references fail closed. The boolean outside-scope flag alone is never authority.

Patch rollback uses the same rule. A successful write transfers the exact published file handle already opened and synced by the mutation platform into a post-state claim; it does not reopen the name. A successful delete retains the exact parent chain and a missing-target expectation. Compensation validates that post-state claim and expected content immediately before restoring the pre-state. Outside Local receipts issue one invocation-bound capability for that rollback; configured Local and Docker reacquire fresh authority and compare it to the receipt. If the name or identity changes, compensation fails closed rather than modifying the replacement.

These guarantees apply to structured Files read/list/write/delete/search and Local scoped-instruction discovery. The first-party Local target still runs general shell/process commands with the current host user's authority. A launched process can traverse links, mounts, and any resource available to that user; it is not an operating-system sandbox. Working-directory and executable path resolution for shell/process execution remain outside the structured guarantee.

### Docker Execution

The Docker custom settings view hydrates image catalog, command timeout, and CLI path as one required Runtime snapshot before first presentation. Image add/delete are serialized single-document commits with a monotonically increasing catalog revision; listing is read-only and malformed or future catalog documents are preserved and surfaced. New image references require an explicit tag, including `latest`, or a full sha256 digest; tagless references remain invalid. Before launch, Docker resolves the selected reference to a complete local sha256 image ID and runs with `--pull never`. Semantic migration retains legacy tagless references as `NeedsAttention`, while schema 2 `latest` entries become refreshable under schema 3.

The first-party Docker target drops all Linux capabilities, enables `no-new-privileges`, disables networking by default, bounds memory/CPU/PIDs, and uses an init process. It is still not a complete sandbox:

- Configured workspace paths are writable bind mounts.
- Container images execute trusted code.
- Docker daemon access is security-sensitive and commonly host-equivalent.
- Kernel/container runtime vulnerabilities remain outside Agent's permission model.

Docker structured read/list/write/delete/search, permission resolution, and scoped `AGENTS.md` discovery perform the filesystem operation directly against configured host bind roots through the same operation-owned secure-handle engine as Local. Container paths are mapped lexically to the most-specific configured bind, while every host root and traversed component is then opened no-follow with device/volume checks. Results retain POSIX container-visible paths. Before every structured operation, Docker acquires the lifecycle lock and verifies the selected running container and bind relationship; the actual requested file operation does not run through a container shell helper. Permission resolution emits a stable `docker-host-resource-claim-v1` record whose namespace binds the pinned endpoint, daemon/container signature, selected binding, image, and complete ordered mount identity chains. It retains no mount or target handles and issues no transient capability.

Docker structured operations have no container-private or outside-bind mode. A path without a configured host-bind mapping fails closed as `docker-structured-bind-required`, even when compatibility context contains `AllowOutsideConfiguredScope` or approved references. Each execution reacquires and re-challenges fresh mount roots, recomputes the structured namespace, captures the exact target or bounded absent suffix, and validates the current claim before access. POSIX component text is preserved exactly; Windows host mapping rejects separator, ADS, rooted, trailing-dot/space, and device-name aliases and proves the combined host path remains below the selected bind. Configured bind roots and logical ancestors of nested binds cannot be deleted, while fresh same-generation physical chains also protect roots reached through host aliases, case-folding, or reparenting that preserves the final inode. Unchanged configured claims can survive process restart; changed endpoint, daemon, container signature, image, mount chain, root, target, binding generation, or owner requires a new claim. Legacy `docker-resource-v3` references are not authority. `MapToHostPath` is advisory, remains limited to configured mounts, and performs the same no-follow host probe.

General Docker shell/process execution still uses normal container filesystem semantics. It is outside the structured no-follow guarantee and may traverse links available inside writable bind mounts. Remote Docker contexts and TCP/SSH endpoints are rejected. The first accepted local endpoint is pinned for the target lifetime, and every subsequent daemon, image, container, exec, and cleanup invocation receives an explicit `--host` argument, so later ambient context or environment changes cannot redirect work. Container reuse signatures include the pinned endpoint, daemon id, immutable image id, security policy, host UID/GID policy, and exact mount-root identities. Containers run as the host effective UID/GID. After every create, start, or reuse, each mount must pass a random bidirectional challenge: the container reads a host-created secret, creates and reads a nested response, the host verifies it through retained handles, and the container must remove the exact challenge files. Failure removes a newly created container and stops the operation. Docker structured and shell/process work share the same lifecycle lock.

Structured Unix host writes create and flush a temporary regular file in the pinned parent, retain exact source/target handles, preserve metadata from the freshly quarantined target handle, and publish without replacement through pinned-directory APIs. Regular-file hard links are rejected. Linux prefers `O_TMPFILE`, attempts exact-fd publication with `linkat(AT_EMPTY_PATH)`, retries unprivileged publication through `/proc/self/fd` plus `AT_SYMLINK_FOLLOW`, and falls back when necessary to a random same-directory `O_CREAT|O_EXCL|O_NOFOLLOW` file that is identity-checked immediately before no-replace rename. macOS retains its named temporary plus `fclonefileat` behavior. Unix first moves an existing selected target name to a random reserved quarantine and verifies the moved inode through a retained descriptor. The transaction remains open through published-file and parent-directory fsync. A failure before either durability barrier commits sanitizes and hides the exact failed publication, restores the original quarantine identity, verifies and fsyncs the restored target, and fsyncs its parent; if another object occupies the name, it is moved without replacement to a reserved alias first. Incomplete rollback returns `strict-mutation-recovery-required`, leaves exact recoverable state under hidden reserved names, and sanitizes failed new bytes before returning. Unix quarantine entries are intentionally retained after successful mutation because pathname unlink cannot prove inode identity at the final syscall. Direct access to reserved challenge, temporary, and quarantine names is denied and those entries are omitted from results, while raw entries still consume traversal budgets. Each physical parent identity has one in-process mutation coordinator shared by reservation accounting and Docker challenges. Capacity is reserved atomically before mutation; each directory permits at most 1,024 retained reserved entries and quarantine accounting scans at most 8,192 entries. Nonrecursive Unix directory deletion, including an empty directory, fails as `recursive-directory-delete-required`. Ranged reads, search, listing, and recursive traversal enforce byte, line, entry, name-byte, and depth limits. Expected-content hashing is checked again after quarantine and before publication/deletion. Cancellation after quarantine completes the exact validated mutation rather than leaving the selected target hidden, but unrelated Unix processes are not subject to the in-process coordinator.

### Custom Or Remote Execution

Document the effective principal, filesystem boundary, network policy, persistence, resource limits, tenancy, and cleanup behavior. Readiness text and settings must not call a target sandboxed unless the implementation establishes and tests that boundary.

## Conformance Tests

At minimum, run the same contract scenarios against every target:

- Relative/absolute paths, sibling-prefix confusion, `..`, root/child links, reparse points, device transitions, case behavior, and root deletion attempts.
- Read ranges, binary/large files, directory bounds, expected-content-hash success/conflict, and cancellation races.
- Shell/process argument boundaries, working-directory containment, timeout, output bounds, process-tree cleanup, and missing executable.
- Readiness for missing settings/backend/root and transition to ready.
- Mapping round trips and access-mode preservation for optional resource mapping.
- Exact `AGENTS.md` casing, longest-root selection, root-to-target-only discovery, fail-closed depth/content bounds, linked roots/targets/documents, newline paths, and target-namespace paths when scoped discovery is implemented.
- Deterministic parent, target-name, temporary-name, and in-place replacement at pre-open, pre-publish, final-publish, pre-unlink, and final-unlink barriers, with attacker-selected inodes never overwritten, published, or unlinked.
- Runtime/App reference boundaries and package unload cleanup.

See the first-party conformance and hardening fixtures under `tests/Sunder.Package.Agent.Execution.Local.Tests`. Next: [Static and dynamic tools](tools-and-permissions.md).
