# Agent Extension Author Guide

This is the canonical guide for packages that integrate with `sunder.package.agent` 2.x through the schema-first contracts in `Sunder.Package.Agent.Protocol`.

Use the Protocol package and the Sunder SDK only. Extensions must not reference `Sunder.Package.Agent`, `Sunder.App`, or `Sunder.Runtime.Host` implementation assemblies. Runtime capabilities are declared in manifests and published only as schema-validated RPC providers; package-local CLR objects never cross an activation boundary.

## Start Here

| Goal | Guide |
| --- | --- |
| Create and build a first extension | [Extension quickstart](extension-quickstart.md) |
| Choose an RPC contract | [RPC contract catalog](extension-points.md) |
| Implement chat or embedding models | [Chat and embedding providers](providers.md) |
| Publish tools and permission surfaces | [Tools and permissions](tools-and-permissions.md) |
| Add an execution target | [Execution targets, paths, and security](execution-targets.md) |
| Add prompt context or lifecycle handling | [Prompts, context, lifecycle, and trust](prompts-context-lifecycle.md) |
| Customize orchestration or child runs | [Behavior loops and subagents](behavior-loops-and-subagents.md) |
| Test an extension | [Testing extensions](testing-extensions.md) |
| Release or troubleshoot a package | [Release, compatibility, and troubleshooting](release-compatibility-troubleshooting.md) |


## Public API Map

| Namespace | Contents |
| --- | --- |
| `Sunder.Package.Agent.Protocol` | Contract ids, discovery/catalog adapters, bounded wire adapters, and embedded descriptors |
| `Sunder.Package.Agent.Protocol.Generated.*` | Deterministically generated DTO, client, and provider ABI for each descriptor |
| `Sunder.Package.Agent.Contracts.Contracts` | Local implementation interfaces accepted by the first-party RPC adapters |
| `Sunder.Package.Agent.Contracts.Models` | Immutable domain requests, descriptors, results, and enums used by those adapters |
| `Sunder.Sdk.Rpc` | Host RPC registration, discovery, invocation, endpoint, and error contracts |

Most Agent capabilities belong in an `ISunderRuntimePackageModule`. Register an activation-owned handler with `ISunderRuntimeContributionRegistry.RegisterRpcProvider`. Add an `ISunderAppPackageModule` only for presentation or an App-side proxy over Runtime operations.

`Sunder.Package.Agent.Protocol` is the shipped NuGet and assembly identity. The repository retains source folder `src/Sunder.Package.Agent.Contracts` and the `Sunder.Package.Agent.Contracts.*` namespaces for the package-local implementation interfaces and stable API types already carried by that artifact. The path and namespace do not identify another package or a cross-package CLR ABI; all 18 cross-package boundaries are the descriptors and generated bindings under the Protocol identity.

## Compatibility Baseline

| Dependency kind | Required range |
| --- | --- |
| NuGet: `Sunder.Package.Agent.Protocol` | `[2.0.0,3.0.0)` |
| Runtime package: `sunder.package.agent` | `>=2.0.0 <3.0.0` |
| NuGet: `Sunder.Sdk`, `Sunder.Package.Build` | The independent Core SDK minor supported by the Protocol package |

Contract descriptors have independent versions, currently `1.0.0`. Package-family versions and descriptor versions serve different compatibility boundaries.
