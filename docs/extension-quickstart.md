# Extension Quickstart

This quickstart creates a headless Runtime package with one fixed, read-only Agent tool. The compiled version is in [`samples/Sunder.Agent.Extension.Minimal`](../samples/Sunder.Agent.Extension.Minimal).

## 1. Create The Project

Start with `dotnet new sunder-package`. The template creates an aggregate package with a non-packable package-local Protocol project for protocols owned by your package. Add the Agent Protocol reference and provider declaration to the Runtime leaf:

```xml
<ItemGroup>
  <PackageReference Include="Sunder.Sdk" Version="[1.1.0,1.2.0)" />
  <PackageReference Include="Sunder.Package.Build" Version="[1.1.0,1.2.0)" PrivateAssets="all" />
  <PackageReference Include="Sunder.Package.Agent.Protocol" Version="[2.0.0,3.0.0)" />
</ItemGroup>

<ItemGroup>
  <SunderRpcProvider Include="acme.tools"
                     ContractId="sunder.agent.tool.source"
                     ContractVersion="1.0.0"
                     Role="runtime" />
</ItemGroup>
```

`Sunder.Package.Agent.Protocol` supplies the local descriptors required by `Sunder.Package.Build`; descriptor validation never depends on another installed package.

The NuGet package and assembly identity are both `Sunder.Package.Agent.Protocol`. Inside this repository, the source project path and some stable public namespaces retain `Sunder.Package.Agent.Contracts`; those names organize package-local implementation interfaces and existing API types inside the Protocol artifact. They are not a separate NuGet package and do not create shared CLR identity between Sunder packages. Generated wire types use `Sunder.Package.Agent.Protocol.Generated.*`.

## 2. Declare Runtime Compatibility

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "com.acme.sunder.agent.tools",
    Name = "Acme Agent Tools",
    Summary = "Adds Acme tools to Sunder Agent.")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=2.0.0 <3.0.0")]
```

Do not maintain a source `sunder-package.json`; the build generates it from compiled metadata and MSBuild RPC items.

## 3. Publish The Provider

Register services first, then publish an activation-owned RPC handler:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<CurrentUtcTimeTool>();
        services.AddSingleton(provider => new AgentStaticToolSourceAdapter(
            "acme-tools",
            "Acme tools",
            "acme",
            [provider.GetRequiredService<CurrentUtcTimeTool>()]));
    }

    public void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services)
        => registry.RegisterRpcProvider(
            "acme.tools",
            AgentToolSourceRpc.CreateHandler(
                services.GetRequiredService<AgentStaticToolSourceAdapter>()));
}
```

The provider id must match the `SunderRpcProvider` item. The Host stamps ownership and caller identity, validates every request and response against `tool-source.rpc.json`, and retires the exact endpoint when the package activation ends. The local implementation interface and adapter never cross the package boundary.

## 4. Build And Inspect

```powershell
dotnet restore
dotnet build
dotnet publish -c Release
```

Build emits `sunder-dev`; publish emits a versioned `.sunderpkg`. Confirm the generated manifest contains the Agent 2.x runtime dependency, the `sunder.agent.tool.source` contract bundle, and the `acme.tools` Runtime provider.

## 5. Choose Another Contract

- `sunder.agent.chat.provider` and `sunder.agent.embedding.provider`: model backends.
- `sunder.agent.tool.source`: fixed or dynamic tools.
- `sunder.agent.execution.target`: shell, file, and resource execution.
- `sunder.agent.permission.surface`: user-configurable actions and boundaries.
- `sunder.agent.system.prompt.contributor`: trusted package-owned instructions.
- `sunder.agent.prompt.context.contributor`: recalled or external reference context.
- `sunder.agent.durable.lifecycle.observer`: replayable package-local effects.

Read the [RPC contract catalog](extension-points.md) before publishing a base-Agent-owned service port.
