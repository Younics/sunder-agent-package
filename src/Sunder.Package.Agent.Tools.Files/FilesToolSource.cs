using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Files;

public sealed class FilesToolSource
    : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentPermissionSurface, IAgentPromptContextContributor,
        IAgentToolPresentationResolver, IAgentToolExecutionPreflightSource, IAgentSessionDataCleaner,
        IAgentPromptContextAcknowledgmentSink
{
    private const int MaxPostAccessScopePaths = 64;
    private readonly IPackageExtensionCatalog _extensionCatalog;
    private readonly IPackageExtensionInvocationCatalog? _invocationCatalog;
    private readonly ScopedInstructionContextService? _scopedInstructions;

    /// <summary>Creates the legacy source shape without scoped-instruction persistence or enforcement.</summary>
    /// <remarks>First-party composition uses the two-argument constructor. This overload exists for CLR binary compatibility.</remarks>
    public FilesToolSource(IPackageExtensionCatalog extensionCatalog)
    {
        _extensionCatalog = extensionCatalog ?? throw new ArgumentNullException(nameof(extensionCatalog));
        _invocationCatalog = extensionCatalog as IPackageExtensionInvocationCatalog;
    }

    /// <summary>Creates a Files source with required scoped-instruction persistence and enforcement.</summary>
    public FilesToolSource(IPackageExtensionCatalog extensionCatalog, IPackageContext packageContext)
    {
        _extensionCatalog = extensionCatalog ?? throw new ArgumentNullException(nameof(extensionCatalog));
        _invocationCatalog = extensionCatalog as IPackageExtensionInvocationCatalog;
        _scopedInstructions = new ScopedInstructionContextService(
            packageContext ?? throw new ArgumentNullException(nameof(packageContext)));
    }

    /// <summary>Gets whether this instance enforces scoped instructions for structured Files mutations.</summary>
    public bool IsScopedInstructionEnforcementEnabled => _scopedInstructions is not null;

    public string SourceId => FileToolDescriptorRegistry.SourceId;

    public string DisplayName => FileToolDescriptorRegistry.DisplayName;

    public string SourceKind => "workspace";

    public string SurfaceId => "files";

    public string ContributorId => FileToolDescriptorRegistry.SourceId;

    public string CleanerId => "agent.files.scoped-instruction-claims";

    public string AcknowledgmentSinkId => "agent.files.scoped-instruction-acknowledgment";

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
        => FileToolPresentationAdapter.Resolve(request);

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(FileToolDescriptorRegistry.Descriptors);
    }

    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(
        string toolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        if (!FileToolDescriptorRegistry.Contains(toolId))
        {
            return null;
        }

        if (context.Workspace is null)
        {
            return new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, "File tools require a selected workspace.");
        }

        if (context.ExecutionBinding is null
            || !TryAcquireTarget(context.ExecutionTargetReference, context.ExecutionBinding, out var targetLease))
        {
            ThrowIfExactTargetUnavailable(context.ExecutionTargetReference);
            return new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, "The selected workspace is not bound to an installed execution target.");
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, invocationToken) =>
            {
                if ((toolId.Equals("grep", StringComparison.OrdinalIgnoreCase)
                     || toolId.Equals("glob", StringComparison.OrdinalIgnoreCase))
                    && target is not IAgentStructuredFileSearchExecutionTarget
                    && target is not IAgentFileSearchExecutionTarget)
                {
                    return new AgentToolReadiness(
                        toolId,
                        AgentToolReadinessStatus.Failed,
                        "The selected target cannot safely bind file-search paths to canonical resources.");
                }

                if (_scopedInstructions is not null
                    && (target is not IAgentScopedInstructionDiscoveryTarget
                        || target is not IAgentExecutionScopeProvider))
                {
                    return new AgentToolReadiness(
                        toolId,
                        AgentToolReadinessStatus.Failed,
                        "The selected target does not support scoped-instruction discovery required by first-party Files enforcement.");
                }

                var readiness = await target.GetReadinessAsync(
                    new AgentExecutionTargetContext(context.SessionId, context.Profile?.ProfileId, context.Workspace, context.ExecutionBinding),
                    invocationToken);
                return readiness.Status == AgentExecutionTargetReadinessStatus.Ready && target.Descriptor.SupportsFiles
                    ? new AgentToolReadiness(
                        toolId,
                        AgentToolReadinessStatus.Ready,
                        _scopedInstructions is null
                            ? "Workspace file tools are ready. Scoped AGENTS.md enforcement is disabled by legacy FilesToolSource composition."
                            : "Workspace file tools are ready with scoped AGENTS.md enforcement.")
                    : new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, readiness.Message);
            });
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (context.Workspace is null)
        {
            return FileToolResult.Error(request.ToolId, "File tools require a selected workspace.", "files-workspace-required");
        }

        if (context.ExecutionBinding is null
            || !TryAcquireTarget(context.ExecutionTargetReference, context.ExecutionBinding, out var targetLease))
        {
            ThrowIfExactTargetUnavailable(context.ExecutionTargetReference);
            return FileToolResult.Error(request.ToolId, "The selected workspace is not bound to an installed execution target.", "files-target-required");
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, invocationToken) =>
            {
                var targetContext = new AgentExecutionTargetContext(
                    context.SessionId,
                    context.ProfileId,
                    context.Workspace,
                    context.ExecutionBinding,
                    context.AllowOutsideConfiguredScope)
                {
                    ApprovedResourceReferences = context.ApprovedResourceReferences,
                    ApprovedResourceClaims = context.ApprovedResourceClaims,
                    ApprovedResourceCapabilities = context.ApprovedResourceCapabilities,
                    ResourceOperation = context.ResourceOperation,
                };
                try
                {
                    if (IsMutation(request.ToolId)
                        && await PreflightExecutionAsync(context, request, invocationToken) is { } deferred)
                    {
                        return deferred;
                    }

                    AgentToolResult result;
                    IReadOnlyList<AgentScopedInstructionProbe> accessProbes = [];
                    if (request.ToolId.Equals("read", StringComparison.OrdinalIgnoreCase))
                    {
                        var readResult = await FileReadHandler.ExecuteAsync(target, targetContext, request, invocationToken);
                        result = readResult.Result;
                        if (!result.IsError
                            && FileToolArguments.TryParseRead(request.ArgumentsJson, out var read, out _))
                        {
                            accessProbes = [new AgentScopedInstructionProbe(read.Path, IsDirectory: readResult.IsDirectory)];
                        }
                    }
                    else
                    {
                        switch (request.ToolId.ToLowerInvariant())
                        {
                            case "write":
                                result = await FileWriteHandler.ExecuteWriteAsync(target, targetContext, request, invocationToken);
                                break;
                            case "edit":
                                result = await FileWriteHandler.ExecuteEditAsync(target, targetContext, request, invocationToken);
                                break;
                            case "apply_patch":
                                result = await FilePatchHandler.ExecuteAsync(target, targetContext, request, invocationToken);
                                break;
                            case "grep":
                                {
                                    var search = await FileSearchHandler.ExecuteGrepAsync(target, targetContext, request, invocationToken);
                                    result = search.Result;
                                    accessProbes = search.MatchedPaths
                                        .Select(path => new AgentScopedInstructionProbe(path))
                                        .ToArray();
                                    break;
                                }
                            case "glob":
                                {
                                    var search = await FileSearchHandler.ExecuteGlobAsync(target, targetContext, request, invocationToken);
                                    result = search.Result;
                                    accessProbes = search.MatchedPaths
                                        .Select(path => new AgentScopedInstructionProbe(path))
                                        .ToArray();
                                    break;
                                }
                            default:
                                result = FileToolResult.Error(request.ToolId, $"Unknown file tool '{request.ToolId}'.", "files-tool-unknown");
                                break;
                        }
                    }
                    if (IsMutation(request.ToolId))
                    {
                        return result with { RequiresPromptContextRefresh = true };
                    }

                    if (_scopedInstructions is not null && !result.IsError && accessProbes.Count > 0)
                    {
                        if (accessProbes.Count > MaxPostAccessScopePaths)
                        {
                            return WithheldAccessResult(
                                target,
                                request.ToolId,
                                $"The result referenced more than {MaxPostAccessScopePaths} distinct paths. Narrow the read or search and rerun it.",
                                "files-scoped-instruction-result-path-limit",
                                requiresRefresh: false);
                        }
                        try
                        {
                            var claim = await _scopedInstructions.ClaimAfterAccessAsync(
                                target,
                                context,
                                accessProbes,
                                invocationToken);
                            if (claim == ScopedInstructionAccessClaimResult.RefreshRequired)
                            {
                                return WithheldAccessResult(
                                    target,
                                    request.ToolId,
                                    "Applicable AGENTS.md instructions were discovered or changed. Prompt context must refresh before this result is requested again.",
                                    "files-prompt-context-refresh-required",
                                    requiresRefresh: true);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            return WithheldAccessResult(
                                target,
                                request.ToolId,
                                $"Scoped instruction discovery failed after access. The successful result was withheld. {BoundMessage(ex.Message)}",
                                "files-scoped-instruction-discovery-failed",
                                requiresRefresh: true);
                        }
                    }

                    return result;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return FileToolResult.Error(request.ToolId, ex.Message, "files-execution");
                }
                finally
                {
                    if (target is IAgentResourceAuthorityExecutionTarget authorityTarget)
                    {
                        authorityTarget.ReleaseResourceAuthority(targetContext.ApprovedResourceCapabilities);
                    }
                }
            });
    }

    public async ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (context.ExecutionBinding is null
            || !TryAcquireTarget(context.ExecutionTargetReference, context.ExecutionBinding, out var targetLease))
        {
            ThrowIfExactTargetUnavailable(context.ExecutionTargetReference);
            return await FilePermissionPlanner.BuildAsync(null, context, request, cancellationToken);
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            (target, invocationToken) => FilePermissionPlanner.BuildAsync(target, context, request, invocationToken));
    }

    public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
        => FilePermissionPlanner.Actions;

    public async ValueTask<AgentPromptContextContribution?> ContributeContextAsync(
        AgentPromptContextRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ExecutionBinding is null
            || !TryAcquireTarget(request.ExecutionTargetReference, request.ExecutionBinding, out var targetLease))
        {
            ThrowIfExactTargetUnavailable(request.ExecutionTargetReference);
            return null;
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, invocationToken) =>
            {
                var scopeContribution = await FileSystemPromptBuilder.BuildAsync(target, request, invocationToken);
                var blocks = scopeContribution?.Blocks.ToList() ?? [];
                if (_scopedInstructions is not null)
                {
                    blocks.AddRange(await _scopedInstructions.BuildPromptContextAsync(target, request, invocationToken));
                }

                return blocks.Count == 0 ? null : new AgentPromptContextContribution(blocks);
            });
    }

    public async ValueTask<AgentToolResult?> PreflightExecutionAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateArguments(request, out var validationError))
        {
            return FileToolResult.Error(request.ToolId, validationError!, "files-arguments-invalid");
        }
        if (context.Workspace is null
            || context.ExecutionBinding is null)
        {
            return null;
        }
        if (!TryAcquireTarget(context.ExecutionTargetReference, context.ExecutionBinding, out var targetLease))
        {
            ThrowIfExactTargetUnavailable(context.ExecutionTargetReference);
            return null;
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, invocationToken) =>
            {
                var targetContext = new AgentExecutionTargetContext(
                    context.SessionId,
                    context.ProfileId,
                    context.Workspace,
                    context.ExecutionBinding,
                    context.AllowOutsideConfiguredScope)
                {
                    ApprovedResourceReferences = context.ApprovedResourceReferences,
                    ApprovedResourceClaims = context.ApprovedResourceClaims,
                    ApprovedResourceCapabilities = context.ApprovedResourceCapabilities,
                    ResourceOperation = context.ResourceOperation,
                };
                if (target is IAgentResourceAuthorityExecutionTarget authorityTarget)
                {
                    var validation = await authorityTarget.ValidateResourceAuthorityAsync(
                        targetContext,
                        invocationToken);
                    if (!validation.IsValid)
                    {
                        return FileToolResult.Error(
                            request.ToolId,
                            validation.ErrorMessage ?? "Resource authority is unavailable.",
                            validation.ErrorCode ?? AgentToolResultErrorCodes.PermissionReapprovalRequired);
                    }
                }
                if (!IsMutation(request.ToolId))
                {
                    return null;
                }
                if (await TryRejectCompoundInstructionPatchAsync(target, context, request, invocationToken) is { } compoundError)
                {
                    return compoundError;
                }
                if (_scopedInstructions is null
                    || !TryBuildMutationProbes(request, out var probes))
                {
                    return null;
                }

                return await _scopedInstructions.PreflightMutationAsync(
                    target,
                    context,
                    request,
                    probes,
                    invocationToken);
            });
    }

    public void DeleteSessionData(Guid sessionId)
        => _scopedInstructions?.DeleteSessionData(sessionId);

    public ValueTask AcknowledgePromptContextAsync(
        AgentPromptContextReceipt receipt,
        CancellationToken cancellationToken = default)
        => _scopedInstructions is null
            ? ValueTask.FromException(new InvalidOperationException(
                "Scoped instruction acknowledgment is unavailable because this Files source uses the legacy disabled constructor."))
            : _scopedInstructions.AcknowledgePromptContextAsync(receipt, cancellationToken);

    private bool TryAcquireTarget(
        IPackageExtensionReference<IAgentExecutionTarget>? selectedReference,
        AgentWorkspaceBindingRecord binding,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out IPackageExtensionLease<IAgentExecutionTarget>? lease)
    {
        var reference = selectedReference ?? ResolveCompatibilityTargetReference(binding);
        if (reference is null || !reference.TryAcquire(out lease))
        {
            lease = null;
            return false;
        }
        if (lease.RetirementToken.IsCancellationRequested
            || !IsBindingMatch(lease.Contribution.Descriptor, binding))
        {
            lease.Dispose();
            lease = null;
            return false;
        }

        return true;
    }

    private IPackageExtensionReference<IAgentExecutionTarget>? ResolveCompatibilityTargetReference(
        AgentWorkspaceBindingRecord binding)
    {
        if (_invocationCatalog is not null)
        {
            foreach (var reference in _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.ExecutionTargets))
            {
                if (!reference.TryAcquire(out var lease))
                {
                    continue;
                }
                using (lease)
                {
                    if (!lease.RetirementToken.IsCancellationRequested
                        && IsBindingMatch(lease.Contribution.Descriptor, binding))
                    {
                        return reference;
                    }
                }
            }

            return null;
        }

        var target = _extensionCatalog.GetExtensions(PackageExtensionPoints.ExecutionTargets)
            .FirstOrDefault(candidate => IsBindingMatch(candidate.Descriptor, binding));
        return target is null ? null : new CompatibilityTargetReference(target);
    }

    private static bool IsBindingMatch(
        AgentExecutionTargetDescriptor descriptor,
        AgentWorkspaceBindingRecord binding)
        => string.Equals(descriptor.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(descriptor.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<TResult> InvokeTargetAsync<TResult>(
        IPackageExtensionLease<IAgentExecutionTarget> lease,
        CancellationToken cancellationToken,
        Func<IAgentExecutionTarget, CancellationToken, ValueTask<TResult>> callback)
    {
        using (lease)
        {
            var packageId = lease.PackageId;
            var retirementToken = lease.RetirementToken;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                retirementToken);
            try
            {
                var result = await callback(lease.Contribution, invocation.Token).ConfigureAwait(false);
                if (retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException(
                        $"Execution-target package '{packageId}' became unavailable while the callback was running.");
                }
                return result;
            }
            catch (OperationCanceledException exception) when (
                retirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Execution-target package '{packageId}' became unavailable while the callback was running.",
                    exception);
            }
        }
    }

    private static void ThrowIfExactTargetUnavailable(
        IPackageExtensionReference<IAgentExecutionTarget>? selectedReference)
    {
        if (selectedReference is not null)
        {
            throw new InvalidOperationException("The selected execution-target package is unavailable.");
        }
    }

    private static bool IsMutation(string toolId)
        => toolId.Equals("write", StringComparison.OrdinalIgnoreCase)
           || toolId.Equals("edit", StringComparison.OrdinalIgnoreCase)
           || toolId.Equals("apply_patch", StringComparison.OrdinalIgnoreCase);

    private static bool TryBuildMutationProbes(
        AgentToolRequest request,
        out IReadOnlyList<AgentScopedInstructionProbe> probes)
    {
        switch (request.ToolId.ToLowerInvariant())
        {
            case "write" when FileToolArguments.TryParseWrite(request.ArgumentsJson, out var write, out _):
                probes = [new AgentScopedInstructionProbe(write.Path)];
                return true;
            case "edit" when FileToolArguments.TryParseEdit(request.ArgumentsJson, out var edit, out _):
                probes = [new AgentScopedInstructionProbe(edit.Path)];
                return true;
            case "apply_patch" when FileToolArguments.TryParsePatch(request.ArgumentsJson, out var patch, out _):
                try
                {
                    probes = FilePatchParser.Parse(patch.PatchText)
                        .GroupBy(operation => operation.Path, StringComparer.Ordinal)
                        .Select(group => new AgentScopedInstructionProbe(group.Key)
                        {
                            FollowFinalSymbolicLink = group.All(operation => operation.Kind != FilePatchOperationKind.Delete),
                        })
                        .ToArray();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
        }

        probes = [];
        return false;
    }

    private static async ValueTask<AgentToolResult?> TryRejectCompoundInstructionPatchAsync(
        IAgentExecutionTarget target,
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ToolId.Equals("apply_patch", StringComparison.OrdinalIgnoreCase)
            || !FileToolArguments.TryParsePatch(request.ArgumentsJson, out var patch, out _))
        {
            return null;
        }

        try
        {
            var operations = FilePatchParser.Parse(patch.PatchText);
            if (operations.Count > FilePatchParser.MaximumOperations)
            {
                return null;
            }
            if (operations.Count <= 1)
            {
                return null;
            }

            var targetContext = new AgentExecutionTargetContext(
                context.SessionId,
                context.ProfileId,
                context.Workspace!,
                context.ExecutionBinding!,
                context.AllowOutsideConfiguredScope)
            {
                ApprovedResourceReferences = context.ApprovedResourceReferences,
                ApprovedResourceClaims = context.ApprovedResourceClaims,
                ApprovedResourceCapabilities = context.ApprovedResourceCapabilities,
                ResourceOperation = context.ResourceOperation,
            };
            var resources = new List<AgentResolvedResource>();
            foreach (var path in operations.Select(operation => operation.Path).Distinct(StringComparer.Ordinal))
            {
                resources.Add(await target.ResolveFileResourceAsync(targetContext, path, cancellationToken));
            }
            if (!resources.Any(resource => resource.IsScopedInstructionDocument
                                           || IsInstructionPath(resource.CanonicalReference))
                && !operations.Any(operation => IsInstructionPath(operation.Path)))
            {
                return null;
            }

            return new AgentToolResult(
                request.ToolId,
                "A patch that changes AGENTS.md cannot mutate another path in the same dispatch.",
                Content: "### Patch not dispatched\n\nSplit the AGENTS.md change into its own apply_patch call, refresh prompt context, and replan later governed mutations.",
                IsError: true,
                ErrorCode: "files-agents-compound-patch-rejected");
        }
        catch (FilePatchOperationLimitException ex)
        {
            return new AgentToolResult(
                request.ToolId,
                ex.Message,
                Content: $"### Patch not dispatched\n\n{ex.Message} No files were changed.",
                IsError: true,
                ErrorCode: "files-scoped-instruction-path-limit");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(
                request.ToolId,
                "The patch targets could not be classified canonically. No files were changed.",
                Content: "### Patch not dispatched\n\nCanonical target classification failed, so the patch was rejected before mutation.",
                IsError: true,
                ErrorCode: "files-agents-patch-classification-failed");
        }
    }

    private static AgentToolResult WithheldAccessResult(
        IAgentExecutionTarget target,
        string toolId,
        string message,
        string errorCode,
        bool requiresRefresh)
        => new(
            toolId,
            "File result withheld pending scoped instruction recovery.",
            Content: $"### File result withheld\n\n{message}\n\nNo file content, directory entries, or search matches from the successful access were returned.",
            IsError: true,
            ErrorCode: errorCode,
            BackendId: FileToolResult.BackendId(target))
        {
            RequiresPromptContextRefresh = requiresRefresh,
        };

    private static bool IsInstructionPath(string path)
        => string.Equals(
            path.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault(),
            "AGENTS.md",
            StringComparison.OrdinalIgnoreCase);

    private static bool TryValidateArguments(AgentToolRequest request, out string? error)
    {
        switch (request.ToolId.ToLowerInvariant())
        {
            case "read":
                return FileToolArguments.TryParseRead(request.ArgumentsJson, out _, out error);
            case "write":
                return FileToolArguments.TryParseWrite(request.ArgumentsJson, out _, out error);
            case "edit":
                return FileToolArguments.TryParseEdit(request.ArgumentsJson, out _, out error);
            case "apply_patch":
                return FileToolArguments.TryParsePatch(request.ArgumentsJson, out _, out error);
            case "grep":
                if (!FileToolArguments.TryParseGrep(request.ArgumentsJson, out var grep, out error))
                {
                    return false;
                }
                return HostSecureFileSearch.TryValidateRequest(
                    new AgentFileSearchRequest(
                        string.IsNullOrWhiteSpace(grep.Path) ? "." : grep.Path,
                        AgentFileSearchKind.Grep,
                        grep.Pattern,
                        grep.Include),
                    out error);
            case "glob":
                if (!FileToolArguments.TryParseGlob(request.ArgumentsJson, out var glob, out error))
                {
                    return false;
                }
                return HostSecureFileSearch.TryValidateRequest(
                    new AgentFileSearchRequest(
                        string.IsNullOrWhiteSpace(glob.Path) ? "." : glob.Path,
                        AgentFileSearchKind.Glob,
                        glob.Pattern),
                    out error);
            default:
                error = $"Unknown file tool '{request.ToolId}'.";
                return false;
        }
    }

    private static string BoundMessage(string message)
    {
        var normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 400 ? normalized : normalized[..400] + "...";
    }

    private sealed class CompatibilityTargetReference(IAgentExecutionTarget target)
        : IPackageExtensionReference<IAgentExecutionTarget>
    {
        public bool TryAcquire(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
            out IPackageExtensionLease<IAgentExecutionTarget>? lease)
        {
            lease = new CompatibilityTargetLease(target);
            return true;
        }
    }

    private sealed class CompatibilityTargetLease(IAgentExecutionTarget target)
        : IPackageExtensionLease<IAgentExecutionTarget>
    {
        private IAgentExecutionTarget? _target = target;

        public string PackageId
        {
            get
            {
                ObjectDisposedException.ThrowIf(_target is null, this);
                return "sunder.package.agent.tools.files.compatibility";
            }
        }

        public IAgentExecutionTarget Contribution
            => Volatile.Read(ref _target)
               ?? throw new ObjectDisposedException(nameof(CompatibilityTargetLease));

        public CancellationToken RetirementToken
        {
            get
            {
                ObjectDisposedException.ThrowIf(_target is null, this);
                return CancellationToken.None;
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _target, null);
    }

}
