# Chat And Embedding Providers

Provider packages run as supervised workers in the Sunder Runtime. Keep network clients, credentials, model translation, retries, and provider-specific state out of App composition. An optional App module may expose settings through typed owner RPC, but it must not contain provider authority.

## Chat Provider Contract

First-party implementations directly implement generated `IChatProviderService`.
Select `younics.ai.agent.chat.provider` from the reviewed
Agent archive and declare the provider with `SunderServiceProvider` from
`Sunder.Sdk.Services`; Core generates its registration and Worker composition.
Use a file-local alias for the imported service and import the shared Models namespace:

```csharp
using IChatProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Chat_Provider.IChatProviderService;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
```

Model names are shared across
the selected Agent contracts; implementations do not maintain copied wire records.

| Member | Author responsibility |
| --- | --- |
| `DescribeAsync` | Return a stable, case-insensitive `ProviderId`, display name, supported auth modes, streaming support, and interruptibility. Set `PackageId = context.PackageId`. |
| `ListModelsAsync` | Return a bounded snapshot of models. Keep `ModelId` stable after profiles can persist it. Populate context/output limits and `ReleaseDate` when known. |
| `GetReadinessAsync` | Perform cheap configuration/auth checks and return `Ready`, `NeedsConfiguration`, or `Failed`; do not throw for an expected missing key. |
| `GetCapabilitiesAsync` | Describe native tool calls, multiple/streaming calls, attachment media, and token limits for the selected model. Be conservative. |
| `ResolveUtilityModelAsync` | Return the configured utility model id, or `null`. |
| `StreamChatAsync` | Translate the selected provider's streaming behavior into `AgentChatStreamEvent` values, preserving cancellation, content and failures. |

The Runtime resolves a profile's chat binding by case-insensitive `ProviderId`; duplicate ids are ambiguous and the first active provider wins. Model ids are provider-owned strings and should include a provider prefix when the upstream namespace is not globally clear.

### Descriptor And Models

```csharp
public ValueTask<AgentProviderDescriptor> DescribeAsync(CancellationToken token = default)
{
    token.ThrowIfCancellationRequested();
    return ValueTask.FromResult(new AgentProviderDescriptor
    {
        ProviderId = "acme",
        DisplayName = "Acme AI",
        SupportedAuthModes = [AgentAuthMode.ApiKey],
        SupportsStreaming = true,
        SupportsInterruptibleRuns = true,
        PackageId = packageContext.PackageId,
    });
}

public ValueTask<IReadOnlyList<AgentModelDescriptor>> ListModelsAsync(
    CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    return ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>(
    [
        new()
        {
            ModelId = "acme/chat-pro",
            DisplayName = "Chat Pro",
            ContextWindow = 128_000,
            MaxOutputTokens = 16_384,
            IsRecommended = true,
            Variants = null,
            SpeedOptions = null,
            ModeOptions = null,
            ReleaseDate = new DateOnly(2026, 1, 15),
        },
    ]);
}
```

Model `Variants`, `SpeedOptions`, and `ModeOptions` are persisted as `AgentChatModelSettings`. Do not encode a UI-only option into a transient model id. Chat calls carry the selected options in `AgentChatStreamRequest`; provider-specific middleware translates them to upstream request options. Imported records use init properties and support `with` updates.

### Chat Client Behavior

First-party providers retain their local `CreateChatClientAsync` vendor integration
and shared MEAI translation algorithms. These are business adapters behind
`StreamChatAsync`, not another public service interface or transport layer.

- Implement the standard `IChatClient` metadata and streaming/non-streaming paths expected by the chosen provider library.
- Preserve function call ids and names. Tool calls must be valid JSON objects and must respect the depth, property-count, and case-collision policy exposed through `AgentPayloadLimits`; Runtime's RPC resource profile owns transport size.
- The first-party OpenAI transport caps an upstream SSE line and accumulated streamed tool arguments at 1 MiB each. These package-local limits bound memory retained from external provider input and are not Agent RPC limits.
- Do not claim native or multiple tool calling unless translated responses preserve all call/result pairs.
- Distinguish caller cancellation from provider timeout/transient transport interruption. The default behavior loop can persist an interrupted run for resumable failures.
- Stream `UsageContent` when upstream usage is available. Populate `TotalTokenCount` when it represents the complete context usage; otherwise populate both `InputTokenCount` and `OutputTokenCount`. The Runtime uses usage from the successful attempt to decide whether session context should be compacted.
- `ChatOptions` temperature, top-p, frequency-penalty, and presence-penalty values are binary32. Agent RPC widens them losslessly to binary64 on send and rejects non-representable binary64 values with `agent.chat.option-not-representable` on receive; it never silently rounds at the wire boundary.
- Never log API keys, bearer tokens, prompt bodies, attachment bytes, or private provider payloads.
- Use `AgentChatClientContext.LogProviderEventAsync` for best-effort structured package logging. It merges provider/model/correlation attributes and deliberately does not let logging failures break a run.

Use `AgentChatProviderException(message, content, errorCode, innerException)` when the user should see controlled Markdown `content`. Unexpected exceptions are still converted into a failed run, but their message is less suitable for a user-facing remediation path.

## Embedding Provider Contract

First-party implementations directly implement imported `IEmbeddingProviderService`. Select
`younics.ai.agent.embedding.provider` from the reviewed Agent archive and let neutral
provider metadata drive generated registration and Worker composition.

```csharp
using IEmbeddingProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Embedding_Provider.IEmbeddingProviderService;
```

| Member | Author responsibility |
| --- | --- |
| `DescribeAsync` | Stable `ProviderId`, display name, auth modes, and package id. Chat and embedding descriptions may share a business provider id; registration ids remain distinct. |
| `ListModelsAsync` | Return stable model ids, optional dimensions, and recommendation state. |
| `GetReadinessAsync` | Return expected missing configuration as readiness, not an exception. |
| `SpaceIdentityAsync` | Return the provider-owned embedding space identity for the model. |
| `GenerateAsync` | Return one result or `null` only when the input intentionally has no embedding. |
| `GenerateBatchAsync` | Preserve input count and index order. Use `null` at an invalid/empty input's index rather than shifting later results. |

The multi-argument operations take generated Models envelopes:
`CYounics_Ai_Agent_Embedding_Provider_EmbeddingProviderGenerateRequest` (`ModelId`, `Text`)
and `CYounics_Ai_Agent_Embedding_Provider_EmbeddingProviderGenerateBatchRequest`
(`ModelId`, `Texts`). Use these generated types directly. Every non-null
`AgentEmbeddingGenerationResult` repeats the selected `ModelId`; dimensions come
from `Values.Count`. Do not narrow binary64 (`double`) components to `float` at the
service boundary. Validate upstream indexes, counts and consistent dimensions.

Semantic memory resolves the embedding binding on the profile, checks readiness, and degrades to lexical/full-text recall when embeddings are disabled, absent, or unavailable. Background indexing records generation failures and retries bounded work; an embedding outage must not corrupt existing memory generations. Internal semantic-memory similarity scores and compact history-index vector storage may use binary32 because they are domain-internal derived data, not Agent RPC values.

## Utility Models

`IChatProviderService.ResolveUtilityModelAsync` exposes a provider-specific model
for small internal tasks. Return `null` when none is configured.

## Authentication And Settings

- Store credentials only through `IPackageContext.Secrets`.
- Store schema-declared non-secret preferences through `IPackageContext.Settings` and register one `PackageSettingsSchema`.
- Keep operational token caches and provider continuation state in package-owned secret/state storage as appropriate.
- Browser authorization uses Sunder's callback abstractions. Packages must never bind their own callback port.
- Keep App settings views as presentation clients over owner RPC. Do not duplicate secrets or network clients in App DI.
- Project only credential presence to App. Blank input retains the stored credential; non-empty replacements and explicit clears cross to Runtime only as commands, and Runtime responses never echo the value.

## Registration

Add reviewed provider imports to the canonical package lock with the owning tools.
Ultimate authority package, contract and service IDs determine generated namespaces
and interface names. Imported natural interfaces are contract-scoped; provider-key
aliases remain available and stable. The declaration on an actual partial chat provider is:

```csharp
[SunderServiceProvider(typeof(IChatProviderService), ProviderId = "acme.chat")]
```

Import that attribute from `Sunder.Sdk.Services` and implement the generated
provider interface. See [GeminiAgentProvider](../src/Sunder.Package.Agent.Provider.Gemini/GeminiAgentProvider.cs)
and [GeminiEmbeddingProvider](../src/Sunder.Package.Agent.Provider.Gemini/GeminiEmbeddingProvider.cs)
for direct implementations. Core generates Worker entry
points and registration tuples. A chat-only provider needs no placeholder embedding
provider, hand-authored descriptor hashes or manual registration factory.

One real class can bind several contracts with repeated `SunderServiceProvider`
attributes and explicit interface methods for colliding signatures. Provider
selection does not grant consumer actions. Consumers use
`IServiceProviders<IChatProviderService>` / `IServiceProviders<IEmbeddingProviderService>`
and call the chosen entry's `.Service`; `.Reference` carries an exact typed binding.
Entries are borrowed, with generated DI managing retirement and cleanup.

Chat binary content uses `AgentChatContent.Content` (`SunderContent`), with ordinary
content/read-stream disposal and no authored RPC scope. URI content remains a URI;
`AgentChatContent.Json` preserves absence versus explicit JSON null. Transient
authority stays JSON-ignored and transfers only through explicit
`SunderServiceMember` intent. Server context is reserved for authenticated-caller
policy, not content conversion.

Reference availability stays strict by default. A transferred direct nullable
reference property may use `SunderOptionalServiceReference` for safe-absence-as-null
behavior; permission denial and unsafe failures still propagate. A provider that
only checks an imported execution target can declare
`[SunderRuntimeUse(typeof(IExecutionTargetService), Actions = ["discover"])]`
without requesting invocation. The contract must already be selected.

`CompleteOnResponseContentRetirement` is a provider-local opt-in for replayable
content-output watches whose consumers resnapshot/resume, not a blanket chat-stream
success policy. Caller cancellation and business failures remain failures.
Generated service codecs use compiled model JSON metadata without reflection-cache
retention of collectible types; authors add no serializer resolver or Unit wrapper.

Private credential settings can inject generated `ICredentialSettingsService`
directly. Its get/set/clear methods return presence only; App owns presentation
state while Runtime owns the credential store.

## Provider Test Matrix

- Descriptor ids, package ownership, model ids, ordering metadata, and readiness states.
- Missing, invalid, expired, and refreshed credentials without real secrets in fixtures.
- Non-streaming and streaming text, tool calls, multiple tool calls, and cancellation.
- Upstream malformed payloads, partial streams, non-success status, rate limits, and timeout mapping.
- Model option translation and unsupported attachment behavior.
- Embedding empty inputs, batch index preservation, dimensions, and malformed response counts.
- Runtime/App composition boundary: provider implementation and credentials exist only in Runtime.

See first-party patterns under `src/Sunder.Package.Agent.Provider.*` and test support under `tests/Sunder.Package.Agent.Provider.TestSupport`. Next: [Testing extensions](testing-extensions.md).
