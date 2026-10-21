# Contributing to AI Agent Packages

This repository contains the first-party Agent package, providers, tools, execution
targets, memory, MCP, skills, subagents and package builder integration.

The current authoring model derives contracts and package-local bindings from ordinary
implementation and declaration source. Each family has one authored project. Removed
project/source captures remain immutable test evidence; see [compatibility](compatibility/README.md).

## Before You Start

For small fixes, documentation improvements, and tests, open a pull request directly.

For larger changes, open an issue first. Please discuss changes to Agent RPC
contracts or their migration corpus, package dependencies, permission surfaces,
execution behavior, provider behavior, and package architecture before
implementation.

## Local Development

Restore and build the repository:

```powershell
dotnet restore Sunder.AgentPackage.slnx
dotnet build Sunder.AgentPackage.slnx --no-restore
```

Useful targeted tests:

```powershell
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj --no-restore
```

## Development Modes

Package projects support two development modes:

| Mode | References |
| --- | --- |
| Inside the private `sunder` workspace | Local source references to `repos/sunder-core` |
| Standalone public clone | NuGet references to `Sunder.Sdk` and `Sunder.Package.Build` |

Package projects must not reference `Sunder.App` or `Sunder.Runtime.Host` directly.

## Package Boundaries

| Area | Owns |
| --- | --- |
| `Sunder.Package.Agent` | Core sessions, chat, profiles, workspaces, permissions, orchestration |
| Agent service contracts | Reviewed public contracts and package-local generated Services/Models |
| Provider packages | Chat and embedding provider integrations |
| Tool packages | File, shell, web, MCP, and native tool capabilities |
| Execution packages | Local and Docker execution targets |
| Memory packages | Indexing, recall, lifecycle observers, and prompt context |
| Skills and subagents | Reusable skill support and child agent coordination |

## Pull Request Checklist

Before opening a PR, please check:

- The change is scoped to the smallest useful fix or feature.
- Package boundaries remain clear.
- New capabilities use exact Agent RPC descriptors and activation-owned providers where appropriate.
- Provider secrets and configuration use Sunder package abstractions.
- File, shell, web, and execution changes consider permissions and user control.
- Relevant tests were added or updated.
- Targeted tests were run locally when practical.
- Documentation was updated when behavior changed.

## Reporting Bugs

Use the bug report template and include:

- AI Agent package version or commit.
- Installed Agent package set.
- Provider, model, execution target, and tool packages involved.
- Reproduction steps.
- Expected behavior.
- Actual behavior.
- Relevant logs with secrets removed.

Please do not include API keys, tokens, provider credentials, local secrets, private prompts, private files, or sensitive transcripts in public issues.

## Security

Please report vulnerabilities privately using [`SECURITY.md`](SECURITY.md). Do not open public issues for security-sensitive problems involving shell execution, file access, provider credentials, package installation, or model/tool behavior that exposes private data.
