# Sunder Package Agent Protocol

Schema-first RPC descriptors and deterministic C# bindings for packages that integrate with the `sunder.package.agent` 2.x family.

```xml
<PackageReference Include="Sunder.Package.Agent.Protocol" Version="[2.0.0,3.0.0)" />
```

Runtime extensions must also declare a `sunder.package.agent` dependency with `>=2.0.0 <3.0.0`. Worker V3 targets pass immutable `SunderWorkerProviderRegistration` entries in `SunderWorkerV3Options` to `SunderWorker.RunAsync`. The Host validates every request, response, and stream event against the bundled contract descriptor.

This project's historical source directory and some public namespaces use `Sunder.Package.Agent.Contracts`. The built assembly, NuGet package, embedded descriptor resource prefix, and generated binding namespaces use `Sunder.Package.Agent.Protocol`. The retained source names are implementation/API organization within this one artifact; they do not represent another package or shared cross-package CLR types.

The package contains exactly 19 checked-in descriptors and generates deterministic DTO/client/provider sources during the build. Its `buildTransitive` assets bundle those descriptors into each consuming Sunder package.

V2 scalar semantics are explicit: identifiers use the `uuid` logical type, UTC timestamps use `utc-date-time`, and numeric model options and embedding components use binary64-compatible JSON numbers. Generated timestamp bindings retain canonical text with one through nine fractional digits; handwritten adapters accept the same range and project it to `DateTimeOffset` tick precision where their public contracts require it.

`Microsoft.Extensions.AI.ChatOptions` exposes temperature, top-p, frequency-penalty, and presence-penalty values as binary32 fields. Agent RPC carries their lossless binary64 widenings. A schema-first caller must therefore send a finite binary64 value that is exactly the widening of a binary32 value; the chat and run-control adapters fail with `agent.chat.option-not-representable` instead of rounding any other schema-valid value.
