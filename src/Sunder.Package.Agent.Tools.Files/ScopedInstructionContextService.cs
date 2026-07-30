using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Files;

internal sealed partial class ScopedInstructionContextService(IPackageContext packageContext) : IAgentSessionDataCleaner
{
    private const int MaxMutationPaths = 64;
    private const int MaxRenderedDocuments = 20;
    private const int MaxRenderedChars = 48_000;
    private const int MaxDocumentChars = 12_000;
    private const int MaxVisibleErrorChars = 600;
    private const string PendingFingerprint = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly ScopedInstructionClaimStore _store = new(packageContext);

    public string CleanerId => "agent.files.scoped-instruction-claims";

    public void DeleteSessionData(Guid sessionId)
        => _store.Delete(sessionId);

    public async ValueTask<IReadOnlyList<AgentPromptContextBlock>> BuildPromptContextAsync(
        IAgentExecutionTarget target,
        AgentPromptContextRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasContext(request.Session.SessionId, request.Workspace, request.ExecutionBinding, request.TranscriptEpoch))
        {
            return [];
        }
        if (target is not IAgentScopedInstructionDiscoveryTarget discoveryTarget
            || target is not IAgentExecutionScopeProvider scopeProvider)
        {
            throw new InvalidOperationException("The selected execution target does not provide the scoped-instruction discovery required by first-party Files enforcement.");
        }

        var sessionId = request.Session.SessionId;
        using var sessionLock = await _store.EnterSessionAsync(sessionId, cancellationToken);
        var loaded = await _store.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (loaded.Status is ScopedInstructionClaimLoadStatus.Invalid or ScopedInstructionClaimLoadStatus.FutureVersion)
        {
            throw new InvalidOperationException(loaded.Status == ScopedInstructionClaimLoadStatus.FutureVersion
                ? "Scoped instruction claims were written by a newer runtime and cannot be changed safely."
                : "Scoped instruction claims could not be validated safely.");
        }

        var targetContext = CreateTargetContext(
            sessionId,
            request.Profile?.ProfileId,
            request.Workspace!,
            request.ExecutionBinding!);
        var scope = await scopeProvider.GetExecutionScopeAsync(targetContext, cancellationToken).ConfigureAwait(false);
        var rootPaths = scope.WorkspacePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var hasStaticIdentity = loaded.State is { } loadedState
                                && MatchesStaticIdentity(
                                    loadedState,
                                    sessionId,
                                    request.Workspace!,
                                    request.ExecutionBinding!,
                                    target,
                                    request.TranscriptEpoch);
        var matchedState = hasStaticIdentity ? loaded.State : null;
        var tentativeClaims = matchedState?.Claims ?? [];
        var pendingProbes = matchedState?.PendingAccessProbes ?? [];
        var probes = rootPaths
            .Select(path => new AgentScopedInstructionProbe(path, IsDirectory: true))
            .Concat(tentativeClaims.Select(claim => new AgentScopedInstructionProbe(claim.Directory, IsDirectory: true)))
            .Concat(pendingProbes.Select(probe => probe.ToProbe()))
            .DistinctBy(ProbeKey)
            .ToArray();
        if (probes.Length == 0)
        {
            return [];
        }

        var discovery = await DiscoverBatchedAsync(
            discoveryTarget,
            targetContext,
            probes,
            cancellationToken).ConfigureAwait(false);
        ValidateCompleteDiscovery(discovery, probes.Length);
        var state = loaded.State is { } current
                    && MatchesIdentity(
                        current,
                        sessionId,
                        request.Workspace!,
                        request.ExecutionBinding!,
                        target,
                        request.TranscriptEpoch,
                        discovery)
            ? current
            : CreateState(
                sessionId,
                request.Workspace!,
                request.ExecutionBinding!,
                target,
                request.TranscriptEpoch,
                discovery);
        var acceptedScopes = discovery.Scopes.ToArray();
        state = UpdateClaims(state, acceptedScopes).State with { PendingAccessProbes = [] };
        await SaveIfChangedAsync(loaded, state, cancellationToken).ConfigureAwait(false);
        return RenderBlocks(acceptedScopes, BuildContextIdentity(state));
    }

    public async ValueTask AcknowledgePromptContextAsync(
        AgentPromptContextReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (receipt.SessionId == Guid.Empty || receipt.TranscriptEpoch <= 0)
        {
            throw new InvalidOperationException("Scoped instruction prompt acknowledgment is missing durable session identity.");
        }

        using var sessionLock = await _store.EnterSessionAsync(receipt.SessionId, cancellationToken);
        var loaded = await _store.LoadAsync(receipt.SessionId, cancellationToken).ConfigureAwait(false);
        if (loaded.Status != ScopedInstructionClaimLoadStatus.Valid || loaded.State is null)
        {
            throw new InvalidOperationException("Scoped instruction claims changed before prompt acknowledgment completed.");
        }
        if (loaded.State.TranscriptEpoch != receipt.TranscriptEpoch)
        {
            throw new InvalidOperationException("Scoped instruction prompt acknowledgment is stale for the current transcript epoch.");
        }

        var contextIdentity = BuildContextIdentity(loaded.State);
        var expected = receipt.Blocks
            .Where(block => string.Equals(block.ContextIdentity, contextIdentity, StringComparison.Ordinal))
            .Select(block => (block.DocumentPath, block.ContentHash))
            .Distinct()
            .ToArray();
        if (expected.Length != receipt.Blocks.Count)
        {
            throw new InvalidOperationException("Scoped instruction prompt acknowledgment does not match the current execution context.");
        }

        var expectedSet = expected.ToHashSet();
        var observedSet = loaded.State.Claims
            .SelectMany(claim => claim.Documents)
            .Select(document => (document.Path, document.ObservedHash))
            .ToHashSet();
        if (!expectedSet.IsSubsetOf(observedSet))
        {
            throw new InvalidOperationException("Scoped instruction prompt acknowledgment contains a stale document hash.");
        }

        var next = loaded.State with
        {
            Claims = loaded.State.Claims.Select(claim => claim with
            {
                Documents = claim.Documents.Select(document =>
                        expectedSet.Contains((document.Path, document.ObservedHash))
                            ? document with { PresentedHash = document.ObservedHash }
                            : document)
                    .ToArray(),
            }).ToArray(),
        };
        await SaveIfChangedAsync(loaded, next, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AgentToolResult?> PreflightMutationAsync(
        IAgentExecutionTarget target,
        AgentToolExecutionContext context,
        AgentToolRequest request,
        IReadOnlyList<AgentScopedInstructionProbe> probes,
        CancellationToken cancellationToken)
    {
        if (probes.Count > MaxMutationPaths)
        {
            return DeferredError(
                request.ToolId,
                $"Scoped instruction preflight supports at most {MaxMutationPaths} distinct patch paths. No files were changed.",
                "files-scoped-instruction-path-limit",
                requiresRefresh: false);
        }
        if (!HasContext(context.SessionId, context.Workspace, context.ExecutionBinding, context.TranscriptEpoch))
        {
            return null;
        }
        if (target is not IAgentScopedInstructionDiscoveryTarget discoveryTarget)
        {
            return DeferredError(
                request.ToolId,
                "The selected execution target does not support required scoped-instruction discovery. No files were changed.",
                "files-scoped-instructions-target-unsupported",
                requiresRefresh: false);
        }

        try
        {
            using var sessionLock = await _store.EnterSessionAsync(context.SessionId!.Value, cancellationToken);
            var loaded = await _store.LoadAsync(context.SessionId.Value, cancellationToken).ConfigureAwait(false);
            if (loaded.Status is ScopedInstructionClaimLoadStatus.Invalid or ScopedInstructionClaimLoadStatus.FutureVersion)
            {
                return DeferredError(
                    request.ToolId,
                    loaded.Status == ScopedInstructionClaimLoadStatus.FutureVersion
                        ? "Scoped instruction claims are from a newer version. No files were changed."
                        : "Scoped instruction claims could not be validated safely. No files were changed.",
                    loaded.Status == ScopedInstructionClaimLoadStatus.FutureVersion
                        ? "files-scoped-instruction-claims-future-version"
                        : "files-scoped-instruction-claims-invalid",
                    requiresRefresh: false);
            }

            var targetContext = CreateTargetContext(
                context.SessionId.Value,
                context.ProfileId,
                context.Workspace!,
                context.ExecutionBinding!,
                context.ExecutionTargetConfigurationGeneration);
            var recoveryState = PrepareRecoveryState(
                loaded.State,
                context.SessionId.Value,
                context.Workspace!,
                context.ExecutionBinding!,
                target,
                context.TranscriptEpoch);
            var hadPendingRecovery = recoveryState.PendingAccessProbes.Count > 0;
            if (!TryAddPendingProbes(recoveryState, probes, out var stagedState))
            {
                return DeferredError(
                    request.ToolId,
                    $"Scoped instruction recovery supports at most {ScopedInstructionClaimStore.MaxPendingProbes} pending paths. No files were changed.",
                    "files-scoped-instruction-recovery-path-limit",
                    requiresRefresh: hadPendingRecovery);
            }
            await SaveIfChangedAsync(loaded, stagedState!, cancellationToken).ConfigureAwait(false);
            var discoveryProbes = stagedState!.PendingAccessProbes.Select(probe => probe.ToProbe()).ToArray();
            var discovery = await DiscoverBatchedAsync(
                discoveryTarget,
                targetContext,
                discoveryProbes,
                cancellationToken).ConfigureAwait(false);
            ValidateCompleteDiscovery(discovery, discoveryProbes.Length);
            var identityChanged = loaded.State is not { } currentIdentity
                                  || !MatchesIdentity(
                                      currentIdentity,
                                      context.SessionId.Value,
                                      context.Workspace!,
                                      context.ExecutionBinding!,
                                      target,
                                      context.TranscriptEpoch,
                                      discovery);
            var state = !identityChanged && loaded.State is { } current
                         && MatchesIdentity(
                            current,
                            context.SessionId.Value,
                            context.Workspace!,
                            context.ExecutionBinding!,
                            target,
                            context.TranscriptEpoch,
                            discovery)
                ? current
                : CreateState(
                    context.SessionId.Value,
                    context.Workspace!,
                    context.ExecutionBinding!,
                    target,
                    context.TranscriptEpoch,
                    discovery);
            var update = UpdateClaims(state, discovery.Scopes);
            var completedState = update.State with { PendingAccessProbes = [] };
            await _store.SaveAsync(completedState, cancellationToken).ConfigureAwait(false);
            if (!hadPendingRecovery && !identityChanged && !update.HasUnpresentedInstructionChange)
            {
                return null;
            }

            var changedPaths = update.UnpresentedDocumentPaths
                .Order(StringComparer.Ordinal)
                .Take(20)
                .ToArray();
            var content = new StringBuilder()
                .AppendLine("### File mutation deferred")
                .AppendLine()
                .AppendLine("Applicable AGENTS.md instructions changed or were discovered before dispatch. No file operation was started and no files were changed. Prompt context must refresh before the mutation is replanned.");
            foreach (var path in changedPaths)
            {
                content.Append("- ").AppendLine(path);
            }

            return new AgentToolResult(
                request.ToolId,
                "File mutation deferred until scoped AGENTS.md context is refreshed.",
                Content: content.ToString().TrimEnd(),
                WasTruncated: update.UnpresentedDocumentPaths.Count > changedPaths.Length,
                IsError: true,
                ErrorCode: "files-prompt-context-refresh-required",
                BackendId: FileToolResult.BackendId(target))
            {
                RequiresPromptContextRefresh = true,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DeferredError(
                request.ToolId,
                $"Scoped instruction preflight failed. No files were changed. {BoundError(ex.Message)}",
                "files-scoped-instruction-discovery-failed",
                requiresRefresh: true);
        }
    }

    public async ValueTask<ScopedInstructionAccessClaimResult> ClaimAfterAccessAsync(
        IAgentExecutionTarget target,
        AgentToolExecutionContext context,
        IReadOnlyList<AgentScopedInstructionProbe> probes,
        CancellationToken cancellationToken)
    {
        if (probes.Count == 0
            || !HasContext(context.SessionId, context.Workspace, context.ExecutionBinding, context.TranscriptEpoch))
        {
            return ScopedInstructionAccessClaimResult.Ready;
        }
        if (probes.Count > MaxMutationPaths)
        {
            throw new InvalidOperationException($"Scoped instruction discovery supports at most {MaxMutationPaths} probes per batch.");
        }
        if (target is not IAgentScopedInstructionDiscoveryTarget discoveryTarget)
        {
            throw new InvalidOperationException("The selected execution target does not support required scoped-instruction discovery.");
        }

        using var sessionLock = await _store.EnterSessionAsync(context.SessionId!.Value, cancellationToken);
        var loaded = await _store.LoadAsync(context.SessionId.Value, cancellationToken).ConfigureAwait(false);
        if (loaded.Status is ScopedInstructionClaimLoadStatus.Invalid or ScopedInstructionClaimLoadStatus.FutureVersion)
        {
            throw new InvalidOperationException("Scoped instruction claims could not be validated safely.");
        }

        var targetContext = CreateTargetContext(
            context.SessionId.Value,
            context.ProfileId,
            context.Workspace!,
            context.ExecutionBinding!,
            context.ExecutionTargetConfigurationGeneration);
        var recoveryState = PrepareRecoveryState(
            loaded.State,
            context.SessionId.Value,
            context.Workspace!,
            context.ExecutionBinding!,
            target,
            context.TranscriptEpoch);
        var hadPendingRecovery = recoveryState.PendingAccessProbes.Count > 0;
        if (!TryAddPendingProbes(recoveryState, probes, out var stagedState))
        {
            throw new InvalidOperationException(
                $"Scoped instruction recovery supports at most {ScopedInstructionClaimStore.MaxPendingProbes} pending paths.");
        }
        await SaveIfChangedAsync(loaded, stagedState!, cancellationToken).ConfigureAwait(false);
        var discoveryProbes = stagedState!.PendingAccessProbes.Select(probe => probe.ToProbe()).ToArray();
        var discovery = await DiscoverBatchedAsync(
            discoveryTarget,
            targetContext,
            discoveryProbes,
            cancellationToken).ConfigureAwait(false);
        ValidateCompleteDiscovery(discovery, discoveryProbes.Length);
        var identityChanged = loaded.State is not { } currentIdentity
                              || !MatchesIdentity(
                                  currentIdentity,
                                  context.SessionId.Value,
                                  context.Workspace!,
                                  context.ExecutionBinding!,
                                  target,
                                  context.TranscriptEpoch,
                                  discovery);
        var state = !identityChanged && loaded.State is { } current
                     && MatchesIdentity(
                        current,
                        context.SessionId.Value,
                        context.Workspace!,
                        context.ExecutionBinding!,
                        target,
                        context.TranscriptEpoch,
                        discovery)
            ? current
            : CreateState(
                context.SessionId.Value,
                context.Workspace!,
                context.ExecutionBinding!,
                target,
                context.TranscriptEpoch,
                discovery);
        var update = UpdateClaims(state, discovery.Scopes);
        await _store.SaveAsync(
            update.State with { PendingAccessProbes = [] },
            cancellationToken).ConfigureAwait(false);
        return hadPendingRecovery || identityChanged || update.HasUnpresentedInstructionChange
            ? ScopedInstructionAccessClaimResult.RefreshRequired
            : ScopedInstructionAccessClaimResult.Ready;
    }

    private static bool HasContext(
        Guid? sessionId,
        AgentWorkspaceRecord? workspace,
        AgentWorkspaceBindingRecord? binding,
        long transcriptEpoch)
        => sessionId is not null
           && workspace is not null
           && binding is not null
           && transcriptEpoch > 0;

    private static AgentExecutionTargetContext CreateTargetContext(
        Guid sessionId,
        string? profileId,
        AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord binding,
        string? expectedConfigurationGeneration = null)
        => new(sessionId, profileId, workspace, binding, AllowOutsideConfiguredScope: false)
        {
            ExpectedConfigurationGeneration = expectedConfigurationGeneration,
        };

    private static ScopedInstructionClaimState CreateState(
        Guid sessionId,
        AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord binding,
        IAgentExecutionTarget target,
        long transcriptEpoch,
        AgentScopedInstructionDiscoveryResult discovery)
        => new(
            ScopedInstructionClaimStore.CurrentVersion,
            sessionId,
            workspace.WorkspaceId,
            binding.BindingId,
            target.Descriptor.TargetKind,
            target.Descriptor.TargetId,
            discovery.TargetFingerprint,
            discovery.ScopeFingerprint,
            transcriptEpoch,
            []);

    private static ScopedInstructionClaimState PrepareRecoveryState(
        ScopedInstructionClaimState? state,
        Guid sessionId,
        AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord binding,
        IAgentExecutionTarget target,
        long transcriptEpoch)
        => state is not null
           && MatchesStaticIdentity(state, sessionId, workspace, binding, target, transcriptEpoch)
            ? state
            : new ScopedInstructionClaimState(
                ScopedInstructionClaimStore.CurrentVersion,
                sessionId,
                workspace.WorkspaceId,
                binding.BindingId,
                target.Descriptor.TargetKind,
                target.Descriptor.TargetId,
                PendingFingerprint,
                PendingFingerprint,
                transcriptEpoch,
                []);

    private static bool TryAddPendingProbes(
        ScopedInstructionClaimState state,
        IReadOnlyList<AgentScopedInstructionProbe> probes,
        out ScopedInstructionClaimState? next)
    {
        var pending = state.PendingAccessProbes
            .Concat(probes.Select(ScopedInstructionPendingProbe.FromProbe))
            .DistinctBy(probe => (probe.Path, probe.IsDirectory, probe.FollowFinalSymbolicLink))
            .ToArray();
        if (pending.Length > ScopedInstructionClaimStore.MaxPendingProbes)
        {
            next = null;
            return false;
        }

        next = state with { PendingAccessProbes = pending };
        return true;
    }

    private static (string Path, bool IsDirectory, bool FollowFinalSymbolicLink) ProbeKey(
        AgentScopedInstructionProbe probe)
        => (probe.Path, probe.IsDirectory, probe.FollowFinalSymbolicLink);

    private static bool MatchesStaticIdentity(
        ScopedInstructionClaimState state,
        Guid sessionId,
        AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord binding,
        IAgentExecutionTarget target,
        long transcriptEpoch)
        => state.SessionId == sessionId
           && state.TranscriptEpoch == transcriptEpoch
           && string.Equals(state.WorkspaceId, workspace.WorkspaceId, StringComparison.Ordinal)
           && string.Equals(state.BindingId, binding.BindingId, StringComparison.Ordinal)
           && string.Equals(state.TargetKind, target.Descriptor.TargetKind, StringComparison.Ordinal)
           && string.Equals(state.TargetId, target.Descriptor.TargetId, StringComparison.Ordinal);

    private static bool MatchesIdentity(
        ScopedInstructionClaimState state,
        Guid sessionId,
        AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord binding,
        IAgentExecutionTarget target,
        long transcriptEpoch,
        AgentScopedInstructionDiscoveryResult discovery)
        => MatchesStaticIdentity(state, sessionId, workspace, binding, target, transcriptEpoch)
           && string.Equals(state.TargetFingerprint, discovery.TargetFingerprint, StringComparison.Ordinal)
           && string.Equals(state.ScopeFingerprint, discovery.ScopeFingerprint, StringComparison.Ordinal);

    private static ScopedInstructionClaimUpdate UpdateClaims(
        ScopedInstructionClaimState state,
        IReadOnlyList<AgentScopedInstructionScope> scopes)
    {
        var previousByDirectory = state.Claims.ToDictionary(claim => claim.Directory, StringComparer.Ordinal);
        var globallyPresented = state.Claims
            .SelectMany(claim => claim.Documents)
            .Where(document => document.PresentedHash is not null)
            .GroupBy(document => document.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().PresentedHash!, StringComparer.Ordinal);
        var nextClaims = state.Claims.ToDictionary(claim => claim.Directory, StringComparer.Ordinal);
        var unpresentedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scope in scopes)
        {
            var documents = scope.Documents
                .Where(IsValidDocument)
                .GroupBy(document => document.Path, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderBy(document => document.AppliesToDirectory, StringComparer.Ordinal)
                .Select(document =>
                {
                    globallyPresented.TryGetValue(document.Path, out var presentedHash);
                    if (!string.Equals(presentedHash, document.ContentHash, StringComparison.Ordinal))
                    {
                        unpresentedPaths.Add(document.Path);
                        presentedHash = null;
                    }
                    return new ScopedInstructionDocumentClaim(
                        document.Path,
                        document.ContentHash,
                        presentedHash);
                })
                .ToArray();
            if (previousByDirectory.TryGetValue(scope.TargetDirectory, out var previous))
            {
                var currentPaths = documents.Select(document => document.Path).ToHashSet(StringComparer.Ordinal);
                foreach (var removed in previous.Documents.Where(document =>
                             document.PresentedHash is not null && !currentPaths.Contains(document.Path)))
                {
                    unpresentedPaths.Add(removed.Path);
                }
            }

            nextClaims[scope.TargetDirectory] = new ScopedInstructionDirectoryClaim(
                scope.TargetDirectory,
                scope.ScopeRoot,
                documents);
        }

        var touchedDirectories = scopes
            .Select(scope => scope.TargetDirectory)
            .ToHashSet(StringComparer.Ordinal);
        var retainedClaims = nextClaims.Values
            .OrderByDescending(claim => touchedDirectories.Contains(claim.Directory))
            .ThenBy(claim => claim.ScopeRoot, StringComparer.Ordinal)
            .ThenBy(claim => claim.Directory, StringComparer.Ordinal)
            .Take(ScopedInstructionClaimStore.MaxRetainedClaims)
            .OrderBy(claim => claim.ScopeRoot, StringComparer.Ordinal)
            .ThenBy(claim => claim.Directory, StringComparer.Ordinal)
            .ToArray();
        var nextState = state with
        {
            Claims = retainedClaims,
        };
        var changed = !EqualsState(state, nextState);
        return new ScopedInstructionClaimUpdate(
            nextState,
            changed,
            unpresentedPaths.Count > 0,
            unpresentedPaths.ToArray());
    }

    private static bool IsValidDocument(AgentScopedInstructionDocument document)
        => !document.WasTruncated
           && document.Content.Length <= MaxDocumentChars
           && document.ContentHash is { Length: 64 }
           && document.ContentHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
           && !string.IsNullOrWhiteSpace(document.Path)
           && !string.IsNullOrWhiteSpace(document.ScopeRoot)
           && !string.IsNullOrWhiteSpace(document.AppliesToDirectory);

    private static bool EqualsState(ScopedInstructionClaimState left, ScopedInstructionClaimState right)
    {
        if (left.Version != right.Version
            || left.SessionId != right.SessionId
            || left.TranscriptEpoch != right.TranscriptEpoch
            || !string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(left.BindingId, right.BindingId, StringComparison.Ordinal)
            || !string.Equals(left.TargetKind, right.TargetKind, StringComparison.Ordinal)
            || !string.Equals(left.TargetId, right.TargetId, StringComparison.Ordinal)
            || !string.Equals(left.TargetFingerprint, right.TargetFingerprint, StringComparison.Ordinal)
            || !string.Equals(left.ScopeFingerprint, right.ScopeFingerprint, StringComparison.Ordinal)
            || left.Claims.Count != right.Claims.Count
            || left.PendingAccessProbes.Count != right.PendingAccessProbes.Count)
        {
            return false;
        }

        for (var claimIndex = 0; claimIndex < left.Claims.Count; claimIndex++)
        {
            var leftClaim = left.Claims[claimIndex];
            var rightClaim = right.Claims[claimIndex];
            if (!string.Equals(leftClaim.Directory, rightClaim.Directory, StringComparison.Ordinal)
                || !string.Equals(leftClaim.ScopeRoot, rightClaim.ScopeRoot, StringComparison.Ordinal)
                || leftClaim.Documents.Count != rightClaim.Documents.Count)
            {
                return false;
            }

            for (var documentIndex = 0; documentIndex < leftClaim.Documents.Count; documentIndex++)
            {
                var leftDocument = leftClaim.Documents[documentIndex];
                var rightDocument = rightClaim.Documents[documentIndex];
                if (!string.Equals(leftDocument.Path, rightDocument.Path, StringComparison.Ordinal)
                    || !string.Equals(leftDocument.ObservedHash, rightDocument.ObservedHash, StringComparison.Ordinal)
                    || !string.Equals(leftDocument.PresentedHash, rightDocument.PresentedHash, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        for (var probeIndex = 0; probeIndex < left.PendingAccessProbes.Count; probeIndex++)
        {
            var leftProbe = left.PendingAccessProbes[probeIndex];
            var rightProbe = right.PendingAccessProbes[probeIndex];
            if (!string.Equals(leftProbe.Path, rightProbe.Path, StringComparison.Ordinal)
                || leftProbe.IsDirectory != rightProbe.IsDirectory
                || leftProbe.FollowFinalSymbolicLink != rightProbe.FollowFinalSymbolicLink)
            {
                return false;
            }
        }

        return true;
    }

    private async Task SaveIfChangedAsync(
        ScopedInstructionClaimLoadResult loaded,
        ScopedInstructionClaimState state,
        CancellationToken cancellationToken)
    {
        if (loaded.Status is ScopedInstructionClaimLoadStatus.Invalid or ScopedInstructionClaimLoadStatus.FutureVersion)
        {
            return;
        }
        if (loaded.State is not null && EqualsState(loaded.State, state))
        {
            return;
        }
        await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<AgentPromptContextBlock> RenderBlocks(
        IReadOnlyList<AgentScopedInstructionScope> scopes,
        string contextIdentity)
    {
        var documents = scopes
            .SelectMany(scope => scope.Documents)
            .Where(IsValidDocument)
            .GroupBy(document => document.Path, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderByDescending(GetScopeDepth)
            .ThenBy(document => document.Path, StringComparer.Ordinal)
            .ToArray();
        if (documents.Length > MaxRenderedDocuments)
        {
            throw new InvalidOperationException($"Applicable scoped instructions exceed the {MaxRenderedDocuments}-document prompt limit.");
        }

        var blocks = documents.Select(document =>
        {
            var rendered = $"Instruction file: {document.Path}\nScope root: {document.ScopeRoot}\nApplies only to directory subtree: {document.AppliesToDirectory}\n\n{document.Content}";
            return new AgentPromptContextBlock(
                $"Scoped AGENTS.md: {document.AppliesToDirectory}",
                rendered,
                Priority: 180 + Math.Min(64, GetScopeDepth(document)),
                SourceId: FileToolDescriptorRegistry.SourceId,
                Provenance: AgentContextProvenance.Tool,
                Trust: AgentContextTrust.Untrusted)
            {
                Usage = AgentPromptContextUsage.ScopedInstruction,
                Scope = new AgentPromptContextScope(
                    document.ScopeRoot,
                    document.AppliesToDirectory,
                    document.Path,
                    document.ContentHash,
                    contextIdentity),
            };
        }).ToArray();
        if (blocks.Sum(block => block.Content.Length) > MaxRenderedChars)
        {
            throw new InvalidOperationException($"Applicable scoped instructions exceed the {MaxRenderedChars}-character prompt limit.");
        }

        return blocks;
    }

    private static int GetScopeDepth(AgentScopedInstructionDocument document)
    {
        var root = document.ScopeRoot.Replace('\\', '/').TrimEnd('/');
        var appliesTo = document.AppliesToDirectory.Replace('\\', '/').TrimEnd('/');
        if (string.Equals(root, appliesTo, StringComparison.Ordinal))
        {
            return 0;
        }
        return appliesTo.Length > root.Length
            ? appliesTo[(root.Length + 1)..].Count(character => character == '/') + 1
            : 0;
    }

    private static string BuildContextIdentity(ScopedInstructionClaimState state)
    {
        var values = new[]
        {
            "sunder-scoped-instruction-context-v1",
            state.SessionId.ToString("N"),
            state.WorkspaceId,
            state.BindingId,
            state.TargetKind,
            state.TargetId,
            state.TargetFingerprint,
            state.ScopeFingerprint,
            state.TranscriptEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var material = new StringBuilder();
        foreach (var value in values)
        {
            material.Append(value.Length).Append(':').Append(value);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()))).ToLowerInvariant();
    }

    private static string BoundError(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= MaxVisibleErrorChars
            ? normalized
            : normalized[..MaxVisibleErrorChars] + "...";
    }

    private static AgentToolResult DeferredError(
        string toolId,
        string message,
        string errorCode,
        bool requiresRefresh)
        => new(
            toolId,
            message,
            Content: $"### File mutation not dispatched\n\n{message}",
            IsError: true,
            ErrorCode: errorCode)
        {
            RequiresPromptContextRefresh = requiresRefresh,
        };
}

internal sealed record ScopedInstructionClaimUpdate(
    ScopedInstructionClaimState State,
    bool Changed,
    bool HasUnpresentedInstructionChange,
    IReadOnlyList<string> UnpresentedDocumentPaths);

internal enum ScopedInstructionAccessClaimResult
{
    Ready,
    RefreshRequired,
}
