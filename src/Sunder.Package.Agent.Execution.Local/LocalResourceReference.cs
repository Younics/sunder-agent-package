using System.Globalization;
using System.Text;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

internal sealed class LocalResourceReference : IDisposable
{
    internal const string ReapprovalRequiredErrorCode = "local-resource-reapproval-required";
    private const string Prefix = "local-resource-authority-v4:";
    private readonly HostApprovalLeaseStore<Grant> _grants;

    public LocalResourceReference(
        TimeSpan? lifetime = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _grants = new HostApprovalLeaseStore<Grant>(lifetime, utcNow);
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
        => TryGetToken(reference, out var token) && _grants.TryRevoke(token);

    public void Dispose() => _grants.Dispose();

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
