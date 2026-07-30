namespace Sunder.Agent.Execution.Common;

internal static class HostSecurePathEngine
{
    internal const int MaxPathDepth = 256;
    internal const int MaxQuarantineEntriesPerDirectory = 1024;
    internal const int MaxQuarantineAccountingEntries = 8192;
    private const string QuarantinePrefix = ".sunder-secure-quarantine-";
    private const string TemporaryPrefix = ".sunder-secure-temp-";
    private const string ChallengePrefix = ".sunder-secure-challenge-";

    public static LocalSecurePathSession OpenAuthorized(
        IReadOnlyList<string> configuredRoots,
        string fullPath,
        LocalSecureApprovalLease? approvedAuthority,
        bool createParents,
        ILocalSecureFileSystemHooks? hooks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fullPath = NormalizePath(fullPath);
        if (approvedAuthority is not null)
        {
            if (!BindingPathEquals(approvedAuthority.Binding, fullPath))
            {
                approvedAuthority.Dispose();
                throw new LocalSecureApprovalChangedException(fullPath);
            }
            return approvedAuthority.OpenSession(createParents, hooks, cancellationToken);
        }
        var configuredRoot = SelectConfiguredRoot(configuredRoots, fullPath);
        if (configuredRoot is not null)
        {
            var root = OpenRoot(configuredRoot, hooks, cancellationToken);
            return LocalSecurePathSession.CreateOwned(root, fullPath, createParents, expectedBinding: null, hooks, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Host path '{fullPath}' is outside configured roots without an exact approved resource lease.");
    }

    public static LocalResourceBinding Probe(
        IReadOnlyList<string> configuredRoots,
        string fullPath,
        ILocalSecureFileSystemHooks? hooks = null,
        CancellationToken cancellationToken = default)
    {
        using var authority = Capture(configuredRoots, fullPath, hooks, cancellationToken);
        return authority.Binding;
    }

    public static LocalSecureApprovalLease Capture(
        IReadOnlyList<string> configuredRoots,
        string fullPath,
        ILocalSecureFileSystemHooks? hooks = null,
        CancellationToken cancellationToken = default,
        bool allowMissingSuffix = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fullPath = NormalizePath(fullPath);
        var selectedRoot = SelectConfiguredRoot(configuredRoots, fullPath);
        var anchorStart = selectedRoot ?? Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Path '{fullPath}' has no filesystem root.");
        var root = OpenRoot(anchorStart, hooks, cancellationToken);
        try
        {
            return CaptureFromRoot(
                root,
                fullPath,
                hooks,
                cancellationToken,
                allowMissingSuffix);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    public static LocalSecureApprovalLease CaptureFromRoot(
        LocalSecureRoot root,
        string fullPath,
        ILocalSecureFileSystemHooks? hooks = null,
        CancellationToken cancellationToken = default,
        bool allowMissingSuffix = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fullPath = NormalizePath(fullPath);
        var descendants = new List<LocalSecureHandle>();
        LocalSecureHandle? retainedTarget = null;
        var ownershipTransferred = false;
        try
        {
            var current = root.Handle;
            var segments = GetRelativeSegments(root.Path, fullPath);
            var caseSensitivePath = OperatingSystem.IsWindows() && current.CaseSensitiveDirectory;
            if (segments.Count == 0)
            {
                ownershipTransferred = true;
                return new LocalSecureApprovalLease(
                    new LocalResourceBinding(
                        fullPath,
                        current.FullPath,
                        current.Identity,
                        current.Identity,
                        current.Kind,
                        Exists: true,
                        caseSensitivePath,
                        TargetIsAuthorityRoot: true),
                    root,
                    descendants,
                    retainedTarget: null,
                    allowMissingSuffix);
            }

            for (var index = 0; index < segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = segments[index];
                var isFinal = index == segments.Count - 1;
                caseSensitivePath |= OperatingSystem.IsWindows() && current.CaseSensitiveDirectory;
                hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeOpen, current.FullPath, segment);
                if (!root.Platform.TryOpenChild(
                        current,
                        segment,
                        root.Volume,
                        requireDirectory: !isFinal,
                        out var child))
                {
                    if (!isFinal && !allowMissingSuffix)
                    {
                        throw new LocalSecurePathNotFoundException(Path.Combine(current.FullPath, segment));
                    }
                    ownershipTransferred = true;
                    return new LocalSecureApprovalLease(
                        new LocalResourceBinding(
                            fullPath,
                            current.FullPath,
                            current.Identity,
                            TargetIdentity: null,
                            TargetKind: null,
                            Exists: false,
                            caseSensitivePath,
                            TargetIsAuthorityRoot: false),
                        root,
                        descendants,
                        retainedTarget: null,
                        allowMissingSuffix);
                }

                if (isFinal)
                {
                    retainedTarget = child!;
                    ownershipTransferred = true;
                    return new LocalSecureApprovalLease(
                        new LocalResourceBinding(
                            fullPath,
                            current.FullPath,
                            current.Identity,
                            retainedTarget.Identity,
                            retainedTarget.Kind,
                            Exists: true,
                            caseSensitivePath,
                            TargetIsAuthorityRoot: false),
                        root,
                        descendants,
                        retainedTarget,
                        allowMissingSuffix);
                }

                descendants.Add(child!);
                current = child!;
            }

            throw new InvalidOperationException("Secure resource capture reached an invalid state.");
        }
        finally
        {
            if (!ownershipTransferred)
            {
                retainedTarget?.Dispose();
                for (var index = descendants.Count - 1; index >= 0; index--)
                {
                    descendants[index].Dispose();
                }
            }
        }
    }

    public static LocalSecureRoot OpenRoot(
        string path,
        ILocalSecureFileSystemHooks? hooks = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = NormalizePath(path);
        var pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Path '{path}' has no filesystem root.");
        var platform = LocalSecureNative.CreatePlatform();
        var handles = new List<LocalSecureHandle>();
        try
        {
            var current = platform.OpenSystemRoot(pathRoot);
            handles.Add(current);
            var volume = current.Identity.Volume;
            foreach (var segment in GetRelativeSegments(pathRoot, fullPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                hooks?.OnCheckpoint(LocalSecureFileSystemCheckpoint.BeforeOpen, current.FullPath, segment);
                current = platform.OpenChild(current, segment, volume, requireDirectory: true);
                handles.Add(current);
            }
            return new LocalSecureRoot(platform, handles, fullPath, volume);
        }
        catch
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }
            throw;
        }
    }

    public static LocalSecureRootIdentitySet OpenRootIdentitySet(
        IReadOnlyList<string> paths,
        ILocalSecureFileSystemHooks? hooks = null,
        CancellationToken cancellationToken = default)
    {
        var roots = new List<LocalSecureRoot>(paths.Count);
        try
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                roots.Add(OpenRoot(path, hooks, cancellationToken));
            }
            return new LocalSecureRootIdentitySet(roots);
        }
        catch
        {
            for (var index = roots.Count - 1; index >= 0; index--)
            {
                roots[index].Dispose();
            }
            throw;
        }
    }

    public static LocalConfiguredRootResolution ClassifyConfiguredRoot(
        IReadOnlyList<string> configuredRoots,
        LocalSecureApprovalLease authority,
        ILocalSecureFileSystemHooks? hooks = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoots = configuredRoots
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(PathComparer)
            .ToArray();
        if (OperatingSystem.IsWindows())
        {
            var lexicalRoot = SelectConfiguredRoot(normalizedRoots, authority.Binding.FullPath);
            if (lexicalRoot is null)
            {
                return LocalConfiguredRootResolution.Outside(
                    LocalConfiguredScopeClassificationBasis.LexicalContainment);
            }

            try
            {
                using var root = OpenRoot(lexicalRoot, hooks, cancellationToken);
                return IsIdentityChainPrefix(root.HandleIdentities, authority.RetainedPathIdentities)
                    ? LocalConfiguredRootResolution.Configured(
                        lexicalRoot,
                        LocalConfiguredScopeClassificationBasis.LexicalAndOpenedIdentity)
                    : LocalConfiguredRootResolution.Unknown();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
            {
                return LocalConfiguredRootResolution.Unknown();
            }
        }

        string? matchedRoot = null;
        var matchedDepth = -1;
        var unresolvedRoot = false;
        foreach (var configuredRoot in normalizedRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var root = OpenRoot(configuredRoot, hooks, cancellationToken);
                if (root.HandleIdentities.Count > matchedDepth
                    && IsIdentityChainPrefix(root.HandleIdentities, authority.RetainedPathIdentities))
                {
                    matchedRoot = configuredRoot;
                    matchedDepth = root.HandleIdentities.Count;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
            {
                unresolvedRoot = true;
            }
        }

        if (matchedRoot is not null)
        {
            return LocalConfiguredRootResolution.Configured(
                matchedRoot,
                LocalConfiguredScopeClassificationBasis.OpenedAncestorIdentity);
        }
        return unresolvedRoot
            ? LocalConfiguredRootResolution.Unknown()
            : LocalConfiguredRootResolution.Outside(
                LocalConfiguredScopeClassificationBasis.OpenedAncestorIdentity);
    }

    public static string? SelectConfiguredRoot(IReadOnlyList<string> configuredRoots, string fullPath)
        => configuredRoots
            .Select(NormalizePath)
            .Where(root => IsSameOrChild(fullPath, root))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();

    public static bool IsSameOrChild(string candidate, string root)
    {
        candidate = NormalizePath(candidate);
        root = NormalizePath(root);
        if (PathComparer.Equals(candidate, root))
        {
            return true;
        }

        var pathRoot = Path.GetPathRoot(root);
        return string.Equals(root, pathRoot, PathComparison)
            ? candidate.StartsWith(root, PathComparison)
            : candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    internal static IReadOnlyList<string> GetRelativeSegments(string root, string target)
    {
        root = NormalizePath(root);
        target = NormalizePath(target);
        var relative = Path.GetRelativePath(root, target);
        if (relative == ".")
        {
            return [];
        }
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Path '{target}' is not contained by '{root}'.");
        }
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > MaxPathDepth)
        {
            throw new LocalSecurePathException(
                $"Secure filesystem paths support at most {MaxPathDepth} components.");
        }
        if (segments.Any(IsReservedName))
        {
            throw new LocalSecurePathException("Reserved secure-filesystem internal paths cannot be accessed directly.");
        }
        return segments;
    }

    private static bool IsIdentityChainPrefix(
        IReadOnlyList<LocalSecureIdentity> expectedPrefix,
        IReadOnlyList<LocalSecureIdentity> candidate)
    {
        if (expectedPrefix.Count > candidate.Count)
        {
            return false;
        }
        for (var index = 0; index < expectedPrefix.Count; index++)
        {
            if (expectedPrefix[index] != candidate[index])
            {
                return false;
            }
        }
        return true;
    }

    internal static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool IsQuarantineName(string name)
        => name.StartsWith(QuarantinePrefix, ReservedNameComparison);

    internal static bool IsTemporaryName(string name)
        => name.StartsWith(TemporaryPrefix, ReservedNameComparison);

    internal static bool IsChallengeName(string name)
        => name.StartsWith(ChallengePrefix, ReservedNameComparison);

    internal static bool IsReservedName(string name)
        => IsQuarantineName(name) || IsTemporaryName(name) || IsChallengeName(name);

    internal static string CreateQuarantineName()
        => QuarantinePrefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    internal static string CreateTemporaryName()
        => TemporaryPrefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    internal static string CreateChallengeName()
        => ChallengePrefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    internal static bool BindingPathEquals(LocalResourceBinding binding, string path)
        => string.Equals(
            NormalizePath(binding.FullPath),
            NormalizePath(path),
            binding.CaseSensitivePath || !OperatingSystem.IsWindows()
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase);

    private static StringComparison ReservedNameComparison
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, PathComparison)
            ? root!
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

internal enum LocalConfiguredScopeClassification
{
    Configured,
    Outside,
    Unknown,
}

internal enum LocalConfiguredScopeClassificationBasis
{
    OpenedAncestorIdentity,
    LexicalContainment,
    LexicalAndOpenedIdentity,
    Unresolved,
}

internal sealed record LocalConfiguredRootResolution(
    LocalConfiguredScopeClassification Classification,
    string? ConfiguredRoot,
    LocalConfiguredScopeClassificationBasis Basis)
{
    public static LocalConfiguredRootResolution Configured(
        string configuredRoot,
        LocalConfiguredScopeClassificationBasis basis)
        => new(LocalConfiguredScopeClassification.Configured, configuredRoot, basis);

    public static LocalConfiguredRootResolution Outside(
        LocalConfiguredScopeClassificationBasis basis)
        => new(LocalConfiguredScopeClassification.Outside, null, basis);

    public static LocalConfiguredRootResolution Unknown()
        => new(
            LocalConfiguredScopeClassification.Unknown,
            ConfiguredRoot: null,
            LocalConfiguredScopeClassificationBasis.Unresolved);
}

internal sealed class LocalSecureRootIdentitySet(IReadOnlyList<LocalSecureRoot> roots) : IDisposable
{
    public IReadOnlyList<IReadOnlyList<LocalSecureIdentity>> IdentityChains { get; } = roots
        .Select(static root => (IReadOnlyList<LocalSecureIdentity>)root.HandleIdentities.ToArray())
        .ToArray();

    public bool Contains(LocalSecureIdentity identity)
        => IdentityChains.Any(chain => chain.Contains(identity));

    public void Dispose()
    {
        for (var index = roots.Count - 1; index >= 0; index--)
        {
            roots[index].Dispose();
        }
    }
}

internal sealed class LocalSecureRoot(
    ILocalSecureFileSystemPlatform platform,
    IReadOnlyList<LocalSecureHandle> handles,
    string path,
    ulong volume) : IDisposable
{
    public ILocalSecureFileSystemPlatform Platform { get; } = platform;

    public LocalSecureHandle Handle => handles[^1];

    public string Path { get; } = path;

    public ulong Volume { get; } = volume;

    public IReadOnlyList<LocalSecureIdentity> HandleIdentities { get; } = handles
        .Select(static handle => handle.Identity)
        .ToArray();

    internal IReadOnlyList<LocalSecureHandle> TakeHandles()
    {
        if (Interlocked.Exchange(ref _handlesTaken, 1) != 0)
        {
            throw new ObjectDisposedException(nameof(LocalSecureRoot));
        }
        return handles;
    }

    private int _handlesTaken;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _handlesTaken, 1) != 0)
        {
            return;
        }
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }
    }
}

internal sealed class LocalSecureApprovalLease(
    LocalResourceBinding binding,
    LocalSecureRoot authorityRoot,
    IReadOnlyList<LocalSecureHandle> retainedAncestors,
    LocalSecureHandle? retainedTarget,
    bool allowMissingSuffix = false) : IDisposable
{
    private readonly object _syncRoot = new();
    private LocalSecureRoot? _authorityRoot = authorityRoot;
    private IReadOnlyList<LocalSecureHandle>? _retainedAncestors = retainedAncestors;
    private LocalSecureHandle? _retainedTarget = retainedTarget;

    public LocalResourceBinding Binding { get; } = binding;

    public LocalSecureIdentity RetainedAnchorIdentity => Binding.AnchorIdentity;

    public string AuthorityRootPath { get; } = authorityRoot.Path;

    public LocalSecureIdentity AuthorityRootIdentity { get; } = authorityRoot.Handle.Identity;

    public IReadOnlyList<LocalSecureIdentity> AuthorityRootIdentityChain { get; } =
        authorityRoot.HandleIdentities.ToArray();

    public IReadOnlyList<LocalSecureIdentity> RetainedPathIdentities { get; } = authorityRoot.HandleIdentities
        .Concat(retainedAncestors.Select(static handle => handle.Identity))
        .Concat(retainedTarget is null ? [] : [retainedTarget.Identity])
        .ToArray();

    public LocalSecureRoot BorrowAuthorityRoot()
    {
        lock (_syncRoot)
        {
            return _authorityRoot ?? throw new LocalSecureApprovalChangedException(Binding.FullPath);
        }
    }

    public LocalSecurePathSession OpenSession(
        bool createParents,
        ILocalSecureFileSystemHooks? hooks,
        CancellationToken cancellationToken)
    {
        LocalSecureRoot root;
        IReadOnlyList<LocalSecureHandle> ancestors;
        LocalSecureHandle? target;
        lock (_syncRoot)
        {
            root = _authorityRoot ?? throw new LocalSecureApprovalChangedException(Binding.FullPath);
            ancestors = _retainedAncestors ?? throw new LocalSecureApprovalChangedException(Binding.FullPath);
            target = _retainedTarget;
            _authorityRoot = null;
            _retainedAncestors = null;
            _retainedTarget = null;
        }
        return LocalSecurePathSession.CreateApproved(
            root,
            ancestors,
            target,
            Binding,
            createParents,
            allowMissingSuffix,
            hooks,
            cancellationToken);
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _retainedTarget?.Dispose();
            _retainedTarget = null;
            if (_retainedAncestors is not null)
            {
                for (var index = _retainedAncestors.Count - 1; index >= 0; index--)
                {
                    _retainedAncestors[index].Dispose();
                }
                _retainedAncestors = null;
            }
            _authorityRoot?.Dispose();
            _authorityRoot = null;
        }
    }
}
