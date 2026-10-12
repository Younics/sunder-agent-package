using Sunder.Package.Agent.Skills.Services;

namespace Sunder.Package.Agent.Skills.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SkillProjection(
    IReadOnlyList<InstalledSkillRecord> Skills,
    IReadOnlyList<string> ImportedSkillIds);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SkillTransferManifest(int ChunkCount, long TotalBytes, string Sha256);
