# Agent Service Contracts

Agent exposes four owned concrete public services—RuntimeCatalog, RunControl,
WorkspaceExecutionResolver and ChildRunExecutor—and 15 declaration-only extension
contracts. Providers implement the imported generated interfaces. All 15 package
families use one authored project with generated Worker/App roles.

## Current Source

```csharp
using IChatProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Chat_Provider.IChatProviderService;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Sdk.Services;
```

- Inject contract-scoped imported `IPackageApi` or `IServiceProviders<TService>`; call the
  selected entry's `Service` and transfer its typed `Reference` when needed.
- Author business records once. Importers use generated Models; codecs, envelopes,
  binding observation and retirement belong to generated DI.
- Implement multiple contracts with repeated `SunderServiceProvider` declarations
  and explicit interface methods where names collide.
- Use `SunderContent` and normal disposal. `[JsonIgnore]` keeps transient authority
  out of persistence; `SunderServiceMember` opts it into deliberate service transfer.
- Include server context only for authenticated-caller policy. Method timeout
  metadata is bounded by the Host/parent, including 2100-second long operations.
- Nullable references remain strict unless `SunderOptionalServiceReference`
  explicitly allows safe exact-provider absence. Permission/unsafe failures propagate.
- Imported role/action grants are explicit. Replayable content watches may opt into
  `CompleteOnResponseContentRetirement`; other failures remain failures.

See the [quickstart](extension-quickstart.md), [contract catalog](extension-points.md)
and [Core service guide](https://github.com/Younics/sunder-core/blob/main/docs/package-development/ORDINARY-SERVICES.md).

## Imported C# Name Map

Imported canonical interfaces use
`Sunder.Generated.Packages.<Authority>_Services.<ContractSymbol>.<Interface>`.
The reviewed Agent locks select ultimate authority `younics.ai.agent`, encoded
as `Younics_Ai_Agent`; every scope below therefore starts with
`Sunder.Generated.Packages.Younics_Ai_Agent_Services.`. The contract symbol is
`C` followed by its dot-separated segments with Pascal initials and underscores.
File-local aliases keep provider attributes, method signatures and explicit
`DescribeAsync` implementations readable.

| Interface / usual local alias | Contract id | Contract symbol |
| --- | --- | --- |
| `IBehaviorLoopService` | `younics.ai.agent.behavior.loop` | `CYounics_Ai_Agent_Behavior_Loop` |
| `IChatProviderService` | `younics.ai.agent.chat.provider` | `CYounics_Ai_Agent_Chat_Provider` |
| `IChildRunExecutorService` | `younics.ai.agent.child.run.executor` | `CYounics_Ai_Agent_Child_Run_Executor` |
| `IDurableLifecycleObserverService` | `younics.ai.agent.durable.lifecycle.observer` | `CYounics_Ai_Agent_Durable_Lifecycle_Observer` |
| `IEmbeddingProviderService` | `younics.ai.agent.embedding.provider` | `CYounics_Ai_Agent_Embedding_Provider` |
| `IExecutionTargetService` | `younics.ai.agent.execution.target` | `CYounics_Ai_Agent_Execution_Target` |
| `IPermissionSurfaceService` | `younics.ai.agent.permission.surface` | `CYounics_Ai_Agent_Permission_Surface` |
| `IProfileCapabilityConsumerService` | `younics.ai.agent.profile.capability.consumer` | `CYounics_Ai_Agent_Profile_Capability_Consumer` |
| `IPromptContextContributorService` | `younics.ai.agent.prompt.context.contributor` | `CYounics_Ai_Agent_Prompt_Context_Contributor` |
| `IRunControlService` | `younics.ai.agent.run.control` | `CYounics_Ai_Agent_Run_Control` |
| `IRuntimeCatalogService` | `younics.ai.agent.runtime.catalog` | `CYounics_Ai_Agent_Runtime_Catalog` |
| `ISelectableCapabilityProviderService` | `younics.ai.agent.selectable.capability.provider` | `CYounics_Ai_Agent_Selectable_Capability_Provider` |
| `ISessionCleanerService` | `younics.ai.agent.session.cleaner` | `CYounics_Ai_Agent_Session_Cleaner` |
| `ISystemPromptContributorService` | `younics.ai.agent.system.prompt.contributor` | `CYounics_Ai_Agent_System_Prompt_Contributor` |
| `IToolExecutionObservedService` | `younics.ai.agent.tool.execution.observed` | `CYounics_Ai_Agent_Tool_Execution_Observed` |
| `IToolSourceService` | `younics.ai.agent.tool.source` | `CYounics_Ai_Agent_Tool_Source` |
| `IWorkspaceEditorService` | `younics.ai.agent.workspace.editor` | `CYounics_Ai_Agent_Workspace_Editor` |
| `IWorkspaceExecutionResolverService` | `younics.ai.agent.workspace.execution.resolver` | `CYounics_Ai_Agent_Workspace_Execution_Resolver` |

Imported `IPackageApi` always lives under the corresponding contract scope, even
for a one-service contract. Provider-key aliases remain stable. Agent's owned and
source-shaped interfaces stay flat under its Services namespace, including when
referenced by tests through an Agent Worker/App assembly alias. Family importers
and their cross-assembly tests use the contract-scoped interfaces. Imported Models
retain their authored business names.

Imported-interface scoping preserves provider, service and method keys. The
[current inventory](../compatibility/agent-current-inventory.json) records runtime
package/contract authorities; authored C# project, assembly and namespace names
remain stable. Current descriptor, ABI and projection identities require owning-tool
evidence and separate review against source intent.

## Current Compatibility

All 18 public APIs start at `1.0.0`, independently of package `2.0.0`; the 13 private
APIs follow package SemVer. Qualified current receipts propose byte records, while
the independent test inventory and functional suites constrain expected methods,
values, errors, deadlines, authority and role grants. Historical captures remain
inert and are never reconstructed by normal tests or release. The
[compatibility guide](../compatibility/README.md) owns those evidence rules.

Current native/cold/publication requirements are in [RELEASES.md](RELEASES.md).
Local qualification history belongs with its retained evidence rather than the
authoring guide; it does not change the current API or freeze a developer's outputs.
