# Retirement evidence inventory

These fixtures make the historical oracle independent of obsolete production files.
The explicit capture set contains 26 projects (25 source projects and the out-of-solution
PackedProtocolConsumer), 154 wrappers, 67 helper-support files, and the obsolete
fixture's Program and verifier. Additional removals require their own authenticated
evidence and current replacement checks.
Historical validation does not replace fresh generated-role and artifact validation.

## Post-cleanup validation

The 249-file retirement is now physical. The current guard rejects reintroduced
retired files, even with identical historical bytes, because the canonical roots no
longer contain the old broad Compile/None exclusions.

`physical-cleanup-history.json` preserves the complete authenticated pre-cleanup
forms of the fifteen roots and `Directory.Build.targets`. The owning test proves
that only stale exclusions, empty groups and the reviewed legacy role/bundle/use/
provider machinery were removed. The Stacks NuGet reference must explicitly exclude
`buildTransitive`; the frozen declaration oracle reads the historical targets as data.

The obsolete `src/Sunder.Package.Agent/Models/AgentSemanticSources.props` is captured
as `AgentSemanticSources.props.txt` and physically removed. Its normalized SHA-256 is
`c37ed587176e9a90c250d2b59043966d537b64173ca89face132a4f5d5f7594f`.
The original 142 business/catalog mappings remain explicit and validated against
both the frozen dispositions and the live relocated sources. Interface readers use
the separate reviewed 38-source relocation map.

The twelve removed App `packages.release.lock.json` files do not occur in the frozen
RPC migration source inventory, so no extra before-images are substituted for them.
They remain a separate concern of any rolling release-candidate inventory.

Two exact UI inverses are checked against `ui-followup-history.json`:

| Source | Reconstructed authenticated before SHA-256 |
| --- | --- |
| `src/Sunder.Package.Agent.Shared/PackageViews/TranscriptScrollCoordinator.cs` | `5926aa390460f34b328b9f38fda2ecb59e9ef173024454f911ddfd695184e937` |
| `src/Sunder.Package.Agent.Subagents/PackageViews/SubagentsViewModel.Operations.cs` | `afa2f35b774af7870a71283b18ab084b3fea976674abd04139f5fcafb965537b` |

Both UI sources are outside the frozen RPC adapter/source-hash inventory; their
new evidence is separate and does not add records to that immutable baseline.

The actual post-cleanup tree passed 20 focused isolated history cases and the full
frozen baseline. These checks compile only the owning history logic and use source
inventories as data; no product, Core or artifact builders participate.

## Verified evidence

- 13 retained-project index blobs reconstruct the frozen original hashes through the
  existing explicit inverses. Test-local alias names and XML formatting may evolve;
  role targets, imports, dependencies, Compile/resource items and output properties are guarded.
- 25 retiring source projects and 154 wrappers match the supplied cleanup snapshot hashes.
- The out-of-solution PackedProtocolConsumer project, its Program and its verifier
  match the working tree, index and HEAD byte-for-byte. They are independent of the
  frozen 57-project inventory and do not add a fictional baseline project.
- 67 additional helper-boundary files are preserved separately: 63 normalized captures
  directly match frozen source hashes; 4 use the existing reviewed split-source inverses.
- The complete original baseline passed isolated validation before retirement and in
  an input-only mirror without all **249** retiring/support paths. This includes all
  57 projects, 189 project references, 252 Compile links, 31 descriptors, 299 migration
  source records and 96 original adapters. Captured code is data, never compiled.
- Seventeen focused owning history cases passed in the isolated C# harness.
- No product build or artifact builder was run for this evidence.

### Fixture SHA-256

- `retirement-history.json`: `271c798f547bd3e0a4fb6b26bac5bfc7acc184768c0936cef12188dacb4499d3`
- `helper-support-history.json`: `48619b9b589bb15dcee37592cfc710a236b28649edbfd38d27094248eb78dd2a`
- `retirement-dispositions.json`: `4ac1b1842a59776620994c8114a8ad6b5646d04e8f7ce797e315e364b77d55a9`
- `packed-protocol-consumer-history.json`: `93302cba48845d76570ea7b6f8fd83bd2fbb33e6bf88a227ecaed6bbb9da1e1d`
- `deletion-inventory.json`: `832e64c7634678c9c17d24385c1e054f2fc15f2353dcd79c3984111e692091a8`
- `physical-cleanup-history.json`: `2e3faefea55a39d04e8d1c9fc839286f472e24442ba4cbafc1d7f9c4846c504e`
- `ui-followup-history.json`: `7dfd90a1bd145160157ecb354baf342a699e566f2eb7fc36a0c7a811862b81cf`

## Machine-readable deletion inventory

`deletion-inventory.json` contains exactly 26 project paths and 249 total file entries.
Every entry records its kind, LF-normalized SHA-256 and capture bundle. Project entries
also state whether they belong to the frozen project inventory and original solution.
`Sunder.Agent.Execution.Common` is explicitly retained. Paths are relative to the
Agent repository root; these are file candidates, not recursive directory deletions.

## Packed consumer provenance

| Path | Authentic index/HEAD blob | SHA-256 |
| --- | --- | --- |
| `fixtures/PackedProtocolConsumer/PackedProtocolConsumer.csproj` | `3f57e8129299d0dfc44064389fe26d43b5451c3b` | `48e43b6e6f7180c73bb640a70d06d9b3f725b4679043e538d9831764d2341b86` |
| `fixtures/PackedProtocolConsumer/Program.cs` | `f12d40164bba5856ab7a13d30827792859b81aaf` | `1475836cb17373aaaacbde6cf42a58c04c1e20a332f7b84fc760d263a4d305e7` |
| `scripts/compatibility/verify-packed-protocol.sh` | `db3340831ad7a492afc7774c02aae4c451ff946f` | `cd537ca7c27711de80a86c217762709a6d10dc7445f368380b87d516da350210` |

The replacement checks use the canonical owner's descriptor and generated-role/archive
coverage. `scripts/compatibility/check-protocol-retirement.mjs` remains a retained
retirement/feed gate; it is not an obsolete verifier or a deletion candidate.

## Constructor/lifecycle correction inverses

The exact transformations in `AgentSourceMigrationProjectChanges.CanonicalBehaviorFixes.cs`
reconcile current corrections without exempting those business sources. Their before
fragments were checked against the index blobs below, and the complete inverse chain
was verified against the unchanged frozen source hashes.

| Current source | Before-fix index blob | Reconstructed frozen SHA-256 |
| --- | --- | --- |
| `src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeBridge.cs` | `6f832a8ac04a7041eaced09baa0e2ae190cfb3f2` | `e4e34f1e0729f4142ec6478cbcf87997e9317264cd2caced428a99f493d40f5a` |
| `src/Sunder.Package.Agent/Runtime/AgentRuntimeTransportBudgetState.cs` | `d7388390626334d1b8b5f1f1eef9ec2a65104a8c` | `222109fcc6ca8466819a0934687a1aada5a9a4c72ea01011e43f8eab37f43e74` |
| `src/Sunder.Package.Agent/Services/AgentLifecycleDispatcher.cs` | `2471be6ba43268f891eb4fc50de8f6eb04ffa615` | `d9cf50d3c41435a94c78b15277046cd63b043f56c834714b0c0738cba422c6cf` |

## Remaining 13 project histories

| Original project | Frozen SHA-256 | Authentic index blob |
| --- | --- | --- |
| `src/Sunder.Agent.Execution.Common/Sunder.Agent.Execution.Common.csproj` | `c1d3a534dcbcf166203d6b8a846665a25dc7a2b8e810bc9fdc2048cb60a84906` | `b470c0edf28f11447e626832d7c73f212749df6b` |
| `tests/Sunder.Package.Agent.Execution.Local.Tests/Sunder.Package.Agent.Execution.Local.Tests.csproj` | `0dbfc96d1f05ffc09f9632e2b7cdf49bdaecb1e104069c14e45115f9107d3d23` | `fca296f0ba7428a677dceddb163ea93c7112ac98` |
| `tests/Sunder.Package.Agent.Memory.Semantic.Tests/Sunder.Package.Agent.Memory.Semantic.Tests.csproj` | `bd6b3e8ef9fc98d52cef02d0a7c3aa8fd653bbd4b21ae366e86788cbe7acc99d` | `a9927ca51dd7241ebfc4966b46c895d88d0ab034` |
| `tests/Sunder.Package.Agent.Presentation.State.Tests/Sunder.Package.Agent.Presentation.State.Tests.csproj` | `051c911674e3fa63716921bba2c41f52987b67e4ee985007eb59dc0518333c9a` | `64fc34c4a888ca11888161691789e57225c478e4` |
| `tests/Sunder.Package.Agent.Presentation.Tests/Sunder.Package.Agent.Presentation.Tests.csproj` | `b3b0d9c824e8c8ddf04dc0847d729b1439cffde51edd11de1885fe42e33f81f2` | `6dd79724507f0189caf9310f31948e4419b7eb4c` |
| `tests/Sunder.Package.Agent.Provider.Anthropic.Tests/Sunder.Package.Agent.Provider.Anthropic.Tests.csproj` | `ecc4349780da3c943c15570af9eed8ffba6bb2a87b019f0177db64bfab669c65` | `6f4076a36db1571f50ff13e2f9e5d3626f2443dd` |
| `tests/Sunder.Package.Agent.Provider.Gemini.Tests/Sunder.Package.Agent.Provider.Gemini.Tests.csproj` | `a016fb0e12ba2dbf92ca44e61e01144170bf6dd52bcb581937b2637dddca7040` | `45b57dfe352c8a99f2baba27b8a4f2d90f2c0f41` |
| `tests/Sunder.Package.Agent.Provider.LMStudio.Tests/Sunder.Package.Agent.Provider.LMStudio.Tests.csproj` | `49d7aa8f3a0fc6a5ad275f5f77ac46a8ed444a6d7d8bd4c9e3f9174114a30d53` | `44c8203e27456d32028722a0b2404ea589ff84cb` |
| `tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj` | `af9584b44c9ebd13a4332dda6aee0b5691a8748a30ebbf3fe556665a0350850b` | `97759160a32b2f307aa766baa921614d5fdd55b5` |
| `tests/Sunder.Package.Agent.Provider.TestSupport/Sunder.Package.Agent.Provider.TestSupport.csproj` | `32026ab92620765f1cae668be828515f28c46db48006f2863c9beebb686399f2` | `0c0a4c19885f56c5ac97eb318d866f9e0ea7308b` |
| `tests/Sunder.Package.Agent.Skills.Tests/Sunder.Package.Agent.Skills.Tests.csproj` | `094064e327b30effbf6bcfa7769b9e8925e329b393681bbbffc6575cc623b167` | `53ef5d74ab07f1d5cae7b83779c6fa39e9cc5d8c` |
| `tests/Sunder.Package.Agent.Subagents.Tests/Sunder.Package.Agent.Subagents.Tests.csproj` | `e1a1ab526e9b1afa3a375cd0f7c1c0500316d16dc184ac62fbf35f49e68b56de` | `17b6c171d09ab7f64746760bf8740529f48676e6` |
| `tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj` | `bc1a6cebb4794d05b932b941f67c00c23578121e4af6d0037503af2e4667b755` | `ba466e2c10af32faf4e089176f91b0322898f5ce` |

## Exact project retirement list (26)

- `fixtures/PackedProtocolConsumer/PackedProtocolConsumer.csproj`
- `src/Sunder.Package.Agent.Builder/App/Sunder.Package.Agent.Builder.App.csproj`
- `src/Sunder.Package.Agent.Builder/Protocol/Sunder.Package.Agent.Builder.Protocol.csproj`
- `src/Sunder.Package.Agent.Contracts/Sunder.Package.Agent.Contracts.csproj`
- `src/Sunder.Package.Agent.Execution.Docker/App/DockerApp.csproj`
- `src/Sunder.Package.Agent.Execution.Docker/Protocol/DockerProtocol.csproj`
- `src/Sunder.Package.Agent.Execution.Local/App/LocalApp.csproj`
- `src/Sunder.Package.Agent.Execution.Local/Protocol/LocalProtocol.csproj`
- `src/Sunder.Package.Agent.Mcp/App/Sunder.Package.Agent.Mcp.App.csproj`
- `src/Sunder.Package.Agent.Mcp/Shared/Sunder.Package.Agent.Mcp.Shared.csproj`
- `src/Sunder.Package.Agent.Memory.Semantic/App/Sunder.Package.Agent.Memory.Semantic.App.csproj`
- `src/Sunder.Package.Agent.Memory.Semantic/Protocol/Sunder.Package.Agent.Memory.Semantic.Protocol.csproj`
- `src/Sunder.Package.Agent.Provider.Anthropic/App/Sunder.Package.Agent.Provider.Anthropic.App.csproj`
- `src/Sunder.Package.Agent.Provider.Anthropic/Protocol/Sunder.Package.Agent.Provider.Anthropic.Protocol.csproj`
- `src/Sunder.Package.Agent.Provider.Gemini/App/Sunder.Package.Agent.Provider.Gemini.App.csproj`
- `src/Sunder.Package.Agent.Provider.Gemini/Protocol/Sunder.Package.Agent.Provider.Gemini.Protocol.csproj`
- `src/Sunder.Package.Agent.Provider.LMStudio/App/Sunder.Package.Agent.Provider.LMStudio.App.csproj`
- `src/Sunder.Package.Agent.Provider.LMStudio/Protocol/Sunder.Package.Agent.Provider.LMStudio.Protocol.csproj`
- `src/Sunder.Package.Agent.Provider.OpenAI/App/Sunder.Package.Agent.Provider.OpenAI.App.csproj`
- `src/Sunder.Package.Agent.Provider.OpenAI/Shared/Sunder.Package.Agent.Provider.OpenAI.Shared.csproj`
- `src/Sunder.Package.Agent.Skills/App/SkillsApp.csproj`
- `src/Sunder.Package.Agent.Skills/Protocol/SkillsProtocol.csproj`
- `src/Sunder.Package.Agent.Subagents/App/SubagentsApp.csproj`
- `src/Sunder.Package.Agent.Subagents/Protocol/SubagentsProtocol.csproj`
- `src/Sunder.Package.Agent/App/Sunder.Package.Agent.App.csproj`
- `src/Sunder.Package.Agent/OwnerProtocol/Sunder.Package.Agent.OwnerProtocol.csproj`

## Obsolete fixture/tool sources (2)

- `fixtures/PackedProtocolConsumer/Program.cs`
- `scripts/compatibility/verify-packed-protocol.sh`

## Exact wrapper retirement list (154)

Each path has an authenticated capture in `retirement-history.json` and an explicit
live replacement/required-test mapping in `retirement-dispositions.json`.

- `src/Sunder.Package.Agent/App/PackageModule.cs`
- `src/Sunder.Package.Agent/App/Runtime/AgentOwnerRpcClient.Legacy.cs`
- `src/Sunder.Package.Agent/App/Runtime/AgentRuntimeOperations.Legacy.cs`
- `src/Sunder.Package.Agent/App/Runtime/AgentWorkspaceEditorBindings.Legacy.cs`
- `src/Sunder.Package.Agent/PackageModule.cs`
- `src/Sunder.Package.Agent/Program.cs`
- `src/Sunder.Package.Agent/Runtime/AgentWorkerComposition.cs`
- `src/Sunder.Package.Agent/Runtime/AgentWorkerProviderCatalog.Legacy.cs`
- `src/Sunder.Package.Agent/Runtime/Rpc/AgentBehaviorBindings.Legacy.cs`
- `src/Sunder.Package.Agent/Runtime/Rpc/AgentChatBindings.Legacy.cs`
- `src/Sunder.Package.Agent/Runtime/Rpc/AgentCleanupBindings.Legacy.cs`
- `src/Sunder.Package.Agent/Runtime/Rpc/AgentEmbeddingBindings.Legacy.cs`
- `src/Sunder.Package.Agent/Runtime/Rpc/AgentExecutionBindings.Legacy.cs`
- `src/Sunder.Package.Agent/Runtime/Rpc/AgentMetadataBindings.Legacy.cs`
- `src/Sunder.Package.Agent/Runtime/Rpc/AgentObservedToolBindings.Legacy.cs`
- `src/Sunder.Package.Agent/Runtime/Rpc/AgentPromptContextBindings.Legacy.cs`
- `src/Sunder.Package.Agent/Runtime/Rpc/AgentSelectableBindings.Legacy.cs`
- `src/Sunder.Package.Agent/Runtime/Rpc/AgentToolBindings.Legacy.cs`
- `src/Sunder.Package.Agent/Runtime/Rpc/AgentToolProfileBindings.Legacy.cs`
- `src/Sunder.Package.Agent/Runtime/Rpc/AgentWorkspacePathBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Builder/App/BuilderAppRpcGateway.cs`
- `src/Sunder.Package.Agent.Builder/App/BuilderWorkspaceExecutionService.Legacy.cs`
- `src/Sunder.Package.Agent.Builder/App/PackageModule.cs`
- `src/Sunder.Package.Agent.Builder/BuilderRpcBridge.cs`
- `src/Sunder.Package.Agent.Builder/BuilderRpcProvider.cs`
- `src/Sunder.Package.Agent.Builder/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Builder/Program.cs`
- `src/Sunder.Package.Agent.Builder/Rpc/BuilderRuntimeService.Legacy.cs`
- `src/Sunder.Package.Agent.Builder/Runtime/BuilderWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Builder/Runtime/BuilderWorkerComposition.cs`
- `src/Sunder.Package.Agent.Contracts/Extensions/AgentProfileSelectableCapabilityChangeObserver.Compatibility.cs`
- `src/Sunder.Package.Agent.Contracts/Rpc/AgentBehaviorLoopRpcBinding.cs`
- `src/Sunder.Package.Agent.Contracts/Rpc/AgentChatProviderRpcBinding.cs`
- `src/Sunder.Package.Agent.Contracts/Rpc/AgentExecutionTargetRpcBinding.cs`
- `src/Sunder.Package.Agent.Contracts/Rpc/AgentObservedToolExecutionRpcBinding.cs`
- `src/Sunder.Package.Agent.Contracts/Rpc/AgentRpcContractDescriptors.cs`
- `src/Sunder.Package.Agent.Contracts/Rpc/AgentRpcSimpleBindings.cs`
- `src/Sunder.Package.Agent.Contracts/Rpc/AgentRpcWire.cs`
- `src/Sunder.Package.Agent.Contracts/Rpc/AgentRuntimeCatalogRpcBinding.cs`
- `src/Sunder.Package.Agent.Contracts/Rpc/AgentToolSourceRpcBinding.cs`
- `src/Sunder.Package.Agent.Contracts/Rpc/AgentWorkspaceResolverRpcBinding.cs`
- `src/Sunder.Package.Agent.Contracts/Runtime/InternalsVisibleTo.cs`
- `src/Sunder.Package.Agent.Execution.Docker/App/AppPackageModule.cs`
- `src/Sunder.Package.Agent.Execution.Docker/App/DockerAppBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Execution.Docker/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Execution.Docker/Program.cs`
- `src/Sunder.Package.Agent.Execution.Docker/Rpc/DockerWorkerBindings.cs`
- `src/Sunder.Package.Agent.Execution.Docker/Runtime/DockerWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Execution.Docker/Runtime/DockerWorkerConfiguration.cs`
- `src/Sunder.Package.Agent.Execution.Local/App/AppPackageModule.cs`
- `src/Sunder.Package.Agent.Execution.Local/App/LocalAppBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Execution.Local/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Execution.Local/Program.cs`
- `src/Sunder.Package.Agent.Execution.Local/Runtime/LocalWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Execution.Local/Runtime/LocalWorkerConfiguration.cs`
- `src/Sunder.Package.Agent.Mcp/App/PackageModule.cs`
- `src/Sunder.Package.Agent.Mcp/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Mcp/Rpc/McpWorkerBindings.cs`
- `src/Sunder.Package.Agent.Mcp/Runtime/LegacyMcpWorkspaceCatalog.cs`
- `src/Sunder.Package.Agent.Mcp/Runtime/McpManagementPayload.cs`
- `src/Sunder.Package.Agent.Mcp/Runtime/McpManagementRpcProvider.Legacy.cs`
- `src/Sunder.Package.Agent.Mcp/Runtime/McpOwnerManagementRpcClient.Legacy.cs`
- `src/Sunder.Package.Agent.Mcp/Services/McpSunderConfigurationSyncService.Legacy.cs`
- `src/Sunder.Package.Agent.Mcp/Shared/Properties/AssemblyInfo.cs`
- `src/Sunder.Package.Agent.Mcp/Worker/McpWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Mcp/Worker/McpWorkerConfiguration.cs`
- `src/Sunder.Package.Agent.Mcp/Worker/Program.cs`
- `src/Sunder.Package.Agent.Memory.Semantic/App/MemoryAppRuntimeGateway.Transport.Legacy.cs`
- `src/Sunder.Package.Agent.Memory.Semantic/App/PackageModule.cs`
- `src/Sunder.Package.Agent.Memory.Semantic/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Memory.Semantic/Program.cs`
- `src/Sunder.Package.Agent.Memory.Semantic/Rpc/MemoryProviderCatalog.cs`
- `src/Sunder.Package.Agent.Memory.Semantic/Runtime/LegacySemanticMemoryRpcCatalog.cs`
- `src/Sunder.Package.Agent.Memory.Semantic/Runtime/MemoryWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Memory.Semantic/Runtime/MemoryWorkerComposition.cs`
- `src/Sunder.Package.Agent.Memory.Semantic/Services/SemanticModelRuntimeResolver.Legacy.cs`
- `src/Sunder.Package.Agent.Provider.Anthropic/App/PackageModule.cs`
- `src/Sunder.Package.Agent.Provider.Anthropic/App/ProviderCredentialOwnerRpcClient.cs`
- `src/Sunder.Package.Agent.Provider.Anthropic/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Provider.Anthropic/Program.cs`
- `src/Sunder.Package.Agent.Provider.Anthropic/Protocol/Properties/AssemblyInfo.cs`
- `src/Sunder.Package.Agent.Provider.Anthropic/ProviderCredentialRpcProvider.cs`
- `src/Sunder.Package.Agent.Provider.Anthropic/Rpc/AnthropicWorkerBindings.cs`
- `src/Sunder.Package.Agent.Provider.Anthropic/Runtime/AnthropicWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Provider.Anthropic/Runtime/AnthropicWorkerComposition.cs`
- `src/Sunder.Package.Agent.Provider.Gemini/App/PackageModule.cs`
- `src/Sunder.Package.Agent.Provider.Gemini/App/ProviderCredentialOwnerRpcClient.cs`
- `src/Sunder.Package.Agent.Provider.Gemini/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Provider.Gemini/Program.cs`
- `src/Sunder.Package.Agent.Provider.Gemini/Protocol/Properties/AssemblyInfo.cs`
- `src/Sunder.Package.Agent.Provider.Gemini/ProviderCredentialRpcProvider.cs`
- `src/Sunder.Package.Agent.Provider.Gemini/Rpc/GeminiWorkerBindings.cs`
- `src/Sunder.Package.Agent.Provider.Gemini/Runtime/GeminiWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Provider.Gemini/Runtime/GeminiWorkerComposition.cs`
- `src/Sunder.Package.Agent.Provider.LMStudio/App/PackageModule.cs`
- `src/Sunder.Package.Agent.Provider.LMStudio/App/ProviderCredentialOwnerRpcClient.cs`
- `src/Sunder.Package.Agent.Provider.LMStudio/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Provider.LMStudio/Program.cs`
- `src/Sunder.Package.Agent.Provider.LMStudio/Protocol/Properties/AssemblyInfo.cs`
- `src/Sunder.Package.Agent.Provider.LMStudio/ProviderCredentialRpcProvider.cs`
- `src/Sunder.Package.Agent.Provider.LMStudio/Rpc/LMStudioWorkerBindings.cs`
- `src/Sunder.Package.Agent.Provider.LMStudio/Runtime/LMStudioWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Provider.LMStudio/Runtime/LMStudioWorkerComposition.cs`
- `src/Sunder.Package.Agent.Provider.OpenAI/App/PackageModule.cs`
- `src/Sunder.Package.Agent.Provider.OpenAI/Management/OpenAiManagementRpcProvider.cs`
- `src/Sunder.Package.Agent.Provider.OpenAI/Management/OpenAiOwnerManagementRpcClient.Legacy.cs`
- `src/Sunder.Package.Agent.Provider.OpenAI/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Provider.OpenAI/Rpc/OpenAiWorkerBindings.cs`
- `src/Sunder.Package.Agent.Provider.OpenAI/Shared/Properties/AssemblyInfo.cs`
- `src/Sunder.Package.Agent.Provider.OpenAI/Worker/OpenAiWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Provider.OpenAI/Worker/OpenAiWorkerConfiguration.cs`
- `src/Sunder.Package.Agent.Provider.OpenAI/Worker/Program.cs`
- `src/Sunder.Package.Agent.Skills/App/PackageModule.cs`
- `src/Sunder.Package.Agent.Skills/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Skills/Program.cs`
- `src/Sunder.Package.Agent.Skills/Runtime/LegacySkillsRuntimeBindings.cs`
- `src/Sunder.Package.Agent.Skills/Runtime/SkillOwnerRpc.Legacy.cs`
- `src/Sunder.Package.Agent.Skills/Runtime/SkillsWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Skills/Runtime/SkillsWorkerComposition.cs`
- `src/Sunder.Package.Agent.Skills/Services/SkillsFeature.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/App/PackageModule.cs`
- `src/Sunder.Package.Agent.Subagents/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Program.cs`
- `src/Sunder.Package.Agent.Subagents/Runtime/LegacySubagentRuntimeBindings.cs`
- `src/Sunder.Package.Agent.Subagents/Runtime/ProviderModelCatalogAdapter.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Runtime/SubagentLocalManagementGateway.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Runtime/SubagentOwnerRpc.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeChangeStream.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeHandler.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Runtime/SubagentsWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Runtime/SubagentsWorkerComposition.cs`
- `src/Sunder.Package.Agent.Subagents/Runtime/SubsessionLocalRuntimeAdapter.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Services/SubagentChildRunCoordinator.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Services/SubagentDescriptorSchema.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Services/SubagentEditorCapabilityCatalog.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Services/SubagentFeature.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Services/SubagentPermissionStatusAdapter.Legacy.cs`
- `src/Sunder.Package.Agent.Subagents/Services/SubagentStackContributor.Legacy.cs`
- `src/Sunder.Package.Agent.Tools.Files/FilesToolSource.Legacy.cs`
- `src/Sunder.Package.Agent.Tools.Files/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Tools.Files/Program.cs`
- `src/Sunder.Package.Agent.Tools.Files/Runtime/FilesExecutionFacets.Legacy.cs`
- `src/Sunder.Package.Agent.Tools.Files/Runtime/FilesWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Tools.Files/Runtime/FilesWorkerComposition.cs`
- `src/Sunder.Package.Agent.Tools.Shell/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Tools.Shell/Program.cs`
- `src/Sunder.Package.Agent.Tools.Shell/Runtime/ShellWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Tools.Shell/Runtime/ShellWorkerComposition.cs`
- `src/Sunder.Package.Agent.Tools.Shell/ShellToolSource.Legacy.cs`
- `src/Sunder.Package.Agent.Tools.Web/PackageDependency.Legacy.cs`
- `src/Sunder.Package.Agent.Tools.Web/Program.cs`
- `src/Sunder.Package.Agent.Tools.Web/Rpc/WebWorkerBindings.cs`
- `src/Sunder.Package.Agent.Tools.Web/Runtime/WebWorkerBindings.Legacy.cs`
- `src/Sunder.Package.Agent.Tools.Web/Runtime/WebWorkerComposition.cs`

## Additional helper-boundary evidence (67)

These captures make removal of the old helper directories possible without retaining
handlers or corpus/support files solely for history. This is separate from the supplied
154-wrapper list. The recorded dispositions identify the current replacement inputs.

- `src/Sunder.Package.Agent.Builder/Protocol/AssemblyInfo.cs`
- `src/Sunder.Package.Agent.Builder/Protocol/BuilderRpcProtocol.cs`
- `src/Sunder.Package.Agent.Builder/Protocol/BuilderRpcWireMapper.cs`
- `src/Sunder.Package.Agent.Builder/Protocol/Contracts/builder-runtime.rpc.json`
- `src/Sunder.Package.Agent.Builder/Protocol/packages.release.lock.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/behavior-loop.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/chat-provider.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/child-run-executor.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/durable-lifecycle-observer.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/embedding-provider.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/execution-target.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/permission-surface.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/profile-capability-consumer.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/prompt-context-contributor.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/run-control.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/runtime-catalog.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/selectable-capability-provider.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/session-cleaner.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/system-prompt-contributor.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/tool-execution-observed.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/tool-source.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/workspace-editor.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/workspace-execution-resolver.rpc.json`
- `src/Sunder.Package.Agent.Contracts/Contracts/workspace-path-migrator.rpc.json`
- `src/Sunder.Package.Agent.Contracts/PublicAPI.Shipped.txt`
- `src/Sunder.Package.Agent.Contracts/PublicAPI.Unshipped.txt`
- `src/Sunder.Package.Agent.Contracts/README.md`
- `src/Sunder.Package.Agent.Contracts/buildTransitive/Sunder.Package.Agent.Protocol.props`
- `src/Sunder.Package.Agent.Contracts/packages.release.lock.json`
- `src/Sunder.Package.Agent.Execution.Docker/Protocol/Contracts/docker-execution-presentation.rpc.json`
- `src/Sunder.Package.Agent.Execution.Docker/Protocol/DockerExecutionPresentationRpc.cs`
- `src/Sunder.Package.Agent.Execution.Docker/Protocol/packages.release.lock.json`
- `src/Sunder.Package.Agent.Execution.Local/Protocol/Contracts/local-execution-presentation.rpc.json`
- `src/Sunder.Package.Agent.Execution.Local/Protocol/LocalExecutionPresentationRpc.cs`
- `src/Sunder.Package.Agent.Execution.Local/Protocol/packages.release.lock.json`
- `src/Sunder.Package.Agent.Mcp/Shared/Contracts/mcp-management.rpc.json`
- `src/Sunder.Package.Agent.Mcp/Shared/McpManagementProtocol.cs`
- `src/Sunder.Package.Agent.Mcp/Shared/packages.release.lock.json`
- `src/Sunder.Package.Agent.Memory.Semantic/Protocol/AssemblyInfo.cs`
- `src/Sunder.Package.Agent.Memory.Semantic/Protocol/Contracts/memory-inspector.rpc.json`
- `src/Sunder.Package.Agent.Memory.Semantic/Protocol/MemoryInspectorRpc.cs`
- `src/Sunder.Package.Agent.Memory.Semantic/Protocol/MemorySemanticProtocol.cs`
- `src/Sunder.Package.Agent.Memory.Semantic/Protocol/packages.release.lock.json`
- `src/Sunder.Package.Agent.Provider.Anthropic/Protocol/AnthropicCredentialProtocol.cs`
- `src/Sunder.Package.Agent.Provider.Anthropic/Protocol/Contracts/credential-settings.rpc.json`
- `src/Sunder.Package.Agent.Provider.Anthropic/Protocol/packages.release.lock.json`
- `src/Sunder.Package.Agent.Provider.Gemini/Protocol/Contracts/credential-settings.rpc.json`
- `src/Sunder.Package.Agent.Provider.Gemini/Protocol/GeminiCredentialProtocol.cs`
- `src/Sunder.Package.Agent.Provider.Gemini/Protocol/packages.release.lock.json`
- `src/Sunder.Package.Agent.Provider.LMStudio/Protocol/Contracts/credential-settings.rpc.json`
- `src/Sunder.Package.Agent.Provider.LMStudio/Protocol/LMStudioCredentialProtocol.cs`
- `src/Sunder.Package.Agent.Provider.LMStudio/Protocol/packages.release.lock.json`
- `src/Sunder.Package.Agent.Provider.OpenAI/Shared/Contracts/openai-management.rpc.json`
- `src/Sunder.Package.Agent.Provider.OpenAI/Shared/OpenAiManagementProtocol.cs`
- `src/Sunder.Package.Agent.Provider.OpenAI/Shared/packages.release.lock.json`
- `src/Sunder.Package.Agent.Skills/Protocol/AssemblyInfo.cs`
- `src/Sunder.Package.Agent.Skills/Protocol/Contracts/skills-owner.rpc.json`
- `src/Sunder.Package.Agent.Skills/Protocol/SkillsOwnerProtocol.cs`
- `src/Sunder.Package.Agent.Skills/Protocol/packages.release.lock.json`
- `src/Sunder.Package.Agent.Subagents/Protocol/AssemblyInfo.cs`
- `src/Sunder.Package.Agent.Subagents/Protocol/Contracts/subagents-owner.rpc.json`
- `src/Sunder.Package.Agent.Subagents/Protocol/SubagentsOwnerProtocol.cs`
- `src/Sunder.Package.Agent.Subagents/Protocol/packages.release.lock.json`
- `src/Sunder.Package.Agent/OwnerProtocol/AgentOwnerRuntimeRpc.cs`
- `src/Sunder.Package.Agent/OwnerProtocol/AssemblyInfo.cs`
- `src/Sunder.Package.Agent/OwnerProtocol/Contracts/agent-owner-runtime.rpc.json`
- `src/Sunder.Package.Agent/OwnerProtocol/packages.release.lock.json`

## Owning checks

The owning class is `AgentCompatibilityCandidateTestsRpcMigrationBaseline`.
The focused added checks are:
- `RetirementHistory_ReconcilesAll57ProjectsWithCurrentRoleTopology`
- `RetirementHistory_ReadsCapturedEvidenceWithoutProductionProjectsOrWrappers`
- `RetainedProjectHistory_RejectsTopologyDriftBeforeReturningIndex`
- `PackedProtocolConsumerHistory_PreservesAuthenticOutOfSolutionEvidence`
- `RetirementDeletionInventory_ContainsExactly26ProjectsAndAuthenticatedFiles`
- `SemanticSourceMap_IsHistoricalDataAndAll142RelocationsRemainLive`
- `PhysicalCleanup_ChangesOnlyRetiredExclusionsAndLegacyBuildMachinery`
- `UiFollowup_ExactInversesReconstructAuthenticatedBeforeImages`
- `CanonicalProjectReplacements_ReconcileFrozenHistoryAndGuardCurrentInputs`
- `CanonicalProjectReplacement_RejectsCurrentDriftBeforeReturningHistory`
- `CanonicalOwnedContractClosure_MatchesEveryFrozenDescriptor`
- `CentralInterfaceRelocations_ReconcileExplicitMovesWithFrozenSourceHashes`

Retain `RpcMigrationBaseline_MatchesDescriptorAndSourceInventory` and the required
`AgentSourceArtifactMigrationTests.CompleteOwnerArtifact_DerivesConsumerAndProviderLocksFromFreshSourcePayload`
integration lane, which validates freshly generated canonical outputs.

Read-only historical consumers use `AgentSourceMigrationProjectChanges.ReadMigrationSourceText`.
Executable probes use current generated roles rather than compile archived handlers.
Meaningful test-project changes require review of the surface guard. Original captures
and the immutable RPC baseline are not rolling acceptance snapshots.
