namespace Sunder.Package.Agent.Skills.Runtime;

internal static class SkillTransferLimits
{
    public const int MaxFiles = 2_000;
    public const int MaxPathDepth = 16;
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxArchiveBytes = 50 * 1024 * 1024;
    public const int MaxExpandedBytes = 50 * 1024 * 1024;
    public const int ChunkBytes = 8 * 1024 * 1024;
    public const int MaxChunkCount = 7;
    public const int MaxManifestBytes = 64 * 1024;
}
