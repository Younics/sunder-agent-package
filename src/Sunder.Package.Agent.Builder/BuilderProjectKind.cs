using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

/// <summary>The authoring toolchain used by a local Sunder package project.</summary>
[SunderPackageCodeRole(SunderPackageCodeRole.Shared)]
public enum BuilderProjectKind
{
    Dotnet,
    Node,
}
