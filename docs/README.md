# Agent Extension Author Guide

This documentation is the canonical author guide for packages that extend `sunder.package.agent` through `Sunder.Package.Agent.Contracts`. It describes the 1.1 contract line implemented in this repository.

Use only the public types in `Sunder.Package.Agent.Contracts` and the Sunder SDK. An extension package must not reference `Sunder.Package.Agent`, `Sunder.App`, or `Sunder.Runtime.Host` implementation assemblies.

## Start Here

| Goal | Guide |
| --- | --- |
| Create and build a first extension | [Extension quickstart](extension-quickstart.md) |
| Choose an extension point | [Extension-point catalog](extension-points.md) |
| Implement chat or embedding models | [Chat and embedding providers](providers.md) |
| Publish fixed or discovered tools | [Static and dynamic tools, permissions](tools-and-permissions.md) |
| Add a local, container, or remote executor | [Execution targets, paths, and security](execution-targets.md) |
| Add instructions, reference context, or event observers | [Prompts, context, lifecycle, and trust](prompts-context-lifecycle.md) |
| Integrate durable recall | [Semantic memory](semantic-memory.md) |
| Customize orchestration or launch child sessions | [Behavior loops, child runs, and subagents](behavior-loops-and-subagents.md) |
| Configure MCP tools and browser authorization | [MCP and OAuth](mcp-and-oauth.md) |
| Understand repository and runtime boundaries | [Package family architecture](package-family-architecture.md) |
| Test an extension | [Testing extensions](testing-extensions.md) |
| Ship safely or diagnose activation/runtime failures | [Release, compatibility, and troubleshooting](release-compatibility-troubleshooting.md) |

The compiled minimal example is [`samples/Sunder.Agent.Extension.Minimal`](../samples/Sunder.Agent.Extension.Minimal/README.md).

## Public API Map

| Namespace | Contents |
| --- | --- |
| `Sunder.Package.Agent.Contracts` | `PackageExtensionPoints` |
| `Sunder.Package.Agent.Contracts.Contracts` | Extension interfaces and host service ports |
| `Sunder.Package.Agent.Contracts.Models` | Immutable requests, descriptors, results, and enums |
| `Sunder.Package.Agent.Contracts.Services` | Public catalog observation helper |
| `Sunder.Sdk.*` | Package modules, activation context, contribution registries, settings, secrets, callbacks, logging, and package metadata |

`PackageExtensionPoints` are role-local. A Runtime registration is visible only in the Runtime extension catalog; an App registration is visible only in the App extension catalog. Most Agent capabilities belong in an `ISunderRuntimePackageModule`. Add an `ISunderAppPackageModule` only for presentation or an App-side proxy over typed Runtime operations.

## Compatibility Baseline

The coordinated 1.1 ranges are:

| Dependency kind | Required range |
| --- | --- |
| NuGet: `Sunder.Sdk`, `Sunder.Package.Build`, `Sunder.Package.Agent.Contracts` | `[1.1.0,1.2.0)` |
| Runtime package: `sunder.package.agent` | `>=1.1.0 <1.2.0` |

These are different range syntaxes for different dependency systems. Do not copy the NuGet syntax into `SunderPackageDependency`, and do not use the Sunder runtime syntax in a `PackageReference`.

For family release operations, see [Agent family releases](RELEASES.md). For the deliberate 1.1 break from 1.0, see [Agent 1.1 breaking baseline](1.1-BREAKING-BASELINE.md).
