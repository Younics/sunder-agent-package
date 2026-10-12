<div align="center">
  <img src="src/Sunder.Package.Agent/Assets/icon.png" alt="Sunder Agent icon" width="128" />
  <h1>Sunder Agent Packages</h1>
  <p><strong>A composable local AI agent stack built as Sunder packages.</strong></p>
  <p>Agents, providers, tools, execution targets, memory, MCP, skills, and subagents for the Sunder desktop platform.</p>
  <p>
    <a href="https://github.com/Younics/sunder-core"><strong>Sunder Core</strong></a> &middot;
    <a href="docs/README.md"><strong>Documentation</strong></a> &middot;
    <a href="scripts/README.md"><strong>Scripts</strong></a> &middot;
    <a href="#package-family"><strong>Package Family</strong></a> &middot;
    <a href="#recommended-starting-sets"><strong>Starting Sets</strong></a> &middot;
    <a href="#build-from-source"><strong>Build</strong></a>
  </p>
  <p>
    <a href="LICENSE"><img alt="License: GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-blue.svg"></a>
    <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4.svg">
    <a href="https://github.com/Younics/sunder-agent-package/releases"><img alt="GitHub release" src="https://img.shields.io/github/v/release/Younics/sunder-agent-package?include_prereleases&label=release"></a>
  </p>
</div>

---

Sunder Agent is the first-party AI agent package family for [Sunder Core](https://github.com/Younics/sunder-core). It is intentionally modular: install the core agent package, then add only the providers, tools, memory, execution targets, and integrations you want.

The packages in this repository are normal Sunder runtime packages built with
`Sunder.Sdk` and `Sunder.Package.Build`. Extension packages depend on
`sunder.package.agent` at runtime and publish capabilities through exact public
contract locks. The obsolete descriptor-first projects are physically removed;
their immutable migration captures remain test evidence. V1 contracts, clients
and provider bindings are generated package-locally from ordinary implementation
and declaration source.

## What You Get

| Capability | What it enables |
| --- | --- |
| Agent workspace | Sessions, chat, profiles, workspaces, permissions, local History Search, and runtime orchestration inside Sunder. |
| Model providers | OpenAI, Anthropic, Gemini, and LM Studio package integrations. |
| Execution targets | Local machine and Docker-backed agent execution surfaces. |
| Tools | File-system tools, shell tools, web fetch/search, MCP tools, and package-provided native tools. |
| Memory and context | Semantic memory indexing, recall, lifecycle observers, and prompt context contribution. |
| Extensibility | Public contracts for providers, tools, execution, profile capabilities, prompts, memory, workspaces, and child runs. |

## Install

Install packages through the configured Sunder Registry when they are available:

```powershell
sunder package install sunder.package.agent
sunder package install sunder.package.agent.provider.openai
sunder package install sunder.package.agent.execution.local
sunder package install sunder.package.agent.tools.files
```

For local development, build package archives from source or load generated `sunder-dev` folders into Sunder App.

## Documentation

Extension authors should start with the **[Agent Extension Author Guide](docs/README.md)**. It includes the [quickstart](docs/extension-quickstart.md), the complete [extension-point catalog](docs/extension-points.md), capability-specific guides, testing guidance, and compatibility/troubleshooting policy.

The Agent workspace includes [local History Search](docs/history-search.md): it opens on recent history for the current workspace, searches lexical text and safe path/activity metadata locally, and maintains its disposable index automatically without embedding-provider calls.

## Package Family

| Package | Identifier | Role |
| --- | --- | --- |
| Sunder Agent | `sunder.package.agent` | Core sessions, chat, profiles, workspaces, permissions, and orchestration |
| OpenAI Provider | `sunder.package.agent.provider.openai` | OpenAI chat and embedding providers |
| Anthropic Provider | `sunder.package.agent.provider.anthropic` | Anthropic chat provider |
| Gemini Provider | `sunder.package.agent.provider.gemini` | Gemini chat and embedding providers |
| LM Studio Provider | `sunder.package.agent.provider.lmstudio` | Local LM Studio chat and embedding providers |
| Local Execution | `sunder.package.agent.execution.local` | Local machine execution target |
| Docker Execution | `sunder.package.agent.execution.docker` | Docker-backed execution target |
| Files Tools | `sunder.package.agent.tools.files` | File-system tool source and permission surface |
| Shell Tools | `sunder.package.agent.tools.shell` | Shell command tool source and permission surface |
| Web Tools | `sunder.package.agent.tools.web` | Web fetch and web search tools |
| MCP | `sunder.package.agent.mcp` | Model Context Protocol tool integration |
| Semantic Memory | `sunder.package.agent.memory.semantic` | Semantic memory indexing, recall, and prompt context |
| Skills | `sunder.package.agent.skills` | Reusable skill support for profiles and runs |
| Subagents | `sunder.package.agent.subagents` | Child agent sessions, subagent profiles, and run coordination |
| Builder | `sunder.package.agent.builder` | Package project creation, prerequisite setup, build, and publish |

## Recommended Starting Sets

| Goal | Packages |
| --- | --- |
| Minimal chat agent | `sunder.package.agent` plus one provider package |
| Local coding agent | Agent, one provider, local execution, files tools, shell tools |
| Research agent | Agent, one provider, web tools, semantic memory |
| Local model setup | Agent, LM Studio provider, local execution |
| Extensible agent workspace | Agent, MCP, skills, subagents, provider of choice |

Local execution runs with the current host user's privileges and is not an operating-system sandbox. Local structured Files operations and scoped `AGENTS.md` discovery use operation-owned no-follow filesystem authority, reject links/reparse points, hard-linked files, and device transitions, and require single-use process-local approval leases outside configured roots. Docker applies the same host-handle guarantees to configured workspace bind mounts without acquiring a container for structured operations; container-private and outside-bind paths fail closed. General shell/process execution is outside that structured guarantee. Docker accepts local daemon endpoints only, binds container reuse to daemon/image/mount identities, drops Linux capabilities, enables no-new-privileges, bounds CPU/memory/PIDs, and disables networking by default. It is not a complete security sandbox: configured workspace paths are writable bind mounts, container images are trusted code, and access to the Docker daemon remains security-sensitive.

## How It Fits Together

```text
Sunder App
  v
sunder.package.agent
  |  sessions, profiles, chat, workspaces, permissions
  +-- providers: OpenAI, Anthropic, Gemini, LM Studio
  +-- tools: files, shell, web, MCP, package-native tools
  +-- execution: local machine, Docker
  +-- context: skills, memory, prompt contributors
  +-- orchestration: behavior loops, subagents, child runs
```

Extension packages implement or declare RPC contracts in ordinary package source;
the build derives canonical descriptors, role-local clients/providers, and exact
contract uses. Cross-package consumers restore exact owner-contract locks without
referencing another package's Worker assembly. Worker V1 targets pass immutable
`SunderWorkerProviderRegistration` entries in `SunderWorkerOptions` to
`SunderWorker.RunAsync`.

Extension ownership is mandatory. Stack exporters use owned catalog contributions to include provider, execution-target, behavior-loop, skill, and subagent package dependencies; an ownerless catalog result is rejected by the SDK.

Builder supports project creation, prerequisite setup, build, and publish. Its Worker runs on the same host-managed shared .NET runtime as the rest of the family, while project `dotnet` commands use the SDK/toolchain explicitly selected for the workspace execution target. Build SDKs never enter the Worker execution-runtime store. Runtime package activation and reload remain host-owned operations outside Builder.

Core session continuity is owned by `sunder.package.agent`: the default behavior loop projects long transcripts into the provider prompt, stores session context checkpoints for omitted turns, and preserves active tool call/result pairs. Semantic memory remains durable, recallable knowledge and should not own active working summaries.

## Safety Model

Agent capabilities are split into explicit packages so users can choose what is installed and enabled.

| Area | Boundary |
| --- | --- |
| File and shell access | Provided by separate tool packages with Agent permission surfaces. |
| Provider secrets | Stored only through the Sunder package secrets capability. |
| Package preferences | Schema-declared settings use `IPackageSettings`; operational catalogs, workspace bindings, and session data remain on opaque `Storage.State`. |
| Execution | Local and Docker execution are separate packages. |
| Package activation | Runtime dependencies require `sunder.package.agent`; extension packages are not standalone. |

Browser callbacks are host-owned; packages never bind callback ports. OpenAI Codex authorization uses the auth projection, while MCP registers the distinct `mcp.oauth.v1` callback handler and starts server-scoped OAuth with the host-provided redirect URI. The MCP App gateway opens the SDK authorization URI, polls the generic callback session through completion, and leaves clear-authorization as a Runtime command. Dynamic client registrations and token caches remain server-scoped package secrets.

## 2.x Compatibility Policy

Exactly 15 Agent runtime packages ship at one 2.x family version; there is no
Agent Protocol release artifact. Extension runtime dependencies use
`>=2.0.0 <3.0.0`, while public RPC contracts retain independent semantic
versions and exact authority locks. Rebuild and release the complete family
together. The repository remains pre-release while the coordinated
implementation-first migration is incomplete.

Agent 2.0 creates the complete current SQLite schema from one checksummed `agent-2.0-baseline` entry. Startup accepts only an absent or empty database, or the exact current one-entry ledger; old, partial, ledgerless nonempty, and checksum-mismatched databases fail without mutation. Semantic Memory applies the same policy with its own `semantic-memory-2.0-baseline`. Do not edit or delete ledger rows.

App package modules own presentation and async Runtime gateways only. Runtime stores and authorities stay in Runtime composition, and Subsessions reads sessions, checkpoints, transcript pages, and change notifications through separate narrow async ports.

## Build From Source

```powershell
dotnet restore Sunder.AgentPackage.slnx
dotnet build Sunder.AgentPackage.slnx --no-restore
```

Package projects support two development modes:

| Mode | References |
| --- | --- |
| Inside the private `sunder` workspace | Local source references to `repos/sunder-core` |
| Standalone public clone | NuGet references to `Sunder.Sdk` and `Sunder.Package.Build` |

Each runtime family has one root, unsuffixed authored Library package project.
Core generates isolated Worker and optional App roles; `packages.json` lists only
the fifteen canonical roots. The 26 obsolete App/helper/central/packed-consumer
projects are physically removed and recorded after local canonical/source acceptance.
A normal canonical build composes the current RID at
`bin/<configuration>/<tfm>/sunder-dev`; invoke `PackSunderPackage` directly on
that same project to build and package all six RIDs.

Local unpublished families select validated local `.sunderpkg` candidates and the
canonical local Core catalog through ordinary package-tool operations. These locks
omit `registryOrigin`; missing local evidence never falls back to a Registry.
Published dependencies use the explicitly selected Registry. Publication evidence
is acquired separately and is not required to create a local family.

All fifteen final post-cleanup artifacts passed qualification on **osx-arm64:
75 commands, zero failures**, plus an audit of **180 managed assemblies**. All eleven actual test projects compiled
with zero warnings and the recorded functional scopes passed with existing Docker/
platform skips. The exact 249-file cleanup and twelve orphan App release locks are
removed; Execution.Common remains retained.

Workspace force-evaluate/locked restore and normal parallel **Build/Rebuild passed,
zero warnings/errors, 3m43.74s / 4m38.15s**, after the Core lease fix. Local candidate
NuGet qualification also verified **31 release locks** and isolated Release/osx-arm64
Build/Pack/CLI for Tools.Shell, Anthropic and Agent. Final required Main/source
acceptance passed **1,835/0/6 scoped skips**, including the actual all-family canonical
artifact test; all 26 project retirements are recorded. The skips are five archive-
directory prerequisites and one opt-in Docker case. Local canonical/local-first
migration is complete; remote Core publication, native/prebuilt, performance and
production release qualification remain separate open gates.

GitHub smoke CI resolves `Younics/sunder-core` `main` once at workflow start and reuses that full commit SHA for every Core checkout in the run. Release CI instead resolves the immutable `sdk/v<CORE_SDK_VERSION>` release and requires its tag, provenance, repository-signed NuGet packages, Agent locks, and every Core validation checkout to identify one commit. A recovery rerun accepts only that same Core identity from the preserved Agent draft. Land coordinated Core changes on `main` before expecting smoke CI to consume them, and publish the required final Core SDK release before creating an Agent release tag; see [`docs/RELEASES.md`](docs/RELEASES.md).

## Tests

```powershell
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Agent.Execution.Local.Tests/Sunder.Package.Agent.Execution.Local.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj --no-restore
```

## Release Tags

The release inventory is exactly **15 `.sunderpkg` archives**. With native evidence,
the output audit and four release-evidence files, publication carries **21 assets**;
the Actions-only handoff adds one file. Generated App roles travel with the native
RID leaves. Core still rejects canonical prebuilt packing until qualified role-input
evidence exists; rebuilding tested/signed leaves is not an acceptable fallback.
The publication/promotion/handoff shell fixtures pass with the corrected inventory;
qualified prebuilt/native evidence, performance and final V1 publication/
promotion integration remain open. See [`docs/RELEASES.md`](docs/RELEASES.md).

<details>
<summary>Historical descriptor-first release workflow (superseded inventory)</summary>

`agent/vX.Y.Z` publishes `Sunder.Package.Agent.Protocol` and all 15 exact runtime versions as one immutable family. Six native jobs build every Worker as an exact-RID framework-dependent .NET 10 managed module against final locked remote Core packages, then run Core Runtime lifecycle, Builder toolchain-boundary, and metric-budget gates. Prebuilt fan-in composes only those exact DLL/runtimeconfig/deps leaves without rebuilding them, records schema-version-2 native evidence plus a deterministic package-output audit, enforces the 360,003,313-byte selected-current-RID installed-family ceiling, validates the final archives through Package Format, Runtime lifecycle, and App snapshot activation, and requires the public Registry to resolve the exact supported shared .NET runtime profile for all six RIDs before any external publication. It then publishes every Registry version with `setLatest=false`, leaves a byte-verified 22-asset GitHub draft, and archives an attempt-specific publication handoff. Protected-main production acceptance and promotion execute only protected policy checkouts, treat the tagged release checkout as immutable data, and revalidate the exact successful publication run/attempt, artifact, workflow, source, tag, numeric release, provenance, and bytes. Acceptance adds an immutable, SHA-bound `agent-family-acceptance-<run-id>-<run-attempt>.json` asset carrying both publication coordinates and the protected acceptance workflow ref/SHA; retries reuse equal bytes and new attempts use new names without replacing prior evidence. A separate globally serialized protected-main promotion dispatch downloads that selected durable evidence, regenerates the output audit, re-verifies all draft and Registry bytes without rebuilding, applies a monotonic family mutation with guarded compensation on failure, and only then finalizes the accepted numeric GitHub release and re-verifies it. Registry state is fully refreshed after the pre-mutation guard, and the complete promoted family is the last remote check before finalization. The atomic numeric PATCH writes a deterministic handoff- and policy-bound receipt with `draft=false`, allowing a runner-loss retry to succeed only against that exact receipt, unchanged assets, and the fully promoted family.

</details>

Protocol public-API and package metadata history is preserved in authenticated test
captures, not active release projects. The live feed/retirement audit remains a
separate required check. Full operator steps and repository configuration are
in [`docs/RELEASES.md`](docs/RELEASES.md).

## Related Projects

| Project | Purpose |
| --- | --- |
| [`Younics/sunder-core`](https://github.com/Younics/sunder-core) | Desktop shell, runtime host, CLI, SDK, package build tooling, templates, and public Registry contracts |
| `Sunder.Sdk` | Public package author contracts from Sunder Core |
| `Sunder.Package.Build` | MSBuild package pipeline for `sunder-dev` and `.sunderpkg` output |

## Contributing

Issues and pull requests are welcome. Please read [`CONTRIBUTING.md`](CONTRIBUTING.md) before opening a larger change, and discuss substantial Agent contracts or package architecture changes first.

For vulnerability reports, use the private process in [`SECURITY.md`](SECURITY.md). Please do not open public issues for security problems involving shell execution, file access, provider credentials, or package installation.

## License

Sunder Agent packages are distributed under the [GNU General Public License v3.0](LICENSE).
