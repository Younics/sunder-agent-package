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
| Builder | `sunder.package.agent.builder` | Sunder package development session controls |

## Recommended Starting Sets

| Goal | Packages |
| --- | --- |
| Minimal chat agent | `sunder.package.agent` plus one provider package |
| Local coding agent | Agent, one provider, local execution, files tools, shell tools |
| Research agent | Agent, one provider, web tools, semantic memory |
| Local model setup | Agent, LM Studio provider, local execution |
| Extensible agent workspace | Agent, MCP, skills, subagents, provider of choice |

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

## Safety Model

Agent capabilities are split into explicit packages so users can choose what is installed and enabled.

| Area | Boundary |
| --- | --- |
| File and shell access | Provided by separate tool packages with Agent permission surfaces. |
| Provider secrets | Stored through Sunder package configuration and secrets abstractions. |
| Execution | Local and Docker execution are separate packages. |
| Package activation | Runtime dependencies require `sunder.package.agent`; extension packages are not standalone. |

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
| `agent/v1.0.0` | `sunder.package.agent` |
| `agent-provider-openai/v1.0.0` | `sunder.package.agent.provider.openai` |
| `agent-tools-files/v1.0.0` | `sunder.package.agent.tools.files` |
| `agent-execution-local/v1.0.0` | `sunder.package.agent.execution.local` |

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
