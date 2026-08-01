# Minimal Agent Extension

This is the smallest compiled Agent extension in the repository. It exposes one fixed, read-only `IAgentTool` through the schema-first `sunder.agent.tool.source` RPC contract.

The project deliberately stays out of `Sunder.AgentPackage.slnx` and does not permanently opt into the repository's central package-build mode because it is an author sample, not one of the coordinated first-party release artifacts in `packages.json`.

Build it from the repository root:

```powershell
dotnet restore samples/Sunder.Agent.Extension.Minimal/Sunder.Agent.Extension.Minimal.csproj
dotnet build samples/Sunder.Agent.Extension.Minimal/Sunder.Agent.Extension.Minimal.csproj --no-restore
```

To validate generated package output inside this repository, opt in for that command only:

```powershell
dotnet restore samples/Sunder.Agent.Extension.Minimal/Sunder.Agent.Extension.Minimal.csproj -p:BuildAuthorSamplePackage=true
dotnet publish samples/Sunder.Agent.Extension.Minimal/Sunder.Agent.Extension.Minimal.csproj -c Release --no-restore -p:BuildAuthorSamplePackage=true
```

Repository builds use the local Agent Protocol project and the repository's source/NuGet SDK switching. For an external extension project, use the bounded package references in [`docs/extension-quickstart.md`](../../docs/extension-quickstart.md).
