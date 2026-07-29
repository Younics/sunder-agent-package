using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Agent.Execution.Common;

internal sealed record HostScopedInstructionMount(
    string HostPath,
    string ReportedPath,
    HostReportedPathStyle ReportedPathStyle = HostReportedPathStyle.Native);

internal sealed record HostScopedInstructionProbe(
    string OriginalPath,
    string HostPath,
    string ReportedPath,
    bool IsDirectory,
    string? SelectedMountHostPath = null);

internal static class HostScopedInstructionDiscovery
{
    private const int MaxAncestorDepth = 64;
    private const int MaxDocumentChars = 12_000;
    private const string InstructionFileName = "AGENTS.md";

    public static async ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverAsync(
        IReadOnlyList<HostScopedInstructionMount> mounts,
        IReadOnlyList<HostScopedInstructionProbe> probes,
        CancellationToken cancellationToken,
        ILocalSecureFileSystemHooks? hooks = null)
    {
        ValidateRequest(mounts, probes);
        var roots = new List<OpenedMount>();
        var traversalBudget = new HostTraversalBudget(cancellationToken);
        try
        {
            foreach (var mount in mounts
                         .DistinctBy(item => item.HostPath, HostSecurePathEngine.PathComparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                roots.Add(new OpenedMount(
                    mount,
                    HostSecurePathEngine.OpenRoot(mount.HostPath, hooks, cancellationToken)));
            }

            var scopes = new List<AgentScopedInstructionScope>();
            foreach (var probe in probes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetHostDirectory = probe.IsDirectory
                    ? probe.HostPath
                    : Path.GetDirectoryName(probe.HostPath)
                      ?? throw new InvalidOperationException($"Path '{probe.OriginalPath}' has no parent directory.");
                var root = probe.SelectedMountHostPath is null
                    ? roots
                        .Where(candidate => HostSecurePathEngine.IsSameOrChild(targetHostDirectory, candidate.Root.Path))
                        .OrderByDescending(candidate => candidate.Root.Path.Length)
                        .FirstOrDefault()
                    : roots.FirstOrDefault(candidate =>
                        HostSecurePathEngine.PathComparer.Equals(
                            candidate.Root.Path,
                            Path.GetFullPath(probe.SelectedMountHostPath)));
                if (root is null)
                {
                    continue;
                }
                if (!HostSecurePathEngine.IsSameOrChild(targetHostDirectory, root.Root.Path))
                {
                    throw new InvalidOperationException(
                        $"Path '{probe.OriginalPath}' is not contained by its selected host mount.");
                }

                var targetReportedDirectory = probe.IsDirectory
                    ? probe.ReportedPath
                    : GetReportedParent(probe.ReportedPath, root.Mount.ReportedPathStyle);
                var relativeSegments = HostSecurePathEngine.GetRelativeSegments(root.Root.Path, targetHostDirectory);
                if (relativeSegments.Count + 1 > MaxAncestorDepth)
                {
                    throw new InvalidOperationException(
                        $"Applicable AGENTS.md ancestor chain exceeds the {MaxAncestorDepth}-directory limit.");
                }

                var chain = OpenExistingDirectoryChain(root.Root, relativeSegments, hooks, cancellationToken);
                try
                {
                    if (!probe.IsDirectory && chain.IsComplete)
                    {
                        ValidateFinalFileTarget(
                            root.Root,
                            chain.Directories[^1],
                            Path.GetFileName(probe.HostPath),
                            hooks);
                    }

                    var documents = new List<AgentScopedInstructionDocument>();
                    for (var index = 0; index < chain.Directories.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var reportedDirectory = CombineReportedSegments(
                            root.Mount.ReportedPath,
                            relativeSegments.Take(index),
                            root.Mount.ReportedPathStyle);
                        var document = await TryReadDocumentAsync(
                            root.Root,
                            chain.Directories[index],
                            root.Mount.ReportedPath,
                            reportedDirectory,
                            root.Mount.ReportedPathStyle,
                            traversalBudget,
                            index,
                            hooks,
                            cancellationToken).ConfigureAwait(false);
                        if (document is not null)
                        {
                            documents.Add(document);
                        }
                    }
                    scopes.Add(new AgentScopedInstructionScope(
                        probe.OriginalPath,
                        targetReportedDirectory,
                        root.Mount.ReportedPath,
                        documents,
                        WasTruncated: false,
                        OmittedAncestorCount: 0));
                }
                finally
                {
                    chain.Dispose();
                }
            }

            var targetFingerprint = ComputeHashSegments(
                [
                    "host-secure-target-v3",
                    Environment.OSVersion.Platform.ToString(),
                    .. roots.Select(root => root.Root.Handle.Identity.ToString()),
                ]);
            var scopeFingerprint = ComputeHashSegments(
                roots.SelectMany(root => new[]
                {
                    root.Mount.ReportedPath,
                    root.Root.Path,
                    root.Root.Handle.Identity.ToString(),
                }));
            return new AgentScopedInstructionDiscoveryResult(
                targetFingerprint,
                scopeFingerprint,
                scopes,
                WasTruncated: false)
            {
                ProcessedProbeCount = probes.Count,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException("Secure host scoped-instruction discovery failed.", ex);
        }
        finally
        {
            for (var index = roots.Count - 1; index >= 0; index--)
            {
                roots[index].Dispose();
            }
        }
    }

    private static ExistingDirectoryChain OpenExistingDirectoryChain(
        LocalSecureRoot root,
        IReadOnlyList<string> relativeSegments,
        ILocalSecureFileSystemHooks? hooks,
        CancellationToken cancellationToken)
    {
        var owned = new List<LocalSecureHandle>();
        var directories = new List<LocalSecureHandle> { root.Handle };
        var current = root.Handle;
        try
        {
            foreach (var segment in relativeSegments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeOpen, current.FullPath, segment);
                if (!root.Platform.TryOpenChild(
                        current,
                        segment,
                        root.Volume,
                        requireDirectory: true,
                        out var child))
                {
                    return new ExistingDirectoryChain(directories, owned, isComplete: false);
                }
                owned.Add(child!);
                directories.Add(child!);
                current = child!;
            }
            return new ExistingDirectoryChain(directories, owned, isComplete: true);
        }
        catch
        {
            for (var index = owned.Count - 1; index >= 0; index--)
            {
                owned[index].Dispose();
            }
            throw;
        }
    }

    private static void ValidateFinalFileTarget(
        LocalSecureRoot root,
        LocalSecureHandle parent,
        string name,
        ILocalSecureFileSystemHooks? hooks)
    {
        hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeOpen, parent.FullPath, name);
        if (!root.Platform.TryOpenChild(parent, name, root.Volume, requireDirectory: false, out var target))
        {
            return;
        }
        using var existingTarget = target!;
        if (existingTarget.Kind != LocalSecureNodeKind.RegularFile)
        {
            throw new LocalSecurePathException(
                $"Scoped-instruction file probe is not a regular file: {existingTarget.FullPath}");
        }
    }

    private static async Task<AgentScopedInstructionDocument?> TryReadDocumentAsync(
        LocalSecureRoot root,
        LocalSecureHandle directory,
        string reportedRoot,
        string reportedDirectory,
        HostReportedPathStyle reportedPathStyle,
        HostTraversalBudget traversalBudget,
        int depth,
        ILocalSecureFileSystemHooks? hooks,
        CancellationToken cancellationToken)
    {
        var hasExactName = false;
        foreach (var name in root.Platform.EnumerateNames(directory))
        {
            traversalBudget.Visit(depth, name);
            if (HostSecurePathEngine.IsReservedName(name))
            {
                continue;
            }
            if (string.Equals(name, InstructionFileName, StringComparison.Ordinal))
            {
                hasExactName = true;
                break;
            }
        }
        if (!hasExactName)
        {
            return null;
        }

        hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeOpen, directory.FullPath, InstructionFileName);
        if (!root.Platform.TryOpenChild(
                directory,
                InstructionFileName,
                root.Volume,
                requireDirectory: false,
                out var openedDocument))
        {
            return null;
        }

        using var document = openedDocument!;
        if (document.Kind != LocalSecureNodeKind.RegularFile)
        {
            throw new LocalSecurePathException(
                $"Instruction document is not a regular file: {CombineReportedSegments(reportedDirectory, [InstructionFileName], reportedPathStyle)}");
        }

        await using var stream = new FileStream(document.DuplicateHandle(), FileAccess.Read, 4096, isAsync: false);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        var buffer = new char[MaxDocumentChars + 1];
        var read = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > MaxDocumentChars)
        {
            throw new InvalidOperationException(
                $"Instruction file '{CombineReportedSegments(reportedDirectory, [InstructionFileName], reportedPathStyle)}' exceeds the {MaxDocumentChars}-character limit.");
        }
        if (read == 0)
        {
            return null;
        }

        var content = new string(buffer, 0, read);
        return new AgentScopedInstructionDocument(
            CombineReportedSegments(reportedDirectory, [InstructionFileName], reportedPathStyle),
            reportedRoot,
            reportedDirectory,
            content,
            ComputeHash(content),
            WasTruncated: false);
    }

    private static void ValidateRequest(
        IReadOnlyList<HostScopedInstructionMount> mounts,
        IReadOnlyList<HostScopedInstructionProbe> probes)
    {
        if (mounts.Count == 0
            || probes.Count == 0
            || probes.Count > 64
            || probes.Any(probe => string.IsNullOrWhiteSpace(probe.OriginalPath)
                                   || !Path.IsPathFullyQualified(probe.HostPath)
                                   || string.IsNullOrWhiteSpace(probe.ReportedPath)))
        {
            throw new InvalidOperationException(
                "Secure host scoped-instruction discovery requires configured mounts and 1 to 64 bounded probes.");
        }
    }

    private static string GetReportedParent(string path, HostReportedPathStyle style)
    {
        if (style == HostReportedPathStyle.Native)
        {
            return Path.GetDirectoryName(path)
                   ?? throw new InvalidOperationException($"Path '{path}' has no parent directory.");
        }
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static string CombineReportedSegments(
        string root,
        IEnumerable<string> segments,
        HostReportedPathStyle style)
    {
        if (style == HostReportedPathStyle.Native)
        {
            return segments.Aggregate(root, Path.Combine);
        }
        return segments.Aggregate(
            root,
            (current, segment) => current == "/" ? "/" + segment : current.TrimEnd('/') + "/" + segment);
    }

    private static string ComputeHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ComputeHashSegments(IEnumerable<string> values)
    {
        var material = new StringBuilder();
        foreach (var value in values)
        {
            material.Append(value.Length).Append(':').Append(value);
        }
        return ComputeHash(material.ToString());
    }

    private sealed class OpenedMount(HostScopedInstructionMount mount, LocalSecureRoot root) : IDisposable
    {
        public HostScopedInstructionMount Mount { get; } = mount;

        public LocalSecureRoot Root { get; } = root;

        public void Dispose() => Root.Dispose();
    }

    private sealed class ExistingDirectoryChain(
        IReadOnlyList<LocalSecureHandle> directories,
        IReadOnlyList<LocalSecureHandle> owned,
        bool isComplete) : IDisposable
    {
        public IReadOnlyList<LocalSecureHandle> Directories { get; } = directories;

        public bool IsComplete { get; } = isComplete;

        public void Dispose()
        {
            for (var index = owned.Count - 1; index >= 0; index--)
            {
                owned[index].Dispose();
            }
        }
    }
}
