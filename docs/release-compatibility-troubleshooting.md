# Release, Compatibility, And Troubleshooting

This guide is for extension authors. Maintainers releasing the complete first-party Agent family should also follow [`RELEASES.md`](RELEASES.md).

## Compatibility Contract

Agent 2.x defines one schema-first RPC compatibility surface. Cross-package behavior is limited to the 18 bundled descriptors, generated bindings, and manifest-declared providers.

Use these coordinated ranges:

```xml
<PackageReference Include="Sunder.Sdk" Version="[1.1.0,1.2.0)" />
<PackageReference Include="Sunder.Package.Build" Version="[1.1.0,1.2.0)" PrivateAssets="all" />
<PackageReference Include="Sunder.Package.Agent.Protocol" Version="[2.0.0,3.0.0)" />
```

```csharp
[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=2.0.0 <3.0.0")]
```

NuGet and Sunder Runtime ranges use different syntax. The Protocol and Runtime ranges accept compatible 2.x releases and reject 3.0. Sunder SDK packages keep their independent Core version range.

Do not use an unbounded `>=2.0.0` runtime dependency. A future major may intentionally change descriptors or behavioral invariants even when binary loading appears possible.

## Versioning An Extension

An independent extension can use its own package version, but every released build should record which Agent minor it targets:

- Patch release: fixes behavior without changing extension package contracts or Agent range.
- Minor release: adds backward-compatible extension features while retaining the same Agent range.
- Major release: breaks the extension's own persisted/public behavior.
- Agent-major update: compile against the new Protocol package and change both NuGet and runtime upper/lower bounds in a deliberate release.

Do not publish different bytes for an existing package id/version. Sunder package versions are immutable artifacts.

## Protocol Surface

For this repository, `src/Sunder.Package.Agent.Contracts/PublicAPI.Shipped.txt` is the immutable stable API ledger. `PublicAPI.Unshipped.txt` tracks reviewed changes before the next stable boundary. Stable family releases require no unshipped entries.

Extension authors should:

- Compile against public Protocol and SDK types only.
- Avoid reflection into concrete first-party assemblies.
- Treat optional interfaces as capability checks.
- Persist stable ids rather than CLR type/assembly names.
- Handle an endpoint becoming stale on package deactivation.
- Rebuild and retest for each supported Agent major.

Binary compatibility is not the only requirement. Descriptor ids, permission boundaries, trust channels, role ownership, ordering, persistence, and failure semantics are behavioral contracts documented in this guide set.

## Release Checklist

1. Set an immutable extension version.
2. Confirm all Sunder/Agent dependency ranges are bounded to the intended minor.
3. Restore/build/test in standalone NuGet mode, not only source-reference mode.
4. Inspect generated manifest capabilities and runtime dependencies.
5. Publish the project and validate the exact `.sunderpkg` bytes.
6. Run clean install/activation, configuration, success/failure, unload/reload, and same-version reinstall smoke.
7. Validate App snapshot behavior when an App module exists.
8. Check logs/results/archive for secrets and local files.
9. Exercise migration from the previous extension version with a backup of test data.
10. Publish immutable bytes, then move a Registry dist tag only after verification.

The first-party family releases all 15 runtime packages and `Sunder.Package.Agent.Protocol` from one Agent commit. For a new release, the workflow resolves Core `main` once at workflow start and reuses that full SHA for every Core checkout. A recovery rerun reuses the Core SHA from preserved release evidence instead of resolving `main` again. Third-party extensions should not assume a mixed family patch set is valid.

## Troubleshooting

### Restore Or Compile Failure

| Symptom | Check |
| --- | --- |
| Protocol types missing | Directly reference `Sunder.Package.Agent.Protocol` with `[2.0.0,3.0.0)`. Generated bindings are under `Sunder.Package.Agent.Protocol.Generated.*`. |
| SDK/module types missing | Add a direct `Sunder.Sdk` reference; do not rely on the Protocol package's transitive dependency. |
| Manifest/build targets missing | Add `Sunder.Package.Build` with `PrivateAssets="all"`. |
| Mixed package downgrade/conflict | Inspect transitive packages and align the Agent Protocol/runtime family to 2.x while preserving the supported Core SDK range. |
| Works only in private workspace | Force standalone/NuGet mode and restore in a clean checkout; remove accidental project/implementation references. |

Useful commands:

```powershell
dotnet restore path/to/Extension.csproj --force-evaluate
dotnet list path/to/Extension.csproj package --include-transitive
dotnet build path/to/Extension.csproj
```

### Package Does Not Activate

Check the generated `sunder-package.json`, not a source manifest:

- Package id is lowercase dot-separated ASCII.
- Entry assembly contains one `SunderPackage` attribute.
- Dependency is exactly `sunder.package.agent` with a satisfiable bounded range.
- Installed base Agent and SDK baseline are from the same compatible family.
- Required capabilities inferred by `Sunder.Package.Build` are supported by the host.
- No implementation/host assemblies were packaged as private dependencies.

Runtime validates the package graph before assembly load. A dependency-range or SDK-baseline error cannot be fixed inside `PackageModule`.

### RPC Provider Is Missing

- Confirm `ConfigureRuntimeServices` registered the service.
- Confirm the project declares the provider id and contract with `SunderRpcProvider`.
- Confirm `RegisterRuntimeContributions` publishes that same provider id with `RegisterRpcProvider`.
- Confirm the code is in `ISunderRuntimePackageModule`, not only `ISunderAppPackageModule`.
- For App presentation, confirm the inverse: the App contribution is registered in the App role and calls Runtime through typed operations.
- Confirm stable semantic ids do not collide with another active contribution.
- Use RPC discovery in a diagnostic fixture to verify the contract id, owner package, provider id, and active endpoint.

Runtime and App provider visibility is intentionally role-scoped.

### Provider Or Model Is Unavailable

- Profile `ProviderId` must match the descriptor case-insensitively.
- Persisted `ModelId` must still appear in the provider's model catalog or remain supported explicitly.
- Readiness should explain missing key/auth/configuration without throwing.
- Provider credentials must be in Runtime package secrets, not App settings/state.
- For embeddings, confirm the profile has a separate `model.embedding` binding and that the embedding provider supports that auth mode.

### Tool Is Not Advertised

- The tool/source must be active in Runtime.
- Readiness must be `Ready` for the current session/profile/workspace/binding.
- The profile must select the tool, its group, or its activation requirement.
- A dynamic source requiring a session cannot advertise during context-free discovery.
- Native declaration and descriptor identities must match.
- Duplicate ids resolve to the first source/tool and should be renamed.

### Tool Is Denied Or Waits

- Agent re-resolves the advertised source/id/read-only identity immediately before execution.
- Mutating tools need a specific permission request or receive the generic mutation `Ask` policy.
- Unknown action/boundary defaults to `Ask`; an empty action id is denied.
- Outside-scope paths require target classification plus an exact opaque resource-identity approval; the compatibility boolean is insufficient.
- Approval is tied to session/run revision/tool call. A superseding run makes it stale.
- Unrestricted Mode affects the current session tree but is not operating-system isolation.

### Execution Path Failure

- Verify the workspace has a current enabled primary execution binding.
- `ContributionId` must match the target `TargetId` or `TargetKind`.
- Check target readiness and configured roots.
- Use execution paths inside containers/remotes, not host paths.
- Remove symlinks/reparse points and mount/device transitions from Local structured paths, including configured-root ancestors and contained links.
- On Linux, use a kernel that supports `openat2` with the required resolution flags; unsupported Unix ABIs fail closed. Windows structured reads remain available, but writes and deletes deliberately return `strict-platform-mutation-unavailable` before mutation.
- `recursive-directory-delete-required` means a Unix directory delete must be retried only with an intentional `Recursive=true`; it also applies to empty directories.
- `strict-mutation-recovery-required` means a post-publication failure could not durably restore the original name automatically. Stop retries and preserve the hidden reserved entries for recovery; failed new bytes are sanitized and normal listing/search omits the recoverable state.
- `docker-resource-approval-required` means the operation did not receive the exact current single-use `docker-resource-v3` lease. Re-resolve and reclassify the resource instead of replaying a persisted reference.
- Docker requires a pinned local socket/npipe endpoint, stable daemon/container signature, host UID/GID execution, and a successful bidirectional challenge for every bind after container create, start, or reuse. Endpoint changes, remote contexts, Windows hosts, inaccessible mounts, or failed challenge cleanup stop execution.
- Approval retains target-owned handle authority but does not bypass current namespace, mount-root, endpoint, daemon, or container verification.

Local shell/process execution has host-user authority and is outside structured no-follow guarantees. Docker's writable mounts and daemon trust mean it is not a complete sandbox.

### Prompt Or Memory Content Is Missing

- System contributors require non-empty block id/title/content and are deduplicated by source plus block id.
- Optional contributor exceptions are isolated; inspect bounded package diagnostics.
- Optional recall context is suppressed when `ContextPlan.ShouldContribute` is false; host-reserved profile and scoped safety instructions are standing context with separate bounds.
- Context blocks are bounded and serialized in a user-role message. Only host-normalized profile/Files blocks receive standing/scoped authority; contributor-set enums do not.
- Semantic recall works lexically without embeddings; check the profile embedding binding/readiness for vector retrieval.
- The first-party memory package promotes only direct user turns, not assistant/tool claims.

### Scoped `AGENTS.md` Content Is Missing Or A Mutation Is Deferred

- Scoped discovery requires first-party Files composition plus a target implementing both `IAgentScopedInstructionDiscoveryTarget` and `IAgentExecutionScopeProvider`.
- Only exact `AGENTS.md` entries on a configured-root-to-target ancestor chain apply; similarly named files, sibling trees, and approved external paths are intentionally ignored.
- Root instructions are eager even for shell-only profiles. Nested instructions appear only after a structured read/search target or mutation reaches their subtree; listing a parent or naming a path inside arbitrary shell text does not claim children.
- A `files-prompt-context-refresh-required` mutation result means no file operation started. Let the next provider cycle rebuild context, then replan instead of blindly replaying the same call.
- Discovery rejects more than 64 probes per batch, ancestor chains over 64 directories, documents over 12,000 characters, or reserved prompt overflow. Partial policy is never accepted.
- Exact hashes are acknowledged only after final prompt serialization. Required discovery/acknowledgment failure prevents provider progression. Shell mutations do not have the structured Files guarantee.
- Corrupt current claim state is quarantined and rebuilt; future-version state remains untouched and fails closed. Session deletion removes package-owned claims; transcript rollback invalidates old receipts.

### MCP Or OAuth Failure

- Local config needs a command array; remote config needs an absolute HTTP(S) URL.
- Credentialed non-loopback remote endpoints require HTTPS and cannot use URL user-info.
- Server must be enabled and selected as a profile tool group.
- Discovery and tool-call timeouts are separate.
- Interactive OAuth starts only from host callback/settings flow, never background discovery.
- Clear server authorization to remove stale token, dynamic registration, and client-secret caches, then authorize again.
- Host and SDK redirect URIs must match exactly.

### Session Deletion Or Unload Failure

- Register an idempotent `IAgentSessionDataCleaner` for package-local session data.
- Unsubscribe events and stop package-owned background services/processes/connections on deactivation.
- Do not retain contribution instances in static state after catalog removal.
- Cleaner failures remain as ids-only durable jobs with redacted diagnostics. Restore the owning package and stable cleaner id; Runtime retries on startup or package reactivation until the idempotent cleanup succeeds.

### Base Agent Data Migration Refusal

The base Agent database has a forward-only migration ledger with immutable names/checksums. If startup reports an unknown/newer/renamed/checksum-mismatched migration, back up `agent/agent.db` and use an Agent build that recognizes that ledger. Do not edit/delete ledger rows, and do not let an extension open the database directly.

## Diagnostics Policy

Include package/version, Agent family version, provider/model, target kind, stable contribution ids, run/session correlation ids where safe, error codes, and bounded exception metadata. Remove API keys, tokens, OAuth codes/state, prompts, transcript text, memory content, file content, private paths, and remote secret headers before sharing logs.

Return to the [documentation index](README.md).
