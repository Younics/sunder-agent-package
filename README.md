<div align="center">
  <img src="src/Sunder.Package.Agent/Assets/icon.png" alt="Sunder Agent icon" width="128" />
  <h1>Sunder Agent Packages</h1>
  <p><strong>A composable local AI agent stack built as Sunder packages.</strong></p>
  <p>Agents, providers, tools, execution targets, memory, MCP, skills, and subagents for the Sunder desktop platform.</p>
  <p>
    <a href="https://github.com/Younics/sunder-core"><strong>Sunder Core</strong></a> &middot;
    <a href="docs/README.md"><strong>Documentation</strong></a> &middot;
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

The packages in this repository are normal Sunder runtime packages built with `Sunder.Sdk` and `Sunder.Package.Build`. Extension packages depend on `sunder.package.agent` at runtime and publish capabilities through the schema-first contracts in `Sunder.Package.Agent.Protocol`.

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
sunder install sunder.package.agent
sunder install sunder.package.agent.provider.openai
sunder install sunder.package.agent.execution.local
sunder install sunder.package.agent.tools.files
```

For local development, build package archives from source or load generated `sunder-dev` folders into Sunder App.

## Documentation

Extension authors should start with the **[Agent Extension Author Guide](docs/README.md)**. It includes the [quickstart](docs/extension-quickstart.md), the complete [extension-point catalog](docs/extension-points.md), capability-specific guides, testing guidance, and compatibility/troubleshooting policy. A compiled minimal extension is available under [`samples/Sunder.Agent.Extension.Minimal`](samples/Sunder.Agent.Extension.Minimal/README.md).

The Agent workspace includes [local History Search](docs/history-search.md): it opens on recent history for the current workspace, searches lexical text and safe path/activity metadata locally, and maintains its disposable index automatically without embedding-provider calls.

## Package Family

| Package | Identifier | Role |
| --- | --- | --- |
| Sunder Agent | `sunder.package.agent` | Core sessions, chat, profiles, workspaces, permissions, and orchestration |
| Agent Protocol | `Sunder.Package.Agent.Protocol` | RPC descriptors plus generated DTO, client, and provider bindings |
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

Extension packages use `Sunder.Package.Agent.Protocol` and `RegisterRpcProvider` to publish schema-validated capabilities to the core Agent package. That keeps providers, tools, execution targets, and memory features independently installable without sharing live extension objects across package activations.

Extension ownership is mandatory. Stack exporters use owned catalog contributions to include provider, execution-target, behavior-loop, skill, and subagent package dependencies; an ownerless catalog result is rejected by the SDK.

Builder supports project creation, prerequisite setup, build, and publish. Runtime package activation and reload remain host-owned operations outside Builder.

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

Every Agent runtime package and `Sunder.Package.Agent.Protocol` ships at one 2.x family version. Protocol references use `[2.0.0,3.0.0)` and extension runtime dependencies use `>=2.0.0 <3.0.0`. Rebuild and release the complete family together. Cross-package capabilities use only the 18 schema-first descriptors, generated bindings, and manifest-declared RPC providers; Runtime rejects mixed package baselines before activation.

Agent data upgrades are forward-only through one ordered SQLite migration ledger, currently through migration 20, `durable-resource-claims`, with checksum `849bc5010a9c700c8cb683e2992e442dc4471fed275135b39f60ae8d54f46fa0`. Migration 20 adds durable resource-claim persistence and execution-target ownership while leaving migrations 1-19 and their checksums unchanged. Each applied migration records its number, immutable name, and SHA-256 checksum in the same transaction as its schema change. Startup refuses unknown, newer, renamed, or checksum-mismatched entries. If validation fails, back up `agent/agent.db` and use an Agent build that recognizes the ledger; do not edit or delete ledger rows.

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

GitHub CI resolves `Younics/sunder-core` `main` once at the start of each run and reuses that full commit SHA for every Core checkout in the run. Land coordinated Core changes on `main` before expecting Agent CI to consume them, and publish the required Core developer-package version before creating an Agent release tag; see [`docs/RELEASES.md`](docs/RELEASES.md).

## Tests

```powershell
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Agent.Execution.Local.Tests/Sunder.Package.Agent.Execution.Local.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj --no-restore
```

## Release Tags

`agent/vX.Y.Z` releases `Sunder.Package.Agent.Protocol` and all 15 runtime packages as one family. The workflow builds once from one Agent commit and one Core `main` commit resolved at workflow start, validates the exact artifacts through Package Format, Runtime lifecycle, and App snapshot activation, publishes immutable versions without moving Registry tags, verifies downloaded bytes, and only then promotes `latest` for stable releases or `preview` for prereleases.

Stable releases require `PublicAPI.Unshipped.txt` to contain no API entries. The 2.x baseline belongs to the `Sunder.Package.Agent.Protocol` package and assembly identity. The retained `Sunder.Package.Agent.Contracts.*` source namespaces are types inside that artifact, not a second package. Full operator steps and required repository configuration are in [`docs/RELEASES.md`](docs/RELEASES.md).

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
