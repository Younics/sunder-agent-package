using System.Text;
using Sunder.Agent.Execution.Common;

namespace Sunder.Package.Agent.Execution.Docker;

internal sealed record DockerMountRootIdentityChain(
    string ContainerPath,
    string HostPath,
    IReadOnlyList<LocalSecureIdentity> Identities);

internal static class DockerMountRootIdentityChains
{
    public static IReadOnlyList<DockerMountRootIdentityChain> Capture(
        IReadOnlyList<DockerVerifiedMount> mounts)
        => mounts
            .OrderBy(static mount => mount.Mount.ContainerPath, StringComparer.Ordinal)
            .Select(static mount => new DockerMountRootIdentityChain(
                mount.Mount.ContainerPath,
                Path.GetFullPath(mount.Mount.HostPath),
                mount.Root.HandleIdentities.ToArray()))
            .ToArray();

    public static IReadOnlyList<DockerMountRootIdentityChain> Clone(
        IReadOnlyList<DockerMountRootIdentityChain> chains)
        => chains
            .Select(static chain => new DockerMountRootIdentityChain(
                chain.ContainerPath,
                chain.HostPath,
                chain.Identities.ToArray()))
            .ToArray();

    public static bool SequenceEquals(
        IReadOnlyList<DockerMountRootIdentityChain> left,
        IReadOnlyList<DockerMountRootIdentityChain> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].ContainerPath, right[index].ContainerPath, StringComparison.Ordinal)
                || !string.Equals(left[index].HostPath, right[index].HostPath, StringComparison.Ordinal)
                || !left[index].Identities.SequenceEqual(right[index].Identities))
            {
                return false;
            }
        }
        return true;
    }
}

internal static class DockerResourceReference
{
    internal const string ApprovalRequiredErrorCode = "docker-resource-approval-required";
    internal const string ApprovalRequiredMessage =
        "Secure Docker structured file execution requires an exact current docker-resource-v3 approval lease.";
    private const string Prefix = "docker-resource-v3:";
    private static readonly HostApprovalLeaseStore<LeaseValue> Leases = new();

    public static string Create(
        string namespaceFingerprint,
        DockerHostPathBinding path,
        LocalSecureApprovalLease resourceAuthority,
        IReadOnlyList<DockerMountRootIdentityChain> mountIdentityChains)
    {
        var value = new LeaseValue(
            namespaceFingerprint,
            path.ContainerPath,
            path.Mount.ContainerPath,
            path.Mount.HostPath,
            path.HostPath,
            resourceAuthority,
            mountIdentityChains);
        return Prefix + Leases.Issue(BuildKey(value), value);
    }

    public static bool TryRedeem(
        string reference,
        string expectedNamespaceFingerprint,
        DockerHostPathBinding expectedPath,
        IReadOnlyList<DockerMountRootIdentityChain> currentMountIdentityChains,
        out DockerApprovedResourceLease? approvedLease)
    {
        approvedLease = null;
        if (string.IsNullOrEmpty(reference) || !reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Leases.TryRedeem(
                reference[Prefix.Length..],
                value => string.Equals(value.NamespaceFingerprint, expectedNamespaceFingerprint, StringComparison.Ordinal)
                          && string.Equals(value.ContainerPath, expectedPath.ContainerPath, StringComparison.Ordinal)
                          && string.Equals(value.MountContainerPath, expectedPath.Mount.ContainerPath, StringComparison.Ordinal)
                          && HostSecurePathEngine.BindingPathEquals(value.ResourceAuthority.Binding, expectedPath.HostPath)
                          && DockerMountRootIdentityChains.SequenceEquals(
                              value.MountIdentityChains,
                              currentMountIdentityChains)
                          && MatchesSelectedMountRoot(
                              value.ResourceAuthority,
                              expectedPath,
                              currentMountIdentityChains),
                out var redeemed)
            || redeemed is null)
        {
            return false;
        }
        approvedLease = new DockerApprovedResourceLease(
            redeemed.MountContainerPath,
            redeemed.ResourceAuthority,
            redeemed.MountIdentityChains);
        redeemed.Detach();
        return true;
    }

    private static string BuildKey(LeaseValue value)
    {
        var key = new StringBuilder();
        Append(key, value.NamespaceFingerprint);
        Append(key, value.ContainerPath);
        Append(key, value.MountContainerPath);
        Append(key, value.MountHostPath);
        Append(key, value.ResourceHostPath);
        Append(key, value.ResourceAuthority.AuthorityRootIdentity.ToString());
        AppendBinding(key, value.ResourceAuthority.Binding);
        foreach (var chain in value.MountIdentityChains)
        {
            Append(key, chain.ContainerPath);
            Append(key, chain.HostPath);
            foreach (var identity in chain.Identities)
            {
                Append(key, identity.ToString());
            }
            Append(key, string.Empty);
        }
        return key.ToString();
    }

    private static bool MatchesSelectedMountRoot(
        LocalSecureApprovalLease resourceAuthority,
        DockerHostPathBinding expectedPath,
        IReadOnlyList<DockerMountRootIdentityChain> chains)
    {
        var selected = chains.SingleOrDefault(chain => string.Equals(
            chain.ContainerPath,
            expectedPath.Mount.ContainerPath,
            StringComparison.Ordinal));
        return selected is not null
               && string.Equals(selected.HostPath, Path.GetFullPath(expectedPath.Mount.HostPath), StringComparison.Ordinal)
               && resourceAuthority.AuthorityRootIdentityChain.SequenceEqual(selected.Identities);
    }

    private static void AppendBinding(StringBuilder builder, LocalResourceBinding binding)
    {
        Append(builder, binding.FullPath);
        Append(builder, binding.AnchorPath);
        Append(builder, binding.AnchorIdentity.ToString());
        Append(builder, binding.TargetIdentity?.ToString() ?? string.Empty);
        Append(builder, binding.TargetKind?.ToString() ?? string.Empty);
        Append(builder, binding.Exists ? "1" : "0");
        Append(builder, binding.CaseSensitivePath ? "1" : "0");
    }

    private static void Append(StringBuilder builder, string value)
        => builder.Append(value.Length).Append(':').Append(value);

    private sealed class LeaseValue(
        string namespaceFingerprint,
        string containerPath,
        string mountContainerPath,
        string mountHostPath,
        string resourceHostPath,
        LocalSecureApprovalLease resourceAuthority,
        IReadOnlyList<DockerMountRootIdentityChain> mountIdentityChains) : IDisposable
    {
        public string NamespaceFingerprint { get; } = namespaceFingerprint;
        public string ContainerPath { get; } = containerPath;
        public string MountContainerPath { get; } = mountContainerPath;
        public string MountHostPath { get; } = mountHostPath;
        public string ResourceHostPath { get; } = resourceHostPath;
        public LocalSecureApprovalLease ResourceAuthority { get; } = resourceAuthority;
        public IReadOnlyList<DockerMountRootIdentityChain> MountIdentityChains { get; } =
            DockerMountRootIdentityChains.Clone(mountIdentityChains);
        private bool _detached;

        public void Detach() => _detached = true;

        public void Dispose()
        {
            if (_detached)
            {
                return;
            }
            ResourceAuthority.Dispose();
        }
    }
}

internal sealed class DockerResourceApprovalException()
    : InvalidOperationException(DockerResourceReference.ApprovalRequiredMessage);

internal sealed class DockerApprovedResourceLease : IDisposable
{
    private readonly string _mountContainerPath;
    private readonly string _resourcePath;
    private readonly IReadOnlyList<DockerMountRootIdentityChain> _approvedMountIdentityChains;
    private IReadOnlyList<DockerMountRootIdentityChain>? _validatedMountIdentityChains;
    private LocalSecureApprovalLease? _resourceAuthority;

    public DockerApprovedResourceLease(
        string mountContainerPath,
        LocalSecureApprovalLease resourceAuthority,
        IReadOnlyList<DockerMountRootIdentityChain> mountIdentityChains)
    {
        _mountContainerPath = mountContainerPath;
        _resourceAuthority = resourceAuthority;
        _resourcePath = resourceAuthority.Binding.FullPath;
        _approvedMountIdentityChains = DockerMountRootIdentityChains.Clone(mountIdentityChains);
    }

    public LocalSecureApprovalLease TakeResourceAuthority()
    {
        if (_validatedMountIdentityChains is null)
        {
            throw new LocalSecureApprovalChangedException(_resourcePath);
        }
        return Interlocked.Exchange(ref _resourceAuthority, null)
               ?? throw new LocalSecureApprovalChangedException(_resourcePath);
    }

    public LocalSecureRoot ValidateMountGeneration(
        DockerHostPathBinding path,
        IReadOnlyList<DockerMountRootIdentityChain> currentMountIdentityChains)
    {
        var retained = _resourceAuthority
            ?? throw new LocalSecureApprovalChangedException(_resourcePath);
        if (!string.Equals(_mountContainerPath, path.Mount.ContainerPath, StringComparison.Ordinal)
            || !DockerMountRootIdentityChains.SequenceEquals(
                _approvedMountIdentityChains,
                currentMountIdentityChains)
            || !MatchesSelectedMountRoot(retained, path, currentMountIdentityChains))
        {
            throw new LocalSecureApprovalChangedException(path.Mount.HostPath);
        }
        _validatedMountIdentityChains = DockerMountRootIdentityChains.Clone(currentMountIdentityChains);
        return retained.BorrowAuthorityRoot();
    }

    public bool IsConfiguredRootOrAncestor()
    {
        var targetIdentity = _resourceAuthority?.Binding.TargetIdentity;
        return targetIdentity is not null
               && (_validatedMountIdentityChains
                   ?? throw new LocalSecureApprovalChangedException(_resourcePath))
               .Any(chain => chain.Identities.Contains(targetIdentity.Value));
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _resourceAuthority, null)?.Dispose();
    }

    private static bool MatchesSelectedMountRoot(
        LocalSecureApprovalLease resourceAuthority,
        DockerHostPathBinding path,
        IReadOnlyList<DockerMountRootIdentityChain> chains)
    {
        var selected = chains.SingleOrDefault(chain => string.Equals(
            chain.ContainerPath,
            path.Mount.ContainerPath,
            StringComparison.Ordinal));
        return selected is not null
               && string.Equals(selected.HostPath, Path.GetFullPath(path.Mount.HostPath), StringComparison.Ordinal)
               && resourceAuthority.AuthorityRootIdentityChain.SequenceEqual(selected.Identities);
    }
}
