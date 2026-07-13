<div align="center">
  <img src="src/Sunder.Package.Agent/Assets/icon.png" alt="Sunder Agent icon" width="128" />
  <h1>Sunder Agent Packages</h1>
  <p><strong>A composable local AI agent stack built as Sunder packages.</strong></p>
  <p>Agents, providers, tools, execution targets, memory, MCP, skills, and subagents for the Sunder desktop platform.</p>
  <p>
    <a href="https://github.com/Younics/sunder-core"><strong>Sunder Core</strong></a> &middot;
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

The packages in this repository are normal Sunder runtime packages built with `Sunder.Sdk` and `Sunder.Package.Build`. Extension packages depend on `sunder.package.agent` at runtime and contribute capabilities through `Sunder.Package.Agent.Contracts`.

## What You Get

| Capability | What it enables |
| --- | --- |
| Agent workspace | Sessions, chat, profiles, workspaces, permissions, and runtime orchestration inside Sunder. |
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

## Package Family

| Package | Identifier | Role |
| --- | --- | --- |
| Sunder Agent | `sunder.package.agent` | Core sessions, chat, profiles, workspaces, permissions, and orchestration |
| Agent Contracts | `Sunder.Package.Agent.Contracts` | Public NuGet contracts used by Agent extension packages |
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
| Builder | `sunder.package.agent.builder` | Package creation/build/publish plus optional host-provided development session controls |

## Recommended Starting Sets

| Goal | Packages |
| --- | --- |
| Minimal chat agent | `sunder.package.agent` plus one provider package |
| Local coding agent | Agent, one provider, local execution, files tools, shell tools |
| Research agent | Agent, one provider, web tools, semantic memory |
| Local model setup | Agent, LM Studio provider, local execution |
| Extensible agent workspace | Agent, MCP, skills, subagents, provider of choice |

Local execution runs commands and file operations with the current host user's privileges. It is a trusted host-user execution target, not an operating-system sandbox. Workspace bindings, canonical path checks, and tool permission contracts constrain requested paths, but they do not isolate a malicious process from the host; use the Docker execution target when isolation is required.

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

Extension packages use `Sunder.Package.Agent.Contracts` to register capabilities with the core Agent package. That keeps providers, tools, execution targets, and memory features independently installable.

Extension ownership is mandatory. Stack exporters use owned catalog contributions to include provider, execution-target, behavior-loop, skill, and subagent package dependencies; an ownerless catalog result is rejected by the SDK.

Builder always supports project creation, build, and publish. Load, auto-load, and Live reload require an available `IPackageDevelopmentSessionControl`; when the App/Runtime topology cannot share a development output path, those controls remain disabled and Builder displays the host-provided reason.

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

## 1.x Compatibility Policy

Version `1.1.0` is an intentional clean break from the unused public `1.0.0` line. Every Agent package and `Sunder.Package.Agent.Contracts` ships at `1.1.0`; extension package dependencies require `>=1.1.0 <1.2.0`. Rebuild the complete family together. There are no 1.0 compatibility shims, and the Runtime rejects mixed SDK/package baselines before assembly load. The new immutable contract ledger is `src/Sunder.Package.Agent.Contracts/PublicAPI.Shipped.txt`.

Agent data upgrades are forward-only through one ordered SQLite migration ledger. Each applied migration records its number, immutable name, and SHA-256 checksum in the same transaction as its schema change. Startup refuses unknown, newer, renamed, or checksum-mismatched entries. If validation fails, back up `agent/agent.db` and use an Agent build that recognizes the ledger; do not edit or delete ledger rows.

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

## Tests

```powershell
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj --no-restore
```

## Release Tags

Package releases are tag-driven. Examples:

| Tag | Package |
| --- | --- |
| `agent/v1.1.0` | `sunder.package.agent` |
| `agent-provider-openai/v1.1.0` | `sunder.package.agent.provider.openai` |
| `agent-tools-files/v1.1.0` | `sunder.package.agent.tools.files` |
| `agent-execution-local/v1.1.0` | `sunder.package.agent.execution.local` |

The release workflow builds, tests, packs a `.sunderpkg`, and uploads it to the GitHub release.

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
