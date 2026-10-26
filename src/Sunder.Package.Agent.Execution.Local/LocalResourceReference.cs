using System.Globalization;
using System.Text;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

internal sealed class LocalResourceReference : IDisposable
{
    internal const string ReapprovalRequiredErrorCode = "local-resource-reapproval-required";
    private const string Prefix = "local-resource-authority-v4:";
    private const string DirectoryPrefix = "local-operation-directories-v1:";
    private readonly HostApprovalLeaseStore<Grant> _grants;
    private readonly HostApprovalLeaseStore<DirectoryLease> _directoryGrants;
    private readonly object _directoryGate = new();
    private readonly Dictionary<string, DirectoryGroup> _directoryGroups = new(StringComparer.Ordinal);

    public LocalResourceReference(
        TimeSpan? lifetime = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _grants = new HostApprovalLeaseStore<Grant>(lifetime, utcNow);
        _directoryGrants = new HostApprovalLeaseStore<DirectoryLease>(lifetime, utcNow);
    }

    public string Create(
        AgentResourceClaim claim,
        AgentResourceOperationContext operation,
        LocalSecureApprovalLease authority)
    {
        ValidateIssuance(operation);
        return Prefix + _grants.Issue(
            BuildKey(claim, operation),
            new Grant(claim, operation, authority));
    }

    public bool TryRedeem(
        string reference,
        AgentResourceClaim expectedClaim,
        AgentResourceOperationContext expectedOperation,
        out LocalSecureApprovalLease? authority)
    {
        authority = null;
        if (!TryGetToken(reference, out var token)
            || !_grants.TryRedeem(
                token,
                grant => Matches(grant, expectedClaim, expectedOperation),
                out var grant)
            || grant is null)
        {
            return false;
        }

        using (grant)
        {
            authority = grant.TakeAuthority();
            return true;
        }
    }

    public bool IsCurrent(
        string reference,
        AgentResourceClaim expectedClaim,
        AgentResourceOperationContext expectedOperation)
        => TryGetToken(reference, out var token)
           && _grants.Contains(
               token,
               grant => Matches(grant, expectedClaim, expectedOperation));

    public bool Revoke(string reference)
    {
        if (!reference.StartsWith(DirectoryPrefix, StringComparison.Ordinal))
            return TryGetToken(reference, out var token) && _grants.TryRevoke(token);
        var directoryToken = reference[DirectoryPrefix.Length..];
        LocalCreatedDirectoryAuthority? authority = null;
        _directoryGrants.Contains(directoryToken, lease => { authority = lease.Authority; return true; });
        var revoked = _directoryGrants.TryRevoke(directoryToken);
        if (authority?.IsDisposed == true && authority.CleanupFailures.Count > 0)
            throw new InvalidOperationException(string.Join(" | ", authority.CleanupFailures.Take(4)));
        return revoked;
    }

    public void Dispose()
    {
        _grants.Dispose();
        _directoryGrants.Dispose();
    }

    internal string IssueDirectoryOperation(AgentExecutionTargetContext context)
    {
        var operation = context.ResourceOperation ?? throw new InvalidOperationException("A directory operation requires exact invocation ownership.");
        ValidateIssuance(operation);
        var key = DirectoryKey(context, operation);
        DirectoryLease lease;
        lock (_directoryGate)
        {
            if (!_directoryGroups.TryGetValue(key, out var group))
                _directoryGroups.Add(key, group = new());
            group.References++;
            lease = new(this, key, group);
        }
        // Never enter the lease store while holding _directoryGate: expiry can
        // release another directory group, including on its timer thread.
        return DirectoryPrefix + _directoryGrants.Issue(key, lease);
    }

    internal DirectoryLease? BorrowDirectoryOperation(AgentExecutionTargetContext context)
    {
        if (context.ResourceOperation is not { } operation || operation.ActionId != "files.mutate") return null;
        var key = DirectoryKey(context, operation);
        foreach (var reference in context.ApprovedResourceCapabilities)
        {
            if (!reference.StartsWith(DirectoryPrefix, StringComparison.Ordinal)) continue;
            DirectoryLease? borrowed = null;
            _directoryGrants.Contains(reference[DirectoryPrefix.Length..], lease =>
            {
                if (lease.Key != key) return false;
                lock (_directoryGate)
                {
                    lease.Group.References++;
                    borrowed = new(this, key, lease.Group);
                }
                return true;
            });
            if (borrowed is not null) return borrowed;
        }
        return null;
    }

    private static string DirectoryKey(AgentExecutionTargetContext context, AgentResourceOperationContext operation)
    {
        var key = new StringBuilder();
        Append(key, context.SessionId?.ToString("N") ?? string.Empty);
        Append(key, context.Workspace.WorkspaceId);
        Append(key, context.Binding.BindingId);
        Append(key, operation.RunId.ToString("N"));
        Append(key, operation.RunRevision.ToString(CultureInfo.InvariantCulture));
        Append(key, operation.ToolCallId);
        Append(key, operation.ActionId);
        Append(key, operation.WorkspaceGeneration);
        Append(key, operation.BindingGeneration);
        Append(key, operation.ToolOwnerPackageId);
        Append(key, operation.ExecutionTargetOwnerPackageId);
        Append(key, operation.AuthorityActivationId);
        // ResourceIndex deliberately differs between approved sibling paths.
        return key.ToString();
    }

    internal sealed class DirectoryGroup
    {
        internal LocalCreatedDirectoryAuthority Authority { get; } = new();
        internal int References;
    }

    internal sealed class DirectoryLease(LocalResourceReference owner, string key, DirectoryGroup group) : IDisposable
    {
        private int _released;
        internal string Key => key;
        internal DirectoryGroup Group => group;
        internal LocalCreatedDirectoryAuthority Authority => group.Authority;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            var release = false;
            lock (owner._directoryGate)
            {
                if (--group.References == 0)
                {
                    owner._directoryGroups.Remove(key);
                    release = true;
                }
            }
            if (release) group.Authority.Dispose();
        }
    }

    private static bool TryGetToken(string reference, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrEmpty(reference)
            || !reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        token = reference[Prefix.Length..];
        return token.Length > 0;
    }

    private static bool Matches(
        Grant grant,
        AgentResourceClaim expectedClaim,
        AgentResourceOperationContext expectedOperation)
        => string.Equals(
               HostResourceClaim.FingerprintClaim(grant.Claim),
               HostResourceClaim.FingerprintClaim(expectedClaim),
               StringComparison.Ordinal)
           && MatchesExactInvocation(grant.Operation, expectedOperation)
           && HostResourceClaim.MatchesOperation(expectedClaim, expectedOperation);

    private static bool MatchesExactInvocation(
        AgentResourceOperationContext issued,
        AgentResourceOperationContext expected)
        => issued.RunId == expected.RunId
           && issued.RunRevision == expected.RunRevision
           && issued.ResourceIndex == expected.ResourceIndex
           && string.Equals(issued.ToolCallId, expected.ToolCallId, StringComparison.Ordinal)
           && string.Equals(issued.ActionId, expected.ActionId, StringComparison.Ordinal)
           && string.Equals(issued.WorkspaceGeneration, expected.WorkspaceGeneration, StringComparison.Ordinal)
           && string.Equals(issued.BindingGeneration, expected.BindingGeneration, StringComparison.Ordinal)
           && string.Equals(issued.ToolOwnerPackageId, expected.ToolOwnerPackageId, StringComparison.Ordinal)
           && string.Equals(
               issued.ExecutionTargetOwnerPackageId,
               expected.ExecutionTargetOwnerPackageId,
               StringComparison.Ordinal)
           && string.Equals(issued.AuthorityActivationId, expected.AuthorityActivationId, StringComparison.Ordinal);

    private static void ValidateIssuance(AgentResourceOperationContext operation)
    {
        if (!operation.CanIssueOutsideAuthority
            || operation.RunId == Guid.Empty
            || operation.RunRevision <= 0
            || operation.ResourceIndex < 0
            || string.IsNullOrWhiteSpace(operation.ToolCallId)
            || string.IsNullOrWhiteSpace(operation.ActionId)
            || string.IsNullOrWhiteSpace(operation.WorkspaceGeneration)
            || string.IsNullOrWhiteSpace(operation.BindingGeneration)
            || string.IsNullOrWhiteSpace(operation.ToolOwnerPackageId)
            || string.IsNullOrWhiteSpace(operation.ExecutionTargetOwnerPackageId)
            || string.IsNullOrWhiteSpace(operation.AuthorityActivationId))
        {
            throw new InvalidOperationException(
                "Outside Local authority requires an exact host-owned invocation binding.");
        }
    }

    private static string BuildKey(
        AgentResourceClaim claim,
        AgentResourceOperationContext operation)
    {
        var key = new StringBuilder();
        Append(key, HostResourceClaim.FingerprintClaim(claim));
        Append(key, operation.RunId.ToString("N"));
        Append(key, operation.RunRevision.ToString(CultureInfo.InvariantCulture));
        Append(key, operation.ToolCallId);
        Append(key, operation.ActionId);
        Append(key, operation.ResourceIndex.ToString(CultureInfo.InvariantCulture));
        Append(key, operation.AuthorityActivationId);
        return key.ToString();
    }

    private static void Append(StringBuilder builder, string value)
        => builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    private sealed class Grant(
        AgentResourceClaim claim,
        AgentResourceOperationContext operation,
        LocalSecureApprovalLease authority) : IDisposable
    {
        private LocalSecureApprovalLease? _authority = authority;

        public AgentResourceClaim Claim { get; } = claim;

        public AgentResourceOperationContext Operation { get; } = operation;

        public LocalSecureApprovalLease TakeAuthority()
            => Interlocked.Exchange(ref _authority, null)
               ?? throw new ObjectDisposedException(nameof(Grant));

        public void Dispose() => Interlocked.Exchange(ref _authority, null)?.Dispose();
    }
}
