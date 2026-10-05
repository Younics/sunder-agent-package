using Sunder.Package.Agent.Skills.Services;

namespace Sunder.Package.Agent.Skills.Runtime;

internal sealed record SkillQuery;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum SkillCommandKind
{
    ImportGitHub,
    ImportCommon,
    ImportTransfer,
    Delete,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SkillCommand(
    SkillCommandKind Kind,
    string? Value = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SkillProjection(
    IReadOnlyList<InstalledSkillRecord> Skills,
    IReadOnlyList<string> ImportedSkillIds);

internal sealed record SkillChangeSubscription;
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SkillChanged;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SkillTransferManifest(int ChunkCount, long TotalBytes, string Sha256);
