# Sunder Agent Package -- AI Agent Rules

This repository contains the public first-party Sunder Agent package family.

## Project Map

- `src/Sunder.Package.Agent` -- main Agent package.
- `src/Sunder.Package.Agent.Builder` -- package project creation, prerequisite, build, and publish package.
- `tests/Sunder.Package.Agent.Tests/Fixtures/PreCanonicalProjects` -- authenticated history for removed App/Contracts/helper projects and wrappers; captured source is evidence, never compiled. Preserve the frozen migration baseline and genuine relocated domain interfaces.
- `src/Sunder.Agent.Execution.Common` -- retained execution support boundary; it is not part of the 26-project retirement set.
- `src/Sunder.Package.Agent.Execution.*` -- execution backends.
- `src/Sunder.Package.Agent.Provider.*` -- model/provider integrations.
- `src/Sunder.Package.Agent.Tools.*` -- tool packages.
- `src/Sunder.Package.Agent.Memory.*` -- memory packages.
- `src/Sunder.Package.Agent.Mcp` -- MCP integration package.
- `src/Sunder.Package.Agent.Skills` -- skills package.
- `src/Sunder.Package.Agent.Subagents` -- subagents package.

## References

Package projects support two development modes:

- In the private Sunder workspace, they use source references to `repos/sunder-core`.
- In a standalone public clone, they use NuGet packages `Sunder.Sdk` and `Sunder.Package.Build`.

Package projects must not reference `Sunder.App` or `Sunder.Runtime.Host`.

## Package Rules

- Runtime package identity is authored with `[assembly: SunderPackage(...)]`.
- Imported package dependencies derive from reviewed API locks; do not duplicate them with authored dependency attributes.
- Package authors do not maintain source `sunder-package.json` files.
- `Sunder.Package.Build` generates manifests, composes `sunder-dev` on build, and emits `.sunderpkg` through direct `PackSunderPackage` on the canonical project.
- Every family has one root, unsuffixed authored Library package project. Core generates isolated exact-RID framework-dependent Worker V1 and optional App roles from that source. Local canonical/source acceptance is complete and the exact 26 obsolete projects are removed and recorded; native/prebuilt, performance and production-release gates remain separate.
- Local unpublished-family development selects validated `.sunderpkg` candidates and a local Core catalog without a Registry; local selections omit `registryOrigin`. Published dependency selection uses an explicit pinned Registry. Publication evidence is separate.
- `packages.json` contains exactly 15 canonical archive entries. Native fan-in must preserve tested/prebuilt role bytes through Core's qualified role-input evidence and explicit selection; it must not restore, compile or rebuild those inputs. See `docs/native-release-inputs.md` and `docs/cold-release-inputs.md`.

## Build And Test

```powershell
dotnet build Sunder.AgentPackage.slnx --no-restore
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj --no-restore
```

These are full-project checks, not the default inner loop. During iteration, filter
the changed class/method in its owning test project and add only relevant consumer/
security regressions. Run the full affected project at a coherent integration point.
The required all-family source-artifact migration lane builds fifteen families and
belongs at migration checkpoints or shared generator/contract changes; do not repeat
it after every local edit. Keep its required-input and final acceptance gates intact.
