using Sunder.Package.Agent.Skills.Services;

namespace Sunder.Package.Agent.Skills.Runtime;

internal sealed record SkillQuery;

internal enum SkillCommandKind
{
    ImportGitHub,
    ImportCommon,
    ImportTransfer,
    Delete,
}

internal sealed record SkillCommand(
    SkillCommandKind Kind,
    string? Value = null);

internal sealed record SkillProjection(
    IReadOnlyList<InstalledSkillRecord> Skills,
    IReadOnlyList<string> ImportedSkillIds);

internal sealed record SkillChangeSubscription;
internal sealed record SkillChanged;

internal sealed record SkillTransferManifest(int ChunkCount, long TotalBytes, string Sha256);
