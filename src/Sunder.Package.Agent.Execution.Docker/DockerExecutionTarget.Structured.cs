using System.Security.Cryptography;
using System.Text;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed partial class DockerExecutionTarget
{
    private async Task<DockerExecutionRuntimeConfig> BuildRuntimeConfigAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetCurrentConfigurationAsync(context, cancellationToken);
        if (context.ExpectedConfigurationGeneration is not null
            && snapshot.WorkspaceConfig.ImageReference is not null
            && snapshot.ImageIdentity is null)
        {
            throw new InvalidOperationException(
                "The Docker image identity is not available for this approved configuration; explicit reapproval is required after target readiness completes.");
        }
        return _configService.BuildRuntimeConfig(
            context.Binding.BindingId,
            context.Workspace,
            snapshot.WorkspaceConfig) with
        {
            DockerCliPath = snapshot.DockerCliPath,
            DefaultTimeoutSeconds = snapshot.DefaultTimeoutSeconds,
            ImageIdentity = context.ExpectedConfigurationGeneration is null
                ? null
                : snapshot.ImageIdentity,
        };
    }

    private async Task<DockerExecutionConfigurationSnapshot> GetCurrentConfigurationAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = await CaptureCurrentConfigurationAsync(
            context.Binding.BindingId,
            cancellationToken);
        if (context.ExpectedConfigurationGeneration is { } expected
            && !string.Equals(
                expected,
                _configService.CreateGeneration(context.Binding.BindingId, snapshot),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Docker execution configuration changed after permission planning; explicit reapproval is required.");
        }

        return snapshot;
    }

    internal string BuildStructuredNamespaceFingerprint(
        DockerExecutionRuntimeConfig config,
        string bindingId,
        IReadOnlyList<DockerMountRootIdentityChain> mountIdentityChains)
    {
        var builder = new StringBuilder();
        AppendFingerprintSegment(builder, "docker-host-structured-v3");
        AppendFingerprintSegment(builder, ContainerSecurityPolicyVersion);
        AppendFingerprintSegment(builder, GetVerifiedDaemonIdentity());
        AppendFingerprintSegment(builder, DockerHostAccessPolicy.Resolve().Signature);
        AppendFingerprintSegment(builder, bindingId);
        AppendFingerprintSegment(builder, ResolveContainerName(config, bindingId));
        AppendFingerprintSegment(builder, GetVerifiedContainerSignature(ResolveContainerName(config, bindingId)));
        AppendFingerprintSegment(builder, config.ImageReference ?? string.Empty);
        foreach (var chain in mountIdentityChains)
        {
            AppendFingerprintSegment(builder, chain.ContainerPath);
            AppendFingerprintSegment(builder, chain.HostPath);
            AppendFingerprintSegment(builder, chain.Identities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var identity in chain.Identities)
            {
                AppendFingerprintSegment(builder, identity.ToString());
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private DockerStructuredIdentitySnapshot CaptureStructuredIdentity(
        DockerExecutionRuntimeConfig config,
        string bindingId,
        DockerStructuredOperationLease operation)
    {
        var chains = DockerMountRootIdentityChains.Capture(operation.Mounts);
        return new DockerStructuredIdentitySnapshot(
            BuildStructuredNamespaceFingerprint(config, bindingId, chains),
            chains);
    }

    private Task<DockerContainerLifecycleService.DockerContainerLease> AcquireContainerAsync(
        AgentExecutionTargetContext context,
        DockerExecutionRuntimeConfig config,
        CancellationToken cancellationToken)
    {
        var container = ResolveContainerName(config, context.Binding.BindingId);
        return _lifecycleService.AcquireAsync(
            container,
            async ct => await EnsureContainerAsync(context, config, ct)
                        ?? throw new InvalidOperationException("Docker container is unavailable."),
            StopContainerAsync,
            cancellationToken);
    }

    private async Task<DockerStructuredOperationLease> AcquireStructuredOperationAsync(
        AgentExecutionTargetContext context,
        DockerExecutionRuntimeConfig config,
        CancellationToken cancellationToken)
    {
        var container = await AcquireContainerAsync(context, config, cancellationToken).ConfigureAwait(false);
        DockerVerifiedMount[]? mounts = null;
        try
        {
            mounts = OpenVerifiedMounts(config.Mounts, cancellationToken);
            await _mountIdentityVerifier.VerifyAsync(
                container.ContainerName,
                config,
                mounts,
                RunDockerAsync,
                cancellationToken).ConfigureAwait(false);
            return new DockerStructuredOperationLease(container, mounts);
        }
        catch
        {
            if (mounts is not null)
            {
                foreach (var mount in mounts)
                {
                    mount.Root.Dispose();
                }
            }
            container.Dispose();
            throw;
        }
    }

    private static DockerApprovedResourceLease RedeemResourceApproval(
        AgentExecutionTargetContext context,
        DockerHostPathBinding path,
        DockerStructuredIdentitySnapshot identity)
    {
        if (context.ApprovedResourceReferences.Count is 0 or > 128)
        {
            throw new DockerResourceApprovalException();
        }
        foreach (var reference in context.ApprovedResourceReferences)
        {
            if (DockerResourceReference.TryRedeem(
                    reference,
                    identity.NamespaceFingerprint,
                    path,
                    identity.MountIdentityChains,
                    out var approvedLease))
            {
                return approvedLease!;
            }
        }
        throw new DockerResourceApprovalException();
    }

    private async Task VerifyApprovedMountAsync(
        DockerStructuredOperationLease operation,
        DockerExecutionRuntimeConfig config,
        DockerHostPathBinding path,
        DockerApprovedResourceLease approval,
        IReadOnlyList<DockerMountRootIdentityChain> currentMountIdentityChains,
        CancellationToken cancellationToken)
    {
        var retained = approval.ValidateMountGeneration(path, currentMountIdentityChains);
        await _mountIdentityVerifier.VerifyAsync(
            operation.ContainerName,
            config,
            [new DockerVerifiedMount(path.Mount, retained)],
            RunDockerAsync,
            cancellationToken).ConfigureAwait(false);
    }

    private static void AppendFingerprintSegment(StringBuilder builder, string value)
        => builder.Append(value.Length).Append(':').Append(value);

    private static LocalSecureApprovalLease CaptureConfiguredResourceAuthority(
        AgentExecutionTargetContext context,
        DockerHostPathBinding path,
        DockerStructuredIdentitySnapshot identity,
        DockerStructuredOperationLease operation,
        CancellationToken cancellationToken)
    {
        if (context.ApprovedResourceClaims.Count > 128
            || context.ApprovedResourceClaims.Count > 0 && context.ResourceOperation is null)
        {
            throw new LocalSecureApprovalChangedException(path.ContainerPath);
        }

        var mountRoot = operation.TakeMountRoot(path.Mount);
        LocalSecureApprovalLease? authority = null;
        try
        {
            authority = HostSecurePathEngine.CaptureFromRoot(
                mountRoot,
                path.HostPath,
                cancellationToken: cancellationToken,
                allowMissingSuffix: true);
            if (context.ApprovedResourceClaims.Count == 0)
            {
                return authority;
            }

            var expectedNamespace = DockerFileSystemExecutor.ClaimNamespacePrefix
                                    + identity.NamespaceFingerprint;
            var operationContext = context.ResourceOperation;
            var matches = context.ApprovedResourceClaims
                .Where(claim => string.Equals(claim.NamespaceId, expectedNamespace, StringComparison.Ordinal)
                                && string.Equals(claim.LogicalPath, path.ContainerPath, StringComparison.Ordinal)
                                && string.Equals(
                                    claim.ConfiguredRoot,
                                    path.Mount.ContainerPath,
                                    StringComparison.Ordinal)
                                && (operationContext is null
                                    || string.Equals(
                                        claim.ActionId,
                                        operationContext.ActionId,
                                        StringComparison.Ordinal)
                                    && string.Equals(
                                        claim.ToolCallId,
                                        operationContext.ToolCallId,
                                        StringComparison.Ordinal)))
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
            {
                throw new LocalSecureApprovalChangedException(path.ContainerPath);
            }

            var claim = matches[0];
            var exactContext = operationContext is null
                ? context
                : context with
                {
                    ResourceOperation = operationContext with { ResourceIndex = claim.ResourceIndex },
                };
            HostResourceClaim.Validate(
                claim,
                expectedNamespace,
                path.ContainerPath,
                path.Mount.ContainerPath,
                authority,
                exactContext);
            return authority;
        }
        catch
        {
            if (authority is null)
            {
                mountRoot.Dispose();
            }
            else
            {
                authority.Dispose();
            }
            throw;
        }
    }

    private AgentFileMutationResult AttachPostMutationResource(
        DockerExecutionRuntimeConfig config,
        AgentExecutionTargetContext context,
        string path,
        DockerStructuredIdentitySnapshot identity,
        AgentFileMutationResult result,
        ref LocalSecureApprovalLease? postMutationAuthority)
    {
        if (postMutationAuthority is null)
        {
            return result;
        }

        var authority = postMutationAuthority;
        postMutationAuthority = null;
        return result with
        {
            PostMutationResource = _fileSystemExecutor.ResolveFileResource(
                config,
                path,
                identity.NamespaceFingerprint,
                CancellationToken.None,
                authority,
                identity.MountIdentityChains,
                context),
        };
    }

    private sealed record DockerStructuredIdentitySnapshot(
        string NamespaceFingerprint,
        IReadOnlyList<DockerMountRootIdentityChain> MountIdentityChains);
}
