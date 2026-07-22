# Chat And Embedding Providers

Provider packages run in the Sunder Runtime. Keep network clients, credentials, model translation, retries, and provider-specific state out of App composition. An optional App module may expose settings through typed Runtime operations, but it must not contain provider authority.

## Chat Provider Contract

Implement `IAgentChatProvider` and register it through `PackageExtensionPoints.ChatProviders`.

| Member | Author responsibility |
| --- | --- |
| `Descriptor` | Return a stable, case-insensitive `ProviderId`, display name, supported auth modes, streaming support, and interruptibility. Set `PackageId = context.PackageId`. |
| `GetAvailableModelsAsync` | Return a bounded snapshot of models. Keep `ModelId` stable after profiles can persist it. Populate context/output limits and `ReleaseDate` when known. |
| `GetReadinessAsync` | Perform cheap configuration/auth checks and return `Ready`, `NeedsConfiguration`, or `Failed`; do not throw for an expected missing key. |
| `GetRunCapabilitiesAsync` | Describe native tool calls, multiple/streaming calls, attachment media, and token limits for the selected model. Be conservative. |
| `CreateChatClientAsync` | Return a configured `Microsoft.Extensions.AI.IChatClient` for the exact provider/model context. Honor cancellation and throw a safe `AgentChatProviderException` for expected user-actionable failures. |

The Runtime resolves a profile's chat binding by case-insensitive `ProviderId`; duplicate ids are ambiguous and the first active provider wins. Model ids are provider-owned strings and should include a provider prefix when the upstream namespace is not globally clear.

### Descriptor And Models

```csharp
public AgentProviderDescriptor Descriptor { get; } = new(
    "acme",
    "Acme AI",
    [AgentAuthMode.ApiKey],
    SupportsStreaming: true,
    SupportsInterruptibleRuns: true)
{
    PackageId = packageContext.PackageId,
};

public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
    CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    return ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>(
    [
        new("acme/chat-pro", "Chat Pro", 128_000, 16_384, IsRecommended: true)
        {
            ReleaseDate = new DateOnly(2026, 1, 15),
        },
    ]);
}
```

Model `Variants`, `SpeedOptions`, and `ModeOptions` are persisted as `AgentChatModelSettings`. Do not encode a UI-only option into a transient model id. The Runtime forwards selected values in `AgentBehaviorLoopContext`; provider-specific `IChatClient` middleware may translate them to upstream request options.

### Chat Client Behavior

- Implement the standard `IChatClient` metadata and streaming/non-streaming paths expected by the chosen provider library.
- Preserve function call ids and names. Tool calls must be valid JSON objects and must respect `AgentPayloadLimits`.
- Do not claim native or multiple tool calling unless translated responses preserve all call/result pairs.
- Distinguish caller cancellation from provider timeout/transient transport interruption. The default behavior loop can persist an interrupted run for resumable failures.
- Never log API keys, bearer tokens, prompt bodies, attachment bytes, or private provider payloads.
- Use `AgentChatClientContext.LogProviderEventAsync` for best-effort structured package logging. It merges provider/model/correlation attributes and deliberately does not let logging failures break a run.

Use `AgentChatProviderException(message, content, errorCode, innerException)` when the user should see controlled Markdown `content`. Unexpected exceptions are still converted into a failed run, but their message is less suitable for a user-facing remediation path.

## Embedding Provider Contract

Implement `IAgentEmbeddingProvider` and register it through `PackageExtensionPoints.EmbeddingProviders`.

| Member | Author responsibility |
| --- | --- |
| `Descriptor` | Stable `ProviderId`, display name, auth modes, and package id. Chat and embedding implementations may share a provider id. |
| `GetAvailableModelsAsync` | Return stable model ids, optional dimensions, and recommendation state. |
| `GetReadinessAsync` | Return expected missing configuration as readiness, not an exception. |
| `GenerateEmbeddingAsync` | Return one result or `null` only when the input intentionally has no embedding. |
| `GenerateEmbeddingsAsync` | Preserve input count and index order. Use `null` at an invalid/empty input's index rather than shifting later results. |

Every non-null `AgentEmbeddingGenerationResult` must repeat the selected `ModelId`; `Dimensions` is derived from the returned values. A provider must not mix dimensions for one model generation. Validate upstream response indexes and counts before returning.

Semantic memory resolves the embedding binding on the profile, checks readiness, and degrades to lexical/full-text recall when embeddings are disabled, absent, or unavailable. Background indexing records generation failures and retries bounded work; an embedding outage must not corrupt existing memory generations.

## Utility Models

A chat provider may additionally implement `IAgentUtilityModelProvider` to expose a provider-specific model used for small internal tasks. This is an optional interface on the selected provider, not a separate `PackageExtensionPoint`. Return `null` when no utility model is configured.

## Authentication And Settings

- Store credentials only through `IPackageContext.Secrets`.
- Store schema-declared non-secret preferences through `IPackageContext.Settings` and register one `PackageSettingsSchema`.
- Keep operational token caches and provider continuation state in package-owned secret/state storage as appropriate.
- Browser authorization uses Sunder's callback abstractions. Packages must never bind their own callback port.
- Keep App settings views as presentation clients over Runtime operations. Do not duplicate secrets or network clients in App DI.

## Registration

```csharp
public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
{
    services.AddSingleton<AcmeChatProvider>();
    services.AddSingleton<AcmeEmbeddingProvider>();
}

public void RegisterRuntimeContributions(
    ISunderRuntimeContributionRegistry registry,
    IServiceProvider services)
{
    registry.RegisterExtension(
        PackageExtensionPoints.ChatProviders,
        services.GetRequiredService<AcmeChatProvider>());
    registry.RegisterExtension(
        PackageExtensionPoints.EmbeddingProviders,
        services.GetRequiredService<AcmeEmbeddingProvider>());
}
```

Register only capabilities actually implemented. A chat-only provider does not need a placeholder embedding provider.

## Provider Test Matrix

- Descriptor ids, package ownership, model ids, ordering metadata, and readiness states.
- Missing, invalid, expired, and refreshed credentials without real secrets in fixtures.
- Non-streaming and streaming text, tool calls, multiple tool calls, and cancellation.
- Upstream malformed payloads, partial streams, non-success status, rate limits, and timeout mapping.
- Model option translation and unsupported attachment behavior.
- Embedding empty inputs, batch index preservation, dimensions, and malformed response counts.
- Runtime/App composition boundary: provider implementation and credentials exist only in Runtime.

See first-party patterns under `src/Sunder.Package.Agent.Provider.*` and test support under `tests/Sunder.Package.Agent.Provider.TestSupport`. Next: [Testing extensions](testing-extensions.md).
