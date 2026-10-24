using Sunder.Agent.Execution.Common;
using IExecutionTargetService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Execution_Target.IExecutionTargetService;
using IPermissionSurfaceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Permission_Surface.IPermissionSurfaceService;
using IPromptContextContributorService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Prompt_Context_Contributor.IPromptContextContributorService;
using ISessionCleanerService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Session_Cleaner.ISessionCleanerService;
using IToolSourceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Tools.Files;

[SunderServiceProvider(typeof(IToolSourceService), ProviderId = "files.tools")]
[SunderServiceProvider(typeof(IPermissionSurfaceService), ProviderId = "files.permissions")]
[SunderServiceProvider(typeof(IPromptContextContributorService), ProviderId = "files.prompt.context")]
[SunderServiceProvider(typeof(ISessionCleanerService), ProviderId = "files.session.cleaner")]
public sealed partial class FilesToolSource : IToolSourceService, IPermissionSurfaceService, IPromptContextContributorService, ISessionCleanerService, ISunderWorkerActivationService
{
    private const int MaxPostAccessScopePaths = 64;
    private readonly Lazy<ScopedInstructionContextService> _scopedInstructions;

    /// <summary>Creates a Files source with required scoped-instruction persistence and enforcement.</summary>
    public FilesToolSource(IPackageContext packageContext)
    {
        ArgumentNullException.ThrowIfNull(packageContext);
        _scopedInstructions = new Lazy<ScopedInstructionContextService>(
            () => new ScopedInstructionContextService(packageContext),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); _ = ScopedInstructions; return ValueTask.CompletedTask; }

    public string SourceId => FileToolDescriptorRegistry.SourceId;

    public string DisplayName => FileToolDescriptorRegistry.DisplayName;

    public string SourceKind => "workspace";

    public string SurfaceId => "files";

    public string ContributorId => FileToolDescriptorRegistry.SourceId;

    public string CleanerId => "agent.files.scoped-instruction-claims";

    public string AcknowledgmentSinkId => "agent.files.scoped-instruction-acknowledgment";

    public ValueTask<AgentToolSourceDescriptor> DescribeAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentToolSourceDescriptor { SourceId = SourceId, DisplayName = DisplayName, SourceKind = SourceKind }); }

    ValueTask<AgentPermissionSurfaceDescriptor> IPermissionSurfaceService.DescribeAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentPermissionSurfaceDescriptor { SurfaceId = SurfaceId, DisplayName = DisplayName, Actions = ListActions() }); }

    ValueTask<AgentPromptContextContributorDescriptor> IPromptContextContributorService.DescribeAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentPromptContextContributorDescriptor { ContributorId = ContributorId, DisplayName = DisplayName, AcknowledgmentSinkId = AcknowledgmentSinkId }); }

    ValueTask<AgentServiceIdentity> ISessionCleanerService.DescribeAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentServiceIdentity { Id = CleanerId, DisplayName = CleanerId }); }

    public ValueTask<AgentToolPresentation?> PresentationAsync(AgentToolPresentationRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(FileToolPresentationAdapter.Resolve(request)); }

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(FileToolDescriptorRegistry.Descriptors);
    }

    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(
        CYounics_Ai_Agent_Tool_Source_ToolSourceGetReadinessRequest request,
        CancellationToken cancellationToken = default)
    {
        var (toolId, context) = (request.ToolId, request.Context);
        if (!FileToolDescriptorRegistry.Contains(toolId))
        {
            return null;
        }

        if (context.Workspace is null)
        {
            return new() { ToolId = toolId, Status = AgentToolReadinessStatus.Failed, Message = "File tools require a selected workspace." };
        }

        if (context.ExecutionBinding is null)
        {
            return new() { ToolId = toolId, Status = AgentToolReadinessStatus.Failed, Message = "The selected workspace is not bound to an installed execution target." };
        }
        var targetLease = await AcquireTargetAsync(
                context.ExecutionTarget,
                context.ExecutionBinding,
                cancellationToken)
            .ConfigureAwait(false);
        if (targetLease is null)
        {
            return new() { ToolId = toolId, Status = AgentToolReadinessStatus.Failed, Message = "The selected workspace is not bound to an installed execution target." };
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, invocationToken) =>
            {
                if ((toolId.Equals("grep", StringComparison.OrdinalIgnoreCase)
                     || toolId.Equals("glob", StringComparison.OrdinalIgnoreCase))
                    && !FilesExecutionFacets.SupportsFacet(target, AgentExecutionFacetIds.StructuredFileSearch))
                {
                    return new AgentToolReadiness { ToolId = toolId, Status = AgentToolReadinessStatus.Failed,
                        Message = "The selected target does not support governed structured file search." };
                }

                if (!FilesExecutionFacets.SupportsFacet(target, AgentExecutionFacetIds.ScopedInstructionDiscovery)
                    || !FilesExecutionFacets.SupportsFacet(target, AgentExecutionFacetIds.ExecutionScope))
                {
                    return new AgentToolReadiness { ToolId = toolId, Status = AgentToolReadinessStatus.Failed,
                        Message = "The selected target does not support scoped-instruction discovery required by first-party Files enforcement." };
                }

                var readiness = await target.Service.ReadinessAsync(
                    FileExecutionTarget.Context(context.SessionId, context.Profile?.ProfileId, context.Workspace, context.ExecutionBinding) with
                    {
                        ExpectedConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
                    },
                    invocationToken);
                var ready = readiness.Status == AgentExecutionTargetReadinessStatus.Ready && target.Descriptor.SupportsFiles;
                return new AgentToolReadiness { ToolId = toolId, Status = ready ? AgentToolReadinessStatus.Ready : AgentToolReadinessStatus.Failed,
                    Message = ready ? "Workspace file tools are ready with scoped AGENTS.md enforcement." : readiness.Message };
            },
            () => new AgentToolReadiness { ToolId = toolId, Status = AgentToolReadinessStatus.Failed,
                Message = "The selected execution target became unavailable while readiness was being checked." });
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        CYounics_Ai_Agent_Tool_Source_ToolSourceExecuteRequest invocation,
        CancellationToken cancellationToken = default)
    {
        var (context, request) = (invocation.Context, invocation.Request);
        if (context.Workspace is null)
        {
            return FileToolResult.Error(request.ToolId, "File tools require a selected workspace.", "files-workspace-required");
        }

        if (context.ExecutionBinding is null)
        {
            return FileToolResult.Error(request.ToolId, "The selected workspace is not bound to an installed execution target.", "files-target-required");
        }
        var targetLease = await AcquireTargetAsync(
                context.ExecutionTarget,
                context.ExecutionBinding,
                cancellationToken)
            .ConfigureAwait(false);
        if (targetLease is null)
        {
            return FileToolResult.Error(request.ToolId, "The selected workspace is not bound to an installed execution target.", "files-target-required");
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, invocationToken) =>
            {
                var targetContext = FileExecutionTarget.Context(context.SessionId, context.ProfileId, context.Workspace, context.ExecutionBinding) with
                {
                    AllowOutsideConfiguredScope = context.AllowOutsideConfiguredScope,
                    ApprovedResourceReferences = context.ApprovedResourceReferences,
                    ApprovedResourceClaims = context.ApprovedResourceClaims,
                    ApprovedResourceCapabilities = context.ApprovedResourceCapabilities,
                    ResourceOperation = context.ResourceOperation,
                    ExpectedConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
                };
                try
                {
                    if (IsMutation(request.ToolId)
                        && await PreflightAsync(new() { Context = context, Request = request }, invocationToken) is { } deferred)
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
                            accessProbes = [new() { Path = read.Path, IsDirectory = readResult.IsDirectory, FollowFinalSymbolicLink = true }];
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
                                        .Select(path => new AgentScopedInstructionProbe { Path = path, IsDirectory = false, FollowFinalSymbolicLink = true })
                                        .ToArray();
                                    break;
                                }
                            case "glob":
                                {
                                    var search = await FileSearchHandler.ExecuteGlobAsync(target, targetContext, request, invocationToken);
                                    result = search.Result;
                                    var directories = FileToolArguments.TryParseGlob(request.ArgumentsJson, out var glob, out _)
                                        && Sunder.Agent.Execution.Common.FileSearchRequestValidation.IsDirectoryGlob(glob.Pattern);
                                    accessProbes = search.MatchedPaths
                                        .Select(path => new AgentScopedInstructionProbe { Path = path, IsDirectory = directories, FollowFinalSymbolicLink = false })
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

                    if (!result.IsError && accessProbes.Count > 0)
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
                            var claim = await ScopedInstructions.ClaimAfterAccessAsync(
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
                        catch (SunderRpcException)
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
                catch (Exception ex) when (ex is not OperationCanceledException and not SunderRpcException)
                {
                    return FileToolResult.Error(request.ToolId, ex.Message, "files-execution");
                }
                finally
                {
                    if (FilesExecutionFacets.SupportsFacet(target, AgentExecutionFacetIds.ResourceAuthority)
                        && !target.Reference.RetirementToken.IsCancellationRequested)
                    {
                        try
                        {
                            await target.Service.ReleaseResourceAuthorityAsync(targetContext.ApprovedResourceCapabilities, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (SunderRpcException exception) when (exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
                        {
                            // Retirement revoked authority; cleanup must not replace an unsafe operation failure.
                        }
                        catch (OperationCanceledException) when (target.Reference.RetirementToken.IsCancellationRequested)
                        {
                            // The exact activation's authority is already revoked.
                        }
                    }
                }
            },
            () => FileToolResult.Error(
                request.ToolId,
                "The selected execution target became unavailable while the file operation was running.",
                AgentToolResultErrorCodes.PackageUnavailable),
            preserveDispatchFailure: IsMutation(request.ToolId));
    }

    public async ValueTask<AgentPermissionRequest?> PermissionAsync(
        CYounics_Ai_Agent_Tool_Source_ToolSourcePermissionRequest invocation,
        CancellationToken cancellationToken = default)
    {
        var (context, request) = (invocation.Context, invocation.Request);
        if (context.ExecutionBinding is null)
        {
            return await FilePermissionPlanner.BuildAsync(null, context, request, cancellationToken);
        }
        var targetLease = await AcquireTargetAsync(
                context.ExecutionTarget,
                context.ExecutionBinding,
                cancellationToken)
            .ConfigureAwait(false);
        if (targetLease is null)
        {
            return await FilePermissionPlanner.BuildAsync(null, context, request, cancellationToken);
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            (target, invocationToken) => FilePermissionPlanner.BuildAsync(target, context, request, invocationToken),
            static () => null);
    }

    public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
        => FilePermissionPlanner.Actions;

    public async ValueTask<AgentPromptContextContribution?> ContributeAsync(
        AgentPromptContextRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ExecutionBinding is null)
        {
            return null;
        }
        var targetLease = await AcquireTargetAsync(
                request.ExecutionTarget,
                request.ExecutionBinding,
                cancellationToken)
            .ConfigureAwait(false);
        if (targetLease is null)
        {
            return null;
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, invocationToken) =>
            {
                var scopeContribution = await FileSystemPromptBuilder.BuildAsync(target, request, invocationToken);
                var blocks = scopeContribution?.Blocks.ToList() ?? [];
                blocks.AddRange(await ScopedInstructions.BuildPromptContextAsync(target, request, invocationToken));

                return blocks.Count == 0 ? null : new AgentPromptContextContribution { Blocks = blocks };
            },
            static () => null);
    }

    public ValueTask<AgentToolResult?> ValidateArgumentsAsync(AgentToolRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryValidateArguments(request, out var error)
            ? null : FileToolResult.Error(request.ToolId, error!, "files-arguments-invalid"));
    }

    public async ValueTask<AgentToolResult?> PreflightAsync(
        CYounics_Ai_Agent_Tool_Source_ToolSourcePreflightRequest invocation,
        CancellationToken cancellationToken = default)
    {
        var (context, request) = (invocation.Context, invocation.Request);
        if (!TryValidateArguments(request, out var validationError))
        {
            return FileToolResult.Error(request.ToolId, validationError!, "files-arguments-invalid");
        }
        if (context.Workspace is null
            || context.ExecutionBinding is null)
        {
            return null;
        }
        var targetLease = await AcquireTargetAsync(
                context.ExecutionTarget,
                context.ExecutionBinding,
                cancellationToken)
            .ConfigureAwait(false);
        if (targetLease is null)
        {
            return null;
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, invocationToken) =>
            {
                var targetContext = FileExecutionTarget.Context(context.SessionId, context.ProfileId, context.Workspace, context.ExecutionBinding) with
                {
                    AllowOutsideConfiguredScope = context.AllowOutsideConfiguredScope,
                    ApprovedResourceReferences = context.ApprovedResourceReferences,
                    ApprovedResourceClaims = context.ApprovedResourceClaims,
                    ApprovedResourceCapabilities = context.ApprovedResourceCapabilities,
                    ResourceOperation = context.ResourceOperation,
                    ExpectedConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
                };
                if (FilesExecutionFacets.SupportsFacet(target, AgentExecutionFacetIds.ResourceAuthority))
                {
                    var validation = await target.Service.ValidateResourceAuthorityAsync(
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
                if (!TryBuildMutationProbes(request, out var probes))
                {
                    return null;
                }

                return await ScopedInstructions.PreflightMutationAsync(
                    target,
                    context,
                    request,
                    probes,
                    invocationToken);
            },
            () => FileToolResult.Error(
                request.ToolId,
                "The selected execution target became unavailable during file preflight.",
                AgentToolResultErrorCodes.PackageUnavailable));
    }

    public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); ScopedInstructions.DeleteSessionData(sessionId); return ValueTask.CompletedTask; }

    public ValueTask AcknowledgeAsync(
        AgentPromptContextReceipt receipt,
        CancellationToken cancellationToken = default)
        => ScopedInstructions.AcknowledgePromptContextAsync(receipt, cancellationToken);

    private ScopedInstructionContextService ScopedInstructions => _scopedInstructions.Value;

    private static async ValueTask<FileExecutionTarget?> AcquireTargetAsync(
        ServiceReference<IExecutionTargetService>? selectedReference,
        AgentWorkspaceBindingRecord binding,
        CancellationToken cancellationToken)
    {
        if (selectedReference is null)
        {
            return null;
        }

        var retirementToken = selectedReference.RetirementToken;
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            retirementToken);
        try
        {
            await selectedReference.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            var descriptor = await selectedReference.Service.DescribeAsync(invocation.Token).ConfigureAwait(false);
            if (retirementToken.IsCancellationRequested || !IsBindingMatch(descriptor, binding))
            {
                return null;
            }

            return new(selectedReference, descriptor);
        }
        catch (Exception exception) when (IsTargetUnavailable(
            exception,
            retirementToken,
            cancellationToken))
        {
            return null;
        }
    }

    private static bool IsBindingMatch(
        AgentExecutionTargetDescriptor descriptor,
        AgentWorkspaceBindingRecord binding)
        => string.Equals(descriptor.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(descriptor.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<TResult> InvokeTargetAsync<TResult>(
        FileExecutionTarget target,
        CancellationToken cancellationToken,
        Func<FileExecutionTarget, CancellationToken, ValueTask<TResult>> callback,
        Func<TResult> unavailableResult,
        bool preserveDispatchFailure = false)
    {
            var retirementToken = target.Reference.RetirementToken;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                retirementToken);
            var callbackCompleted = false;
            try
            {
                var result = await callback(target, invocation.Token).ConfigureAwait(false);
                callbackCompleted = true;
                cancellationToken.ThrowIfCancellationRequested();
                await target.Reference.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
                if (retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    if (preserveDispatchFailure)
                    {
                        retirementToken.ThrowIfCancellationRequested();
                    }
                    return unavailableResult();
                }
                return result;
            }
            catch (Exception exception) when (IsTargetUnavailable(
                exception,
                retirementToken,
                cancellationToken)
                && (!preserveDispatchFailure || !callbackCompleted && exception is SunderRpcException
                {
                    Error.DispatchState: SunderRpcDispatchState.NotDispatched,
                    Error.RetrySafety: SunderRpcRetrySafety.Safe,
                }))
            {
                // Mutating calls must preserve uncertain dispatch for Agent's ambiguity boundary.
                return unavailableResult();
            }
    }

    private static bool IsTargetUnavailable(
        Exception exception,
        CancellationToken retirementToken,
        CancellationToken callerCancellationToken)
        => !callerCancellationToken.IsCancellationRequested
           && (exception is SunderRpcException
               {
                   Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable,
               }
               || retirementToken.IsCancellationRequested
               && (exception is OperationCanceledException
                   || exception is SunderRpcException { Error.Kind: SunderRpcErrorKind.Cancelled }));

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
                probes = [new() { Path = write.Path, IsDirectory = false, FollowFinalSymbolicLink = true }];
                return true;
            case "edit" when FileToolArguments.TryParseEdit(request.ArgumentsJson, out var edit, out _):
                probes = [new() { Path = edit.Path, IsDirectory = false, FollowFinalSymbolicLink = true }];
                return true;
            case "apply_patch" when FileToolArguments.TryParsePatch(request.ArgumentsJson, out var patch, out _):
                try
                {
                    probes = FilePatchPaths.Affected(FilePatchParser.Parse(patch.PatchText))
                        .GroupBy(path => path.Path, StringComparer.Ordinal)
                        .Select(group => new AgentScopedInstructionProbe
                        {
                            Path = group.Key, IsDirectory = false,
                            FollowFinalSymbolicLink = group.All(path => !path.IsDelete),
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
        FileExecutionTarget target,
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
            var affected = FilePatchPaths.Affected(operations);
            if (affected.Count <= 1)
            {
                return null;
            }

            var targetContext = FileExecutionTarget.Context(context.SessionId, context.ProfileId, context.Workspace!, context.ExecutionBinding!) with
            {
                AllowOutsideConfiguredScope = context.AllowOutsideConfiguredScope,
                ApprovedResourceReferences = context.ApprovedResourceReferences,
                ApprovedResourceClaims = context.ApprovedResourceClaims,
                ApprovedResourceCapabilities = context.ApprovedResourceCapabilities,
                ResourceOperation = context.ResourceOperation,
                ExpectedConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
            };
            var resources = new List<AgentResolvedResource>();
            foreach (var path in affected.Select(path => path.Path).Distinct(StringComparer.Ordinal))
            {
                resources.Add(await target.Service.ResolveFileAsync(new() { Context = targetContext, Path = path }, cancellationToken));
            }
            if (!resources.Any(resource => resource.IsScopedInstructionDocument
                                           || IsInstructionPath(resource.CanonicalReference))
                && !affected.Any(path => IsInstructionPath(path.Path)))
            {
                return null;
            }

            return FileToolResult.Error(
                request.ToolId,
                "A patch that changes AGENTS.md cannot mutate another path in the same dispatch.",
                "files-agents-compound-patch-rejected") with
            { Content = "### Patch not dispatched\n\nSplit the AGENTS.md change into its own apply_patch call, refresh prompt context, and replan later governed mutations." };
        }
        catch (FilePatchOperationLimitException ex)
        {
            return FileToolResult.Error(request.ToolId, ex.Message, "files-scoped-instruction-path-limit") with
            { Content = $"### Patch not dispatched\n\n{ex.Message} No files were changed." };
        }
        catch (FilePatchPathLimitException ex)
        {
            return FileToolResult.Error(request.ToolId, ex.Message, "files-scoped-instruction-path-limit") with
            { Content = $"### Patch not dispatched\n\n{ex.Message} No files were changed." };
        }
        catch (SunderRpcException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FileToolResult.Error(
                request.ToolId,
                "The patch targets could not be classified canonically. No files were changed.",
                "files-agents-patch-classification-failed") with
            { Content = "### Patch not dispatched\n\nCanonical target classification failed, so the patch was rejected before mutation." };
        }
    }

    private static AgentToolResult WithheldAccessResult(
        FileExecutionTarget target,
        string toolId,
        string message,
        string errorCode,
        bool requiresRefresh)
        => FileToolResult.Error(toolId, "File result withheld pending scoped instruction recovery.", errorCode) with
        {
            Content = $"### File result withheld\n\n{message}\n\nNo file content, directory entries, or search matches from the successful access were returned.",
            BackendId = FileToolResult.BackendId(target),
            RequiresPromptContextRefresh = requiresRefresh,
        };

    private static bool IsInstructionPath(string path)
        => string.Equals(
            path.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault(),
            "AGENTS.md",
            StringComparison.OrdinalIgnoreCase);

    private static bool TryValidateArguments(AgentToolRequest request, out string? error)
    {
        if (!AgentToolArgumentObject.TryParse(request.ArgumentsJson, out var arguments, out error)) return false;
        string[] allowed = request.ToolId.ToLowerInvariant() switch
        {
            "read" => ["path", "offset", "limit"],
            "write" => ["path", "content"],
            "edit" => ["path", "oldString", "newString", "replaceAll"],
            "apply_patch" => ["patchText"],
            "grep" => ["pattern", "path", "include", "literal", "caseSensitive", "limit"],
            "glob" => ["pattern", "path", "hidden", "limit"],
            _ => [],
        };
        foreach (var key in arguments!.ToDictionary().Keys)
        {
            if (allowed.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            error = $"Invalid {request.ToolId} arguments: Unsupported parameter `{key}`. Use the advertised tool schema.";
            return false;
        }
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
                return FileSearchRequestValidation.TryValidateRequest(
                    new AgentFileSearchRequest { Path = string.IsNullOrWhiteSpace(grep.Path) ? "." : grep.Path,
                        Kind = AgentFileSearchKind.Grep, Pattern = grep.Pattern, Include = grep.Include,
                        Literal = grep.Literal, CaseSensitive = grep.CaseSensitive, Limit = grep.Limit, Hidden = false },
                    out error);
            case "glob":
                if (!FileToolArguments.TryParseGlob(request.ArgumentsJson, out var glob, out error))
                {
                    return false;
                }
                return FileSearchRequestValidation.TryValidateRequest(
                    new AgentFileSearchRequest { Path = string.IsNullOrWhiteSpace(glob.Path) ? "." : glob.Path,
                        Kind = AgentFileSearchKind.Glob, Pattern = glob.Pattern, Include = null,
                        Hidden = glob.Hidden, Limit = glob.Limit, Literal = false, CaseSensitive = true },
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

}
