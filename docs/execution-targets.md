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
| `IAgentRangedFileExecutionTarget` | Declare that `ReadFileAsync` validates one-based `Offset`/`Limit` and returns line metadata/errors. |
| `IAgentExecutionScopeProvider` | Describe target-visible workspace roots, default working directory, and path syntax. Required by consumers that need a concrete execution workspace. |
| `IAgentExecutionPathMapper` | Map a target-visible path back to a host path inside an allowed root. |
| `IAgentExecutionPathEnvironment` | List and add target-visible executable search paths. Persist changes in binding-scoped target configuration. |
| `IAgentExecutionResourceResolver` | Project host resources into target-visible paths, preserving source, access mode, and metadata. |
| `IAgentWorkspaceEditorContributor` | Supply target-specific workspace fields. Use separate Runtime authority and App presentation/proxy contributions. |
| `IAgentWorkspacePathMigrationContributor` | Migrate legacy target-owned paths into Agent-owned `AgentWorkspaceRecord.Paths`. |

Do not implement an optional interface with partial or misleading behavior. Consumers branch on interface presence.

## Path Model

Three path domains can exist:

| Domain | Example | Owner |
| --- | --- | --- |
| Host path | `/Users/alex/project` or `C:\Users\alex\project` | Host/execution package |
| Workspace record path | Host path selected by the user | Base Agent workspace store |
| Execution path | `/workspace/project` in a container or remote target path | Execution target |

`AgentExecutionScopeDescriptor.WorkspacePaths` and `DefaultWorkingDirectory` are execution paths. `AgentExecutionPathMapping` explicitly carries execution and host paths plus `IsInsideAllowedRoot`. Never infer equivalence between path domains by string concatenation.

`AgentExecutionResourceDescriptor` allows another package to request mapping for a host resource, including `ReadOnly` or `ReadWrite` access. Return one `AgentResolvedExecutionResource` per accepted descriptor with the same stable resource/source identity. Do not silently upgrade access.

## Canonicalization Requirements

For every file and working-directory operation:

1. Reject empty, malformed, unsupported, or target-incompatible paths.
2. Resolve relative paths against the configured default execution directory.
3. Normalize `.` and `..` segments using target path rules.
4. Resolve physical links/reparse points or use an equivalent backend-safe containment check.
5. Compare against canonical configured roots with the target filesystem's case rules.
6. Revalidate immediately before opening, replacing, deleting, or launching.
7. Reject outside-scope access unless the Runtime supplied an approved `AllowOutsideConfiguredScope` context.

`ResolveFileResourceAsync` is used for permission planning and should classify a resolvable path as `configured-scope`, `outside-configured-scope`, or `unknown`. It may inspect an outside path for classification, but that does not authorize a later operation.

Never trust `AgentPermissionRequest.Path` or a previous resolution as the final path. The execution target performs the last check.

## File Semantics

- Enforce text/binary and full-read size policy. Shared local full-read limit is exposed through `AgentPayloadLimits`.
- A ranged target validates positive one-based offsets/limits, reports `StartLine`, `EndLine`, and `TotalLines`, and uses the shared stable read error codes.
- Preserve `WasTruncated` whenever content or directory entries are bounded.
- Writes replace complete text. If `ExpectedContentHash` is present, compare the lowercase SHA-256 of current UTF-8 text immediately before mutation and fail without writing on mismatch.
- Deletes with `ExpectedContentHash` apply the same compare-and-swap rule to regular files.
- Prefer atomic temp-file plus replace semantics. Cancellation is not a rollback guarantee; report whether mutation completed.
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

A path migrator runs only when the Agent workspace has no current paths. `GetLegacyWorkspacePathsAsync` should be read-only and idempotent; `CompleteWorkspacePathMigrationAsync` removes or marks legacy data only after Agent persists the migrated paths. Cleanup may be retried.

## Security Boundaries

### Local Execution

The first-party local target runs with the current host user's authority. Canonical path checks and Agent permissions constrain requested operations, but a launched process can use all authority available to that user. It is not an operating-system sandbox.

### Docker Execution

The first-party Docker target drops all Linux capabilities, enables `no-new-privileges`, disables networking by default, bounds memory/CPU/PIDs, and uses an init process. It is still not a complete sandbox:

- Configured workspace paths are writable bind mounts.
- Container images execute trusted code.
- Docker daemon access is security-sensitive and commonly host-equivalent.
- Kernel/container runtime vulnerabilities remain outside Agent's permission model.

### Custom Or Remote Execution

Document the effective principal, filesystem boundary, network policy, persistence, resource limits, tenancy, and cleanup behavior. Readiness text and settings must not call a target sandboxed unless the implementation establishes and tests that boundary.

## Conformance Tests

At minimum, run the same contract scenarios against every target:

- Relative/absolute paths, sibling-prefix confusion, `..`, symlinks, dangling links, case behavior, and root deletion attempts.
- Read ranges, binary/large files, directory bounds, expected-content-hash success/conflict, and cancellation races.
- Shell/process argument boundaries, working-directory containment, timeout, output bounds, process-tree cleanup, and missing executable.
- Readiness for missing settings/backend/root and transition to ready.
- Mapping round trips and access-mode preservation for optional resource mapping.
- Runtime/App reference boundaries and package unload cleanup.

See the first-party conformance and hardening fixtures under `tests/Sunder.Package.Agent.Execution.Local.Tests`. Next: [Static and dynamic tools](tools-and-permissions.md).
