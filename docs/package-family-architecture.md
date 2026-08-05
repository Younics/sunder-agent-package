# Package Family Architecture

Sunder Agent is a package family, not one monolithic plugin. The base package owns durable Agent state and orchestration; independently installed packages provide tools, execution, memory, MCP, skills, and subagents through the 18 public RPC descriptors.

## Layering

```text
Sunder Runtime / Sunder App
  |
  +-- Sunder.Sdk (+ optional Avalonia/Stacks SDKs)
  |
  +-- sunder.package.agent
  |     sessions, profiles, workspaces, transcripts, permissions, runs
  |     publishes base service ports through Agent contracts
  |
  +-- Agent extension packages
        consume Sunder.Package.Agent.Protocol
        publish RPC providers for tools/execution/context/orchestration
```

An extension compiles against:

- `Sunder.Sdk` for package activation and host capabilities.
- `Sunder.Package.Build` for generated manifests and archives.
- `Sunder.Package.Agent.Protocol` for bundled descriptors and generated wire bindings.

It declares a runtime dependency on `sunder.package.agent`. It must not reference `Sunder.Package.Agent`, `Sunder.App`, or `Sunder.Runtime.Host` implementation assemblies.

## Family Inventory

| Artifact | Runtime package id | Responsibility |
| --- | --- | --- |
| `Sunder.Package.Agent.Protocol` | Not a runtime package | Public RPC descriptors and generated bindings |
| `Sunder.Package.Agent` | `sunder.package.agent` | Core profiles, workspaces, sessions, transcript, attachments, permissions, runs, context projection, default loop |
| `Sunder.Package.Agent.Builder` | `sunder.package.agent.builder` | Package project creation, prerequisite setup, build, and publish |
| `Sunder.Package.Agent.Provider.OpenAI` | `sunder.package.agent.provider.openai` | OpenAI chat and embeddings |
| `Sunder.Package.Agent.Provider.Anthropic` | `sunder.package.agent.provider.anthropic` | Anthropic chat |
| `Sunder.Package.Agent.Provider.Gemini` | `sunder.package.agent.provider.gemini` | Gemini chat and embeddings |
| `Sunder.Package.Agent.Provider.LMStudio` | `sunder.package.agent.provider.lmstudio` | Local LM Studio chat and embeddings |
| `Sunder.Package.Agent.Execution.Local` | `sunder.package.agent.execution.local` | Host-user local shell/files/process execution |
| `Sunder.Package.Agent.Execution.Docker` | `sunder.package.agent.execution.docker` | Resource-bounded Docker execution |
| `Sunder.Package.Agent.Tools.Files` | `sunder.package.agent.tools.files` | Workspace file tools and permission surface |
| `Sunder.Package.Agent.Tools.Shell` | `sunder.package.agent.tools.shell` | Workspace shell tool and permission surface |
| `Sunder.Package.Agent.Tools.Web` | `sunder.package.agent.tools.web` | Hardened web fetch/search tools |
| `Sunder.Package.Agent.Mcp` | `sunder.package.agent.mcp` | MCP discovery, tools, configuration, OAuth, and stacks |
| `Sunder.Package.Agent.Memory.Semantic` | `sunder.package.agent.memory.semantic` | Durable semantic memory and inspector |
| `Sunder.Package.Agent.Skills` | `sunder.package.agent.skills` | Reusable profile-selected skill instructions/tools |
| `Sunder.Package.Agent.Subagents` | `sunder.package.agent.subagents` | Subagent profiles, delegation tools, child sessions, and orchestrated loop |

`Sunder.Agent.Execution.Common` is a build-time implementation library shared by first-party execution/tool projects, not a Sunder runtime package. `Sunder.Package.Agent.Shared` and `Sunder.Package.Agent.Provider.Shared` contain source-linked implementation helpers, not public artifacts or supported extension APIs.

`packages.json` is the first-party release/capability inventory. Samples are intentionally excluded.

`Sunder.Package.Agent.Tools.Shell` is a self-contained six-RID Worker V2 package. Its universal archive aggregates one exact executable target for each supported RID. Runtime starts the selected target as a supervised child process; it does not load an `ISunderRuntimePackageModule` for Shell.

## Runtime And App Roles

Managed Agent packages can activate separate modules in the Runtime and App processes. The Shell package follows the supervised Worker V2 process lifecycle described above instead:

| Role | Owns | Must not own |
| --- | --- | --- |
| Runtime (`ISunderRuntimePackageModule`) | Network clients, secrets, storage authority, providers, tools, execution, memory, callbacks, typed operations/streams | Avalonia controls or direct App state |
| App (`ISunderAppPackageModule`) | Views, view models, navigation, host browser launch, typed Runtime clients | Runtime stores, provider clients, credentials, execution authority |

Role-local DI and RPC visibility mean a Runtime service object is never available directly in App. When both roles expose related behavior, they use different activation-owned providers with different responsibilities. The App should call a typed Runtime operation rather than duplicating state.

Runtime snapshots/gateways are explicit. Do not use static singletons, shared files, or assumptions about a common current directory to communicate between roles.

## Authority And Data Ownership

| Data | Authority |
| --- | --- |
| Profiles, model bindings, selectable capability assignments | Base Agent Runtime |
| Workspaces, canonical workspace paths, execution binding selection | Base Agent Runtime |
| Target-specific binding configuration | Execution package Runtime |
| Sessions, transcript turns/items, attachments, runs/checkpoints, approvals | Base Agent Runtime |
| Provider credentials and provider continuation/token state | Provider package Runtime secrets/state |
| Tool/execution implementation | Contributing package Runtime |
| Semantic memories and embeddings | Memory package Runtime |
| MCP server records and secret values | MCP package Runtime |
| Skills and subagent definitions | Their respective package Runtime stores |
| Views and temporary editor state | App role only |

Extensions read base projections through `IAgentRuntimeCatalog` and use service ports for host-owned mutations. They do not open or modify `agent/agent.db`.

## Extension Ownership

The Host records the activating package id for every managed `RegisterRpcProvider` call and every provider advertised by a Worker V2 activation. Each endpoint names one exact activation and becomes stale when that owner deactivates.

Use `SunderRpcProviderSnapshot.PackageId` when package identity matters, especially stack export or dependency inference. A domain descriptor's optional `PackageId` is useful display metadata but is not the ownership authority.

Stack exporters include the owning provider, execution-target, behavior-loop, skill, or subagent package when selected state depends on it. Ownerless results are invalid because an imported stack must know which package supplies a contribution.

## Dependency Graph

Every first-party Agent extension runtime archive declares:

```csharp
[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=2.0.0 <3.0.0")]
```

The Agent Protocol NuGet uses `[2.0.0,3.0.0)` while Sunder SDK/build packages retain their independent Core version line. Runtime graph validation occurs before assembly load; a package cannot rely on compatibility shims after an invalid graph is already active.

An extension may add dependencies on another extension only when its runtime behavior truly requires that package. Prefer optional RPC discovery when absence can be represented as not ready or unavailable.

## Storage And Configuration

- Use `IPackageSettings` plus one registered schema for normal user preferences.
- Use `IPackageSecrets` for keys, tokens, auth registrations, and secret header/environment values.
- Use package `Storage.State` for opaque operational state.
- Use role-local workspace only for package-owned files whose lifecycle matches that role.
- Include schema/version/checksum handling for durable package data.
- Implement `IAgentSessionDataCleaner` for session-keyed extension data.
- Never write another package's storage or database.

## Packaging

Identity and dependencies are authored as assembly attributes. `Sunder.Package.Build`:

1. Reads compiled metadata.
2. Infers required SDK capabilities from module calls and attributed APIs.
3. Generates `sunder-package.json`.
4. Emits `sunder-dev` after build.
5. Emits a versioned `.sunderpkg` on publish or `PackSunderPackage`.

Do not maintain a source manifest or copy SDK assemblies into the package archive. Package activation/reload remains host-owned; Builder builds and publishes artifacts but does not own Runtime activation.

## Public Surface Policy

`Sunder.Package.Agent.Protocol` is the public Agent author protocol assembly. Its checked-in descriptors and generated bindings are compatibility surfaces reviewed with the public API ledger.

Concrete types under first-party package projects are examples, not supported cross-package APIs. If a required capability cannot be expressed with the Protocol package, propose a descriptor/binding change instead of reflecting into an implementation or copying its internal storage format.

Next: [Extension-point catalog](extension-points.md) and [Release, compatibility, and troubleshooting](release-compatibility-troubleshooting.md).
