# Agent Extension Author Guide

Build extensions as single-project Sunder packages. Select reviewed Agent APIs from
an exact local archive or explicit Registry; the lock generates dependencies and
package-local service/model bindings. Extensions use the Sunder SDK and generated
APIs rather than referencing Agent, App or Runtime Host implementation assemblies.

## Start Here

| Goal | Guide |
| --- | --- |
| Create and build an extension | [Extension quickstart](extension-quickstart.md) |
| Choose an API | [Contract catalog](extension-points.md) |
| Implement chat or embedding models | [Providers](providers.md) |
| Publish tools and permission surfaces | [Tools and permissions](tools-and-permissions.md) |
| Add an execution target | [Execution targets](execution-targets.md) |
| Add prompt context or lifecycle handling | [Prompts, context and lifecycle](prompts-context-lifecycle.md) |
| Customize orchestration or child runs | [Behavior loops and subagents](behavior-loops-and-subagents.md) |
| Test an extension | [Testing](testing-extensions.md) |
| Release or troubleshoot | [Release/compatibility troubleshooting](release-compatibility-troubleshooting.md) |

## Ownership

The base Agent Worker owns profiles, workspaces, sessions, transcripts, approvals and
runs. Provider packages own credentials/continuations; execution packages own target
configuration; tools, Memory, MCP, Skills and Subagents own their own durable data.
App roles own presentation and transient editor state. They call generated APIs for
Worker behavior and never open another package's database or exchange state through
static objects/shared files.

Use settings for preferences, secrets for credentials and package storage for opaque
state/files. Package-owned migrations/checksums protect durable state. Host-stamped
provider ownership and exact activation identity determine authority; display metadata
does not. Stack dependencies derive from the actual owning package of each contribution.

## Public Surface

- `Sunder.Generated.Packages.Younics_Ai_Agent_Services.<ContractSymbol>`:
  imported service interfaces and contract-scoped `IPackageApi`, for example
  `CYounics_Ai_Agent_Chat_Provider.IChatProviderService`. See the
  [C# name map](ordinary-service-refactor.md#imported-c-name-map).
- `Sunder.Generated.Packages.Younics_Ai_Agent_Services`: Agent's own
  source-shaped service interfaces.
- `Sunder.Generated.Packages.Younics_Ai_Agent_Models`: imported business values.
- `Sunder.Sdk.Services`: authoring, typed references/catalogs and content.
- `Sunder.Sdk.Rpc`: advanced transport contracts and authenticated invocation context.

AI Agent has 18 public contracts: four owned implementation-first services and 14
declaration-only extension contracts. All 18 `younics.ai.agent.*` API identities
start at `1.0.0`; the package and its private contracts remain `2.0.0`.
The platform-owned `sunder.stack.contributor` also remains `2.0.0`. There is no
Protocol NuGet artifact or authored App/Worker helper project.

For current expectations, byte-record review and inert historical evidence, see
[compatibility](../compatibility/README.md). Release/native/cold-input requirements
are maintained in [Releases](RELEASES.md), [native inputs](native-release-inputs.md)
and [cold inputs](cold-release-inputs.md).
