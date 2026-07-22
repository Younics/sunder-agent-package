# Testing Agent Extensions

Test at three levels: contract behavior, package composition, and generated artifact/runtime lifecycle. A project compiling is necessary but does not prove role placement, package dependency metadata, permissions, or unload safety.

## Compile The Minimal Sample

From this repository:

```powershell
dotnet restore samples/Sunder.Agent.Extension.Minimal/Sunder.Agent.Extension.Minimal.csproj
dotnet build samples/Sunder.Agent.Extension.Minimal/Sunder.Agent.Extension.Minimal.csproj --no-restore
```

The project is intentionally outside `Sunder.AgentPackage.slnx` because it is not a first-party release artifact. Its normal build is a compile fixture. To exercise generated manifest/archive output without adding the sample to the coordinated repository inventory, opt in for that command only:

```powershell
dotnet restore samples/Sunder.Agent.Extension.Minimal/Sunder.Agent.Extension.Minimal.csproj -p:BuildAuthorSamplePackage=true
dotnet publish samples/Sunder.Agent.Extension.Minimal/Sunder.Agent.Extension.Minimal.csproj -c Release --no-restore -p:BuildAuthorSamplePackage=true
```

## Unit Tests

Instantiate the public contract directly with deterministic fakes for package context, storage, secrets, HTTP/process backends, and time where applicable.

### Common Assertions

- Descriptor and source ids are stable, non-empty, and case-insensitively unique.
- Expected missing configuration returns readiness, not an exception.
- Cancellation is rethrown and no success state is persisted after cancellation.
- Input limits, malformed JSON, duplicate/case-colliding properties, and boundary values are rejected.
- Expected failures use stable result/error codes and bounded safe messages.
- No credential, prompt, private content, or unrestricted payload reaches logs/results.
- Repeated calls and retries are idempotent where the host may replay work.

For a tool, test descriptor, readiness, valid execution, every validation error, permission request, result bounds, and cancellation independently. For a provider, test model/readiness catalogs and the complete response translation without live credentials.

## Composition Tests

Create a service collection, call `ConfigureRuntimeServices`, build the provider, and pass a capture implementation of `ISunderRuntimeContributionRegistry` to `RegisterRuntimeContributions`.

Assert:

- The intended extension-point id and concrete service instance are registered exactly once.
- The instance is the DI-owned singleton, not a second allocation.
- Settings schema, operations, streams, callbacks, and background services are registered in the correct role.
- Runtime authority is absent from App services/registrations.
- App presentation and typed Runtime clients are absent from headless Runtime composition.

If a test fake implements `IPackageExtensionCatalog`, implement both methods:

```csharp
public IReadOnlyList<T> GetExtensions<T>(PackageExtensionPoint<T> point) => ...;

public IReadOnlyList<PackageExtensionContribution<T>> GetExtensionContributions<T>(
    PackageExtensionPoint<T> point)
    => GetExtensions(point)
        .Select(value => new PackageExtensionContribution<T>("test.package", value))
        .ToArray();
```

Use canonical lowercase test package ids because owner validation is part of the contract.

## Capability Test Matrix

| Capability | Required scenarios |
| --- | --- |
| Chat provider | Model/readiness states, auth modes, text and tool streams, malformed upstream payload, timeout/transient failure, cancellation, option/attachment translation |
| Embedding provider | Empty/mixed batches, index preservation, dimensions, model identity, malformed counts/indexes, readiness, cancellation |
| Static tool | Profile assignment, readiness, strict arguments, expected failure results, permission policy, concurrency claim |
| Dynamic source | Context-dependent discovery, invalidation, identity stability, stale advertisement denial, native declaration parity |
| Permission surface | Every action/boundary/default, unknown boundary behavior, canonical resource summary, session approval and stale-run safety |
| Execution target | Path/symlink containment, read ranges, output bounds, compare-and-swap writes/deletes, process arguments, timeout/tree cleanup, optional interface conformance |
| Prompt contributor | Dedup identity, ordering/limits, no user/dynamic text in system blocks, cancellation and isolated failure |
| Context contributor | Plan suppression, entry/character bounds, accurate provenance/trust, malicious embedded instructions remain data |
| Lifecycle observer | All relevant events, idempotency, partial failure, queue saturation, cancellation, direct-user-only promotion |
| Behavior loop | Run revision ownership, checkpoint/completion consistency, provider/tool cycles, approval suspension, no-progress/budget limits, cancellation |
| Child runs | Parent correlation, task retries, workspace/profile snapshot, permission inheritance, wait/resume/restart, superseding run |
| MCP integration | Local/remote parsing, secret separation, transport policy, discovery/result bounds, OAuth callback/session validation, cleanup |

## Artifact Validation

Build and publish the actual package project:

```powershell
dotnet restore path/to/Extension.csproj
dotnet build path/to/Extension.csproj --no-restore
dotnet publish path/to/Extension.csproj -c Release --no-restore
```

Inspect generated `sunder-dev/sunder-package.json` and the `.sunderpkg`:

- Correct package id/name/version.
- Runtime dependency `sunder.package.agent` with `>=1.1.0 <1.2.0`.
- No source-authored manifest overriding generated metadata.
- Expected entry assembly and assets.
- Required inferred capabilities and no accidental capabilities from wrong-role code.
- No SDK assemblies, test binaries, credentials, local config, or private files.

Validate both source-SDK mode in the private workspace and NuGet mode in an isolated standalone checkout before release.

## Runtime Lifecycle Smoke

Use clean host state and test:

1. Install base Agent and all required extension dependencies.
2. Install and activate the extension.
3. Discover/configure/select its capability.
4. Exercise one successful and one expected-failure path.
5. Deactivate/unload the extension graph.
6. Confirm background work, processes, connections, events, and callbacks stop cleanly.
7. Reactivate and verify durable state.
8. Reinstall the same immutable version and repeat activation.
9. Delete a related Agent session and verify package-local cleanup.

If the extension has an App module, also validate App snapshot activation and behavior while Runtime is temporarily unavailable.

## Repository Validation

For changes to this first-party family:

```powershell
dotnet restore Sunder.AgentPackage.slnx
dotnet build Sunder.AgentPackage.slnx --no-restore
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj --no-restore
```

Run the narrow project first, then the solution and relevant security/architecture suites. Execution changes should include `Sunder.Package.Agent.Execution.Local.Tests`; UI changes should include `Sunder.Package.Agent.Presentation.Tests`; provider changes should include that provider's project.

## Test Hygiene

- Never require a developer's real API key, OAuth cache, Docker image mutation, MCP server, or home-directory state.
- Use temporary directories and delete only test-owned paths.
- Do not weaken TLS/path/permission policy to simplify a fixture.
- Bound waits and process output; kill only processes created by the test.
- Avoid ordering assertions based on package activation when the contract uses semantic ids/sorting.
- Include package unload/disposal tests for event subscriptions and background services.

Next: [Release, compatibility, and troubleshooting](release-compatibility-troubleshooting.md).
