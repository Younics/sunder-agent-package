using Sunder.Package.Agent.Skills.Services;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Skills.Runtime;

internal static class SkillRuntimeOperations
{
    internal static readonly PackageRuntimeOperation<SkillQuery, SkillProjection> Query =
        new("skills.query.v1");
    internal static readonly PackageRuntimeOperation<SkillCommand, SkillProjection> Command =
        new("skills.command.v1");
    internal static readonly PackageRuntimeStream<SkillChangeSubscription, SkillChanged> Changes =
        new("skills.changes.v1");
}

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
