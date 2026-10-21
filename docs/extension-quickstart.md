# Extension Quickstart

Create one headless package that cleans up its own session notes when Agent deletes
a session. The example uses the current generated service/model surface; select a
matching reviewed Agent candidate. See [service contracts](ordinary-service-refactor.md)
for the current authoring boundary and compatibility policy.

## 1. Create One Package Project

```sh
dotnet new sunder-package --name Acme.SessionNotes --packageId com.acme.sessionnotes --packageName "Acme Session Notes" --output Acme.SessionNotes
dotnet restore Acme.SessionNotes/Acme.SessionNotes.csproj
```

The template creates one authored Library project with `SunderPackageProject=true`.
Keep its coordinated SDK references and package metadata. Sunder generates the
Worker and any App role from this source. New owned services are private by default.

## 2. Select The Extension Contract

Before adding the provider source, select `younics.ai.agent.session.cleaner` from an
explicit local Agent archive:

```sh
dotnet msbuild Acme.SessionNotes/Acme.SessionNotes.csproj -t:ImplementSunderPackageApi -p:SunderPackageId=younics.ai.agent -p:SunderContractId=younics.ai.agent.session.cleaner -p:SunderPackageVersionRange=">=2.0.0 <3.0.0" -p:SunderPackageCandidatePath=/path/to/agent.sunderpkg
```

Retain the tool-written `sunder-contracts.lock.json`. It selects exact descriptors
and model-projection metadata and derives the Agent runtime dependency. A published
selection uses the explicitly chosen `SunderRegistryOrigin` instead of a candidate.
Provider selection grants implementation intent, not consumer access to other APIs.

## 3. Implement The Business Operation

Add `Services/SessionNotesCleaner.cs`:

```csharp
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using ISessionCleanerService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Session_Cleaner.ISessionCleanerService;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace Acme.SessionNotes;

[SunderServiceProvider(typeof(ISessionCleanerService), ProviderId = "acme.session.notes.cleaner")]
public sealed partial class SessionNotesCleaner(IPackageContext context) : ISessionCleanerService
{
    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentServiceIdentity
        {
            Id = "acme.session.notes.cleaner",
            DisplayName = "Acme session notes",
        });
    }

    public async ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken token = default)
    {
        await context.Storage.State.DeleteValueAsync($"session.notes.{sessionId:N}", token);
    }
}
```

Use the same key when storing a session's notes. Deletion is package-local and
idempotent, so a replay can safely repeat it. Core generates provider registration,
dispatch and composition from the attribute and imported interface. The example's
`AgentServiceIdentity` is a generated model, initialized through its properties.

The imported service namespace derives from the ultimate authority package
`younics.ai.agent` and contract `younics.ai.agent.session.cleaner`. The file-local
alias keeps implementation signatures natural. Provider-key aliases remain stable.
Agent authors its business records once; extensions import Models rather than
copying records, writing serializers or maintaining transport wrappers.

One implementation may provide several contracts: repeat `SunderServiceProvider`
with distinct provider IDs and implement each generated interface. Use explicit
interface methods for colliding names, as in
[SkillsFeature](../src/Sunder.Package.Agent.Skills/Services/SkillsFeature.cs).

## 4. Build And Inspect

```sh
dotnet build Acme.SessionNotes/Acme.SessionNotes.csproj --no-restore
dotnet msbuild Acme.SessionNotes/Acme.SessionNotes.csproj -t:PackSunderPackage -p:Configuration=Release
```

Build composes the current-RID `bin/Debug/net10.0/sunder-dev`; Pack produces one
universal `.sunderpkg` for the declared RIDs. Inspect the generated Agent dependency,
session-cleaner bundle and `acme.session.notes.cleaner` Runtime provider. Private-only
owned source needs no Accept/Freeze. Defining a new public API requires its own
explicit review; implementing a selected Agent API does not take ownership of it.

## 5. Consume Services And Resources

`AddSunderPackageApi` selects the complete public API for ordinary consumption.
Imported package APIs are contract-scoped; for example, inject
`Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IPackageApi`
for the selected runtime-catalog contract. For multiple implementations, inject
`IServiceProviders<TService>`, select an authorized entry and call `provider.Service`.
Pass `provider.Reference` when a business value needs a typed exact-provider reference.
Entries are borrowed; generated DI owns transport observation and cleanup.

Use `SunderContent` for content and dispose it and its opened streams normally.
Transient authority stays `[JsonIgnore]`; only an explicit `SunderServiceMember`
opts a public property into service transfer. Neither a generated type nor a
reference supplies a use grant. Server context belongs only on genuine
authenticated-caller policy methods. Longer deadlines are declaration metadata,
such as `SunderMethod(TimeoutSeconds = 2100)`, subject to Host limits.

Optional reference availability is an explicit business choice: mark a transferred,
direct nullable reference property with `SunderOptionalServiceReference` to receive
null on safe exact-provider absence. Nullable syntax alone stays strict; permission,
malformed metadata and unsafe failures propagate. For narrow imported discovery,
declare `[SunderRuntimeUse(typeof(IExecutionTargetService), Actions = ["discover"])]`
on the consuming class after selecting the contract, with this file-local alias:

```csharp
using IExecutionTargetService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Execution_Target.IExecutionTargetService;
```

This grants no invocation.
Replayable content watches can opt into provider-local
`CompleteOnResponseContentRetirement = true`, with consumers resnapshotting/resuming.
Compiled model JSON metadata and empty completion need no authored resolver or Unit.

The AI Agent package remains `2.0.0`; its 19 new public API identities each start
at `1.0.0`. Private contracts follow package `2.0.0`, and the platform-owned
`sunder.stack.contributor` remains `2.0.0`. Use the selected archive's reviewed
tuples rather than overriding every imported contract version.

See the [contract catalog](extension-points.md), [chat/embedding providers](providers.md)
and [Core ordinary-service guide](https://github.com/Younics/sunder-core/blob/main/docs/package-development/ORDINARY-SERVICES.md).
