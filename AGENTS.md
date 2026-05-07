# Sunder Agent Package -- AI Agent Rules

This repository contains the public first-party Sunder Agent package family.

## Project Map

- `src/Packages/Sunder.Package.Agent` -- main Agent package.
- `src/Packages/Sunder.Package.Agent.Contracts` -- public contracts used by Agent extension packages.
- `src/Packages/Sunder.Package.Agent.Execution.*` -- execution backends.
- `src/Packages/Sunder.Package.Agent.Provider.*` -- model/provider integrations.
- `src/Packages/Sunder.Package.Agent.Tools.*` -- tool packages.
- `src/Packages/Sunder.Package.Agent.Memory.*` -- memory packages.
- `src/Packages/Sunder.Package.Agent.Mcp` -- MCP integration package.
- `src/Packages/Sunder.Package.Agent.Skills` -- skills package.
- `src/Packages/Sunder.Package.Agent.Subagents` -- subagents package.

## References

Package projects support two development modes:

- In the private Sunder workspace, they use source references to `repos/sunder-core`.
- In a standalone public clone, they use NuGet packages `Sunder.Sdk` and `Sunder.Package.Build`.

Package projects must not reference `Sunder.App` or `Sunder.Runtime.Host`.

## Package Rules

- Runtime package identity is authored with `[assembly: SunderPackage(...)]`.
- Runtime package dependencies are authored with `[assembly: SunderPackageDependency(...)]`.
- Package authors do not maintain source `sunder-package.json` files.
- `Sunder.Package.Build` generates manifests, emits `sunder-dev` on build, and emits `.sunderpkg` on publish.
- Package modules implement `ISunderPackageModule` and register services/contributions through SDK APIs.

## Build And Test

```powershell
dotnet build Sunder.AgentPackage.slnx --no-restore
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj --no-restore
```
