# Sunder Agent Package

This repository contains the public first-party Agent package family for Sunder.

## Build

```powershell
dotnet restore Sunder.AgentPackage.slnx
dotnet build Sunder.AgentPackage.slnx --no-restore
```

When cloned inside the private `sunder` workspace, package projects use local `sunder-core` source references. When cloned standalone, they use the public `Sunder.Sdk` and `Sunder.Package.Build` NuGet packages.

## Tests

```powershell
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj --no-restore
```
