# Release, Compatibility, And Troubleshooting

This guide is for extension authors. Maintainers releasing the complete first-party Agent family should also follow [`RELEASES.md`](RELEASES.md).

## Compatibility Contract

Agent 1.1 is a clean break from the unused 1.0 public line. There are no 1.0 compatibility shims.

Use these coordinated ranges:

```xml
<PackageReference Include="Sunder.Sdk" Version="[1.1.0,1.2.0)" />
<PackageReference Include="Sunder.Package.Build" Version="[1.1.0,1.2.0)" PrivateAssets="all" />
<PackageReference Include="Sunder.Package.Agent.Contracts" Version="[1.1.0,1.2.0)" />
```

```csharp
[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=1.1.0 <1.2.0")]
```

NuGet and Sunder Runtime ranges use different syntax. Both accept compatible 1.1 patches and reject 1.2. The stable lower bound excludes `1.1.0-*` prereleases.

Do not use an unbounded `>=1.1.0` runtime dependency. A future minor may intentionally change the contracts or behavioral invariants even when binary loading appears possible.

## Versioning An Extension

An independent extension can use its own package version, but every released build should record which Agent minor it targets:

- Patch release: fixes behavior without changing extension package contracts or Agent range.
- Minor release: adds backward-compatible extension features while retaining the same Agent range.
- Major release: breaks the extension's own persisted/public behavior.
- Agent-minor migration: compile against the new contracts and change both NuGet and runtime upper/lower bounds in a deliberate release.

Do not publish different bytes for an existing package id/version. Sunder package versions are immutable artifacts.

## Contracts Surface

For this repository, `src/Sunder.Package.Agent.Contracts/PublicAPI.Shipped.txt` is the immutable stable API ledger. `PublicAPI.Unshipped.txt` tracks reviewed changes before the next stable boundary. Stable family releases require no unshipped entries.

Extension authors should:

- Compile against public contract types only.
- Avoid reflection into concrete first-party assemblies.
- Treat optional interfaces as capability checks.
- Persist stable ids rather than CLR type/assembly names.
- Handle an optional contribution disappearing on package deactivation.
- Rebuild and retest for each supported Agent minor.

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

The first-party family releases all 15 runtime packages and `Sunder.Package.Agent.Contracts` from one Agent commit against one Core `main` commit resolved at workflow start. Third-party extensions should not assume a mixed family patch set is valid.

## Troubleshooting

### Restore Or Compile Failure

| Symptom | Check |
| --- | --- |
| Contracts types missing | Directly reference `Sunder.Package.Agent.Contracts` with `[1.1.0,1.2.0)`. Use namespaces `.Contracts`, `.Models`, and root `PackageExtensionPoints`. |
| SDK/module types missing | Add a direct `Sunder.Sdk` reference; do not rely on the contracts package's transitive dependency. |
| Manifest/build targets missing | Add `Sunder.Package.Build` with `PrivateAssets="all"`. |
| Mixed package downgrade/conflict | Inspect transitive packages and align all coordinated Sunder references to `[1.1.0,1.2.0)`. |
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

### Contribution Is Missing

- Confirm `ConfigureRuntimeServices` registered the service.
- Confirm `RegisterRuntimeContributions` registered that same DI instance on the intended `PackageExtensionPoints` property.
- Confirm the code is in `ISunderRuntimePackageModule`, not only `ISunderAppPackageModule`.
- For App presentation, confirm the inverse: the App contribution is registered in the App role and calls Runtime through typed operations.
- Confirm stable semantic ids do not collide with another active contribution.
- Use `GetExtensionContributions` in a diagnostic fixture to verify owner and active instance.

Runtime and App catalogs are intentionally separate.

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
- Outside-scope paths require canonical target classification and approval.
- Approval is tied to session/run revision/tool call. A superseding run makes it stale.
- Unrestricted Mode affects the current session tree but is not operating-system isolation.

### Execution Path Failure

- Verify the workspace has a current enabled primary execution binding.
- `ContributionId` must match the target `TargetId` or `TargetKind`.
- Check target readiness and configured roots.
- Use execution paths inside containers/remotes, not host paths.
- Resolve physical symlinks/reparse points and sibling-prefix edge cases.
- Approval does not bypass a target's final canonicalization/revalidation.

Local execution has host-user authority. Docker's writable mounts and daemon trust mean it is not a complete sandbox.

### Prompt Or Memory Content Is Missing

- System contributors require non-empty block id/title/content and are deduplicated by source plus block id.
- Optional contributor exceptions are isolated; inspect bounded package diagnostics.
- Prompt context is suppressed when `ContextPlan.ShouldContribute` is false.
- Context blocks are bounded, ordered, and serialized as user-role reference data, never system instructions.
- Semantic recall works lexically without embeddings; check the profile embedding binding/readiness for vector retrieval.
- The first-party memory package promotes only direct user turns, not assistant/tool claims.

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
- Cleaner failures are collected; inspect the owning cleaner id and retry only package-owned cleanup.

### Base Agent Data Migration Refusal

The base Agent database has a forward-only migration ledger with immutable names/checksums. If startup reports an unknown/newer/renamed/checksum-mismatched migration, back up `agent/agent.db` and use an Agent build that recognizes that ledger. Do not edit/delete ledger rows, and do not let an extension open the database directly.

## Diagnostics Policy

Include package/version, Agent family version, provider/model, target kind, stable contribution ids, run/session correlation ids where safe, error codes, and bounded exception metadata. Remove API keys, tokens, OAuth codes/state, prompts, transcript text, memory content, file content, private paths, and remote secret headers before sharing logs.

Return to the [documentation index](README.md).
