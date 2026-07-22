# Extension Quickstart

This quickstart creates a headless Runtime package with one fixed, read-only Agent tool. The complete compiled version is in [`samples/Sunder.Agent.Extension.Minimal`](../samples/Sunder.Agent.Extension.Minimal).

## 1. Generate The Package

Install a 1.1 `Sunder.Package.Templates` release and generate against the Agent host contracts:

```powershell
dotnet new install Sunder.Package.Templates::1.1.0
dotnet new sunder-package --name AcmeAgentTools --packageId com.acme.sunder.agent.tools --packageName "Acme Agent Tools" --withHostContracts --hostPackageId sunder.package.agent --hostPackageVersionRange ">=1.1.0 <1.2.0" --hostContractsPackageId Sunder.Package.Agent.Contracts --hostContractsVersionRange "[1.1.0,1.2.0)"
```

The template adds `Sunder.Sdk`, `Sunder.Package.Build`, the Agent contracts reference, package metadata, and the host runtime dependency. Delete generated placeholder state or integration stubs that the extension does not use.

For a manually authored project, the relevant package references are:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Sunder.Sdk" Version="[1.1.0,1.2.0)" />
    <PackageReference Include="Sunder.Package.Build" Version="[1.1.0,1.2.0)" PrivateAssets="all" />
    <PackageReference Include="Sunder.Package.Agent.Contracts" Version="[1.1.0,1.2.0)" />
  </ItemGroup>
</Project>
```

Keep direct references to all three packages. The contracts package's transitive dependencies are not a replacement for a direct SDK reference, and build tooling must remain `PrivateAssets="all"`.

## 2. Declare Identity And Runtime Compatibility

Author identity in assembly attributes. Do not create or maintain a source `sunder-package.json` file.

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "com.acme.sunder.agent.tools",
    Name = "Acme Agent Tools",
    Summary = "Adds Acme tools to Sunder Agent.")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=1.1.0 <1.2.0")]
```

`Sunder.Package.Build` generates the manifest from compiled metadata and validates the package. The bounded minor dependency prevents an extension compiled for 1.1 contracts from activating against an incompatible Agent minor.

## 3. Implement And Register A Capability

Register Runtime services first, then publish their activation-scoped instances in `RegisterRuntimeContributions`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Sdk.Abstractions;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
        => services.AddSingleton<CurrentUtcTimeTool>();

    public void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services)
        => registry.RegisterExtension(
            PackageExtensionPoints.Tools,
            services.GetRequiredService<CurrentUtcTimeTool>());
}
```

The sample's [`CurrentUtcTimeTool.cs`](../samples/Sunder.Agent.Extension.Minimal/CurrentUtcTimeTool.cs) shows the complete `IAgentTool` implementation. Use a globally stable `ToolId`, return explicit readiness, honor cancellation, validate all arguments, and return expected failures as `AgentToolResult` values.

Do not instantiate a second contribution in `RegisterRuntimeContributions`. Register the same DI-owned object so its state and disposal follow the package activation lifetime.

## 4. Build And Inspect

```powershell
dotnet restore
dotnet build
dotnet publish -c Release
```

Build emits a generated development package under `bin/<configuration>/net10.0/sunder-dev`. Publish emits a versioned `.sunderpkg` beside the publish output. Inspect the generated `sunder-package.json` and confirm that it contains:

- Package id `com.acme.sunder.agent.tools`.
- Runtime dependency `sunder.package.agent` with `>=1.1.0 <1.2.0`.
- The capabilities inferred from the module's SDK calls.

## 5. Choose The Next Contract

- Use `PackageExtensionPoints.Tools` for a small fixed set of process-local tools.
- Use `PackageExtensionPoints.ToolSources` when tools depend on a session, profile, workspace, server discovery, or changing external state.
- Use `ChatProviders` or `EmbeddingProviders` for model backends.
- Use `ExecutionTargets` for shell and file execution environments.
- Use `SystemPromptContributors` only for trusted package-owned instructions.
- Use `PromptContextContributors` for recalled, external, transcript-derived, tool-produced, or otherwise lower-trust data.

Read the [extension-point catalog](extension-points.md) before registering a service port that is owned by the base Agent package.

## Dependency Range Rules

| Reference | Correct | Incorrect examples |
| --- | --- | --- |
| NuGet packages | `[1.1.0,1.2.0)` | `1.*`, `>=1.1.0`, `[1.1.0]` |
| Agent runtime dependency | `>=1.1.0 <1.2.0` | `[1.1.0,1.2.0)`, `>=1.1.0` |

The stable lower bound intentionally excludes `1.1.0-*` prereleases. Test prerelease builds only in a coordinated environment with explicit matching versions; do not broaden a published stable extension's range to make a preview install resolve.

Next: [Testing extensions](testing-extensions.md) and [Release, compatibility, and troubleshooting](release-compatibility-troubleshooting.md).
