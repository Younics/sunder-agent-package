<div align="center">
  <img src="src/Sunder.Package.Agent/Assets/icon.png" alt="AI Agent icon" width="128" />
  <h1>AI Agent Packages</h1>
  <p>A composable local AI agent stack for Sunder.</p>
</div>

Install the base Agent, then add the providers, tools, execution targets and
integrations you need. Packages use the Sunder SDK, reviewed API locks and generated
service/model bindings. [Sunder Core](https://github.com/Younics/sunder-core) supplies
the desktop shell, local Host, CLI and package tooling.

## Install

Install published packages through your configured Registry:

```sh
sunder package install younics.ai.agent
sunder package install younics.ai.agent.provider.openai
sunder package install younics.ai.agent.execution.local
sunder package install younics.ai.agent.tools.files
```

For unpublished development, build archives or load the generated `sunder-dev`
folders. Local API selection uses exact candidates and a Core catalog without a Registry.

## Documentation

- [Extension author guide](docs/README.md) and [quickstart](docs/extension-quickstart.md)
- [Contract catalog](docs/extension-points.md)
- [Local History Search](docs/history-search.md)
- [Sunder Package Maker: local projects, live reload, and SDKs](docs/package-maker.md)
- [Releases](docs/RELEASES.md), [native inputs](docs/native-release-inputs.md),
  [cold inputs](docs/cold-release-inputs.md)
- [Script ownership](scripts/README.md) and [compatibility evidence](compatibility/README.md)

## Package Family

| Package | Identifier | Responsibility |
| --- | --- | --- |
| AI Agent | `younics.ai.agent` | Profiles, workspaces, sessions, chat, history, permissions and runs |
| OpenAI | `younics.ai.agent.provider.openai` | Chat and embeddings |
| Anthropic | `younics.ai.agent.provider.anthropic` | Chat |
| Gemini | `younics.ai.agent.provider.gemini` | Chat and embeddings |
| LM Studio | `younics.ai.agent.provider.lmstudio` | Local chat and embeddings |
| Local Execution | `younics.ai.agent.execution.local` | Host-user execution |
| Docker Execution | `younics.ai.agent.execution.docker` | Docker-backed execution |
| Files | `younics.ai.agent.tools.files` | Structured filesystem tools and permission surface |
| Shell | `younics.ai.agent.tools.shell` | Shell tools and permission surface |
| Web | `younics.ai.agent.tools.web` | Fetch and search |
| MCP | `younics.ai.agent.mcp` | MCP tools, configuration and OAuth |
| Semantic Memory | `younics.ai.agent.memory.semantic` | Durable indexing, recall and context |
| Skills | `younics.ai.agent.skills` | Profile-selected skills |
| Subagents | `younics.ai.agent.subagents` | Child sessions and orchestration |
| Sunder Package Maker | `younics.sunder.package.maker` | Local package creation, SDKs, builds, live reload and archives |

`packages.json` owns the 15-archive inventory. `Sunder.Agent.Execution.Common` is a
retained implementation library, not a sixteenth runtime package.

## Recommended Starting Sets

| Goal | Packages |
| --- | --- |
| Minimal chat | Agent and one provider |
| Local coding | Agent, provider, Local Execution, Files and Shell |
| Research | Agent, provider, Web and Semantic Memory |
| Local models | Agent, LM Studio and Local Execution |
| Extensible workspace | Agent, provider, MCP, Skills and Subagents |

## How It Fits Together

Each family has one authored Library project. Core generates isolated exact-RID
Worker and optional App roles, service clients, dispatch and composition. App views
call generated APIs; they do not own Runtime stores or credentials. Cross-package
consumers use reviewed locks rather than another package's implementation assembly.

The base Agent owns session continuity, transcript/tool pairing and working summaries.
Semantic Memory owns durable recallable knowledge. Provider credentials, MCP registrations
and OAuth token caches stay in the owning package's secrets. Browser callbacks are
Host-owned; packages do not bind callback ports. Stack contributions identify their
actual owning package so dependencies can be exported accurately.

Package Maker's Worker uses the Host-managed .NET execution runtime. Project build commands
use local SDKs/toolchains selected against each project's requirements. Runtime activation
and reload remain Host-owned. See the [Package Maker workflow](docs/package-maker.md).

## Safety Model

Local execution runs with the host user's privileges. Structured Local/Docker file
operations retain no-follow filesystem authority, reject links/hard links/device
transitions and enforce exact scope/approval claims. General shell/process execution
is outside that guarantee.

Docker accepts local daemon endpoints, binds reuse to daemon/image/mount identity,
drops capabilities, enables no-new-privileges, bounds CPU/memory/PIDs and disables
networking by default. Writable mounts, trusted image code and daemon access mean it
is not a complete OS sandbox. See [execution security](docs/execution-targets.md).

## 2.x Compatibility Policy

The 15 runtime packages share the 2.x family line. Public APIs owned by
`younics.ai.agent` started their new identity families at `1.0.0`; `chat-provider`
and `execution-target` now use locally reviewed `2.0.0` successors, retaining frozen
`1.0.0` history. The other sixteen remain at `1.0.0`; private contracts follow package
version `2.0.0`. The platform-owned `sunder.stack.contributor` stays
at `2.0.0`. Runtime IDs and current test expectations follow the
[current inventory](compatibility/agent-current-inventory.json); C# project, assembly
and authored namespace names remain stable. No Protocol package
is published. Current qualification uses the clean-slate baseline; native/performance/production release
qualification remains separate.

Agent and Semantic Memory SQLite startup accepts an empty store or the exact current
checksummed baseline. Old, partial or mismatched ledgers fail without mutation; do
not edit ledger rows. Authenticated source captures and historical compatibility
oracles remain inert history, outside normal test/build/release inputs. See the
[current baseline and qualification inputs](docs/current-test-baseline.md).

## Build From Source

Restore NuGet dependencies first:

```sh
dotnet restore Sunder.AgentPackage.slnx
```

On a fresh checkout or after clearing the contract cache, follow
[local contract preparation](scripts/README.md#local-contract-preparation) with the
exact Main package candidate and Core catalog selected by the checked-in locks.
Then build normally:

```sh
dotnet build Sunder.AgentPackage.slnx --no-restore
```

Workspace builds use sibling Core source; standalone clones use coordinated NuGet
packages. Build emits each current-RID `bin/<configuration>/<tfm>/sunder-dev`.
Invoke `PackSunderPackage` on the same canonical project for all declared RIDs.
For automatic development builds, run `dotnet watch build` from the package directory
and enable `--watch` when loading its output in Sunder.

## Tests

Start with the changed class, then the affected project:

```sh
dotnet test tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj --no-restore --filter FullyQualifiedName~RepositoryArchitectureTests
dotnet test tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj --no-restore
```

The all-family artifact/native lanes belong at their integration/release boundaries;
missing required inputs fail those lanes. See [testing](docs/testing-extensions.md).

## Release Tags

`agent/vX.Y.Z` releases the exact 15-archive family. Release CI requires immutable
Core SDK source/provenance and matching package locks; mutable `main` is only a smoke
input. Qualified schema-3 native inputs preserve tested role bytes through no-rebuild
fan-in. Publication starts Yanked and promotion uses one retained atomic cohort
transition after acceptance. [Releases](docs/RELEASES.md) owns the full operator sequence.

## Contributing And License

Read [CONTRIBUTING.md](CONTRIBUTING.md) for changes and [SECURITY.md](SECURITY.md) for
private vulnerability reporting. Licensed under [GPL-3.0](LICENSE).
