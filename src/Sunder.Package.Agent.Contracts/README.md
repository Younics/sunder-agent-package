# Sunder Package Agent Protocol

Schema-first RPC descriptors and deterministic C# bindings for packages that integrate with the `sunder.package.agent` 2.x family.

```xml
<PackageReference Include="Sunder.Package.Agent.Protocol" Version="[2.0.0,3.0.0)" />
```

Runtime extensions must also declare a `sunder.package.agent` dependency with `>=2.0.0 <3.0.0`. Providers are published with `RegisterRpcProvider`; the Host validates every request, response, and stream event against the bundled contract descriptor.

This project's historical source directory and some public namespaces use `Sunder.Package.Agent.Contracts`. The built assembly, NuGet package, embedded descriptor resource prefix, and generated binding namespaces use `Sunder.Package.Agent.Protocol`. The retained source names are implementation/API organization within this one artifact; they do not represent another package or shared cross-package CLR types.

The package contains exactly 18 checked-in descriptors and deterministic generated DTO/client/provider sources. Its `buildTransitive` assets bundle those descriptors into each consuming Sunder package.
