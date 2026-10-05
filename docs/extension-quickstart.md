# Extension Quickstart

> **Pre-cutover guide:** this currently exercises the descriptor-first migration
> tree. Do not use the Protocol package/project shape as a final V1 template;
> implementation-first generation replaces it before public release. See
> [`../compatibility/README.md`](../compatibility/README.md).

This quickstart creates a headless Runtime package with one fixed, read-only Agent tool.

## 1. Create The Project

Start with `dotnet new sunder-package`. The template creates a root, unsuffixed canonical Worker/package project with `SunderPackagePrimaryProject=true`, a non-packable package-local Protocol project, and an optional App project. The App is selected through `SunderPackageAppProject` as a build-only input. Add the Agent Protocol reference and provider declaration to the canonical Worker project:

```xml
<ItemGroup>
  <PackageReference Include="Sunder.Sdk.Worker" Version="[1.1.0,1.2.0)" />
  <PackageReference Include="Sunder.Package.Build" Version="[1.1.0,1.2.0)" PrivateAssets="all" />
  <PackageReference Include="Sunder.Package.Agent.Protocol" Version="[2.0.0,3.0.0)" />
</ItemGroup>

<ItemGroup>
  <SunderPackageTarget Include="runtime/$(RuntimeIdentifier)"
                       Role="runtime"
                       Rid="$(RuntimeIdentifier)"
                       Kind="worker" />
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

## 3. Run The Provider Worker

Create the implementation and return its activation-owned RPC handler from the canonical worker entry point:

```csharp
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

await SunderWorker.RunAsync(_ =>
{
    var source = new AgentStaticToolSourceAdapter(
        "acme-tools",
        "Acme tools",
        "acme",
        [new CurrentUtcTimeTool()]);
    var descriptor = AgentRpcContractDescriptors.Get(AgentRpcContractIds.ToolSource);
    return new SunderWorkerOptions(
    [
        new SunderWorkerProviderRegistration(
            "acme.tools",
            descriptor.ContractId,
            descriptor.Version,
            descriptor.Sha256,
            descriptor.AbiSha256 ?? throw new InvalidDataException("Agent tool RPC is missing its ABI hash."),
            AgentToolSourceRpc.CreateHandler(source)),
    ]);
});
```

The provider id must match the `SunderRpcProvider` item. The Host stamps ownership and caller identity, validates every request and response against `tool-source.rpc.json`, and retires the exact endpoint when the package activation ends. The local implementation interface and adapter never cross the package boundary.

## 4. Build And Inspect

```powershell
dotnet restore
dotnet build
dotnet msbuild path/to/Acme.Package.csproj -t:PackSunderPackage -p:Configuration=Release
```

A normal build emits the exact current-RID Worker leaf and composes the canonical non-RID `bin/<configuration>/<tfm>/sunder-dev`. `PackSunderPackage` runs directly on the same primary Worker project, builds all configured RIDs, includes the optional App, and emits one universal versioned `.sunderpkg`. Confirm the generated manifest contains the Agent 2.x runtime dependency, the `sunder.agent.tool.source` contract bundle, and the `acme.tools` Runtime provider.

## 5. Choose Another Contract

- `sunder.agent.chat.provider` and `sunder.agent.embedding.provider`: model backends.
- `sunder.agent.tool.source`: fixed or dynamic tools.
- `sunder.agent.execution.target`: shell, file, and resource execution.
- `sunder.agent.permission.surface`: user-configurable actions and boundaries.
- `sunder.agent.system.prompt.contributor`: trusted package-owned instructions.
- `sunder.agent.prompt.context.contributor`: recalled or external reference context.
- `sunder.agent.durable.lifecycle.observer`: replayable package-local effects.

Read the [RPC contract catalog](extension-points.md) before publishing a base-Agent-owned service port.
