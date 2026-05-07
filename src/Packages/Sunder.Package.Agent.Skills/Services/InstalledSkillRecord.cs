namespace Sunder.Package.Agent.Skills.Services;

public sealed record InstalledSkillRecord(
    string SkillId,
    string RelativeRootPath,
    string? Name,
    string? Description,
    string? Version,
    string? Author,
    string SourceKind,
    string? SourceUri,
    string? SourceRef,
    string? ResolvedCommitSha,
    string ContentHash,
    DateTimeOffset InstalledAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<string> Warnings);

internal sealed record ParsedSkillMarkdown(
    string? Name,
    string? Description,
    string? Version,
    string? Author,
    string Body,
    string RawContent,
    IReadOnlyDictionary<string, string> Metadata);
