using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tests;

internal class MutationToolSource(string toolId) : IAgentToolSource, IRegressionTestArgumentValidator
{
    private int _executionCount;
    private int _readinessCount;
    private TaskCompletionSource? _executionStarted;
    private TaskCompletionSource? _executionRelease;

    public string ToolId { get; } = toolId;

    public int ExecutionCount => Volatile.Read(ref _executionCount);

    public int ReadinessCount => Volatile.Read(ref _readinessCount);

    public bool ThrowOnExecution { get; init; }

    public AgentToolResult? ArgumentValidationResult { get; init; }

    public Func<AgentToolRequest, AgentToolResult?>? ArgumentValidator { get; init; }

    public ValueTask<AgentToolResult?> ValidateArgumentsAsync(AgentToolRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ArgumentValidator is { } validator ? validator(request) : ArgumentValidationResult);
    }

    public string SourceId => "permission-hardening-source";

    public string DisplayName => "Permission Hardening Source";

    public string SourceKind => "test";

    protected AgentToolDescriptor Descriptor => new(
        ToolId,
        "Mutating Test Tool",
        "Mutates deterministic test state.",
        IsReadOnly: false,
        ArgumentsJsonSchema: "{\"type\":\"object\"}",
        SourceKind: SourceKind,
        SourceId: SourceId,
        SourceDisplayName: DisplayName);

    public Task ExecutionStarted => _executionStarted?.Task ?? Task.CompletedTask;

    public void BlockExecution()
    {
        _executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _executionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ReleaseExecution() => _executionRelease?.TrySetResult();

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([Descriptor]);

    public ValueTask<AgentToolReadiness?> GetReadinessAsync(
        string requestedToolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _readinessCount);
        return ValueTask.FromResult<AgentToolReadiness?>(
            string.Equals(requestedToolId, ToolId, StringComparison.OrdinalIgnoreCase)
                ? new AgentToolReadiness(ToolId, AgentToolReadinessStatus.Ready, "Ready.")
                : null);
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _executionCount);
        _executionStarted?.TrySetResult();
        if (_executionRelease is not null)
        {
            await _executionRelease.Task.WaitAsync(cancellationToken);
        }

        if (ThrowOnExecution)
        {
            throw new InvalidOperationException("Injected mutating tool execution failure.");
        }

        return new AgentToolResult(ToolId, "Mutating tool executed.", Content: "ok");
    }
}

internal sealed class PermissionAwareMutationToolSource(
    string toolId,
    string boundaryId = "test-boundary",
    bool includeResourceClaim = false)
    : MutationToolSource(toolId), IAgentPermissionAwareToolSource, IAgentPermissionSurface
{
    private int _permissionResolutionCount;
    private TaskCompletionSource? _permissionResolutionStarted;
    private TaskCompletionSource? _permissionResolutionRelease;

    public int PermissionResolutionCount => Volatile.Read(ref _permissionResolutionCount);

    public bool ThrowOnPermissionRevalidation { get; init; }

    public bool OmitPermissionRequest { get; set; }

    public AgentPermissionRequest? PermissionOverride { get; set; }

    public Task PermissionResolutionStarted =>
        _permissionResolutionStarted?.Task ?? Task.CompletedTask;

    public string SurfaceId => "permission-hardening-surface";

    public void BlockPermissionResolution()
    {
        _permissionResolutionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _permissionResolutionRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ReleasePermissionResolution() => _permissionResolutionRelease?.TrySetResult();

    public async ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        var resolutionCount = Interlocked.Increment(ref _permissionResolutionCount);
        if (ThrowOnPermissionRevalidation && resolutionCount > 1)
        {
            throw new InvalidOperationException("Injected permission revalidation failure.");
        }
        if (_permissionResolutionRelease is not null)
        {
            _permissionResolutionStarted?.TrySetResult();
            await _permissionResolutionRelease.Task.WaitAsync(cancellationToken);
        }

        if (OmitPermissionRequest) return null;
        if (PermissionOverride is { } permissionOverride) return permissionOverride;
        var permission = new AgentPermissionRequest(
            "test.mutate",
            boundaryId,
            "Allow the deterministic mutation?",
            ToolId: request.ToolId,
            WorkspaceId: context.Workspace?.WorkspaceId,
            BindingId: context.ExecutionBinding?.BindingId,
            ResourceDisplayName: "test state",
            ResourceReference: "test://state",
            IsMutation: true);
        if (!includeResourceClaim)
        {
            return permission;
        }

        var operation = context.ResourceOperation
            ?? throw new InvalidOperationException("Resource claim planning requires an invocation binding.");
        var claim = new AgentResourceClaim(
            1,
            "permission-hardening-resource-v1",
            "/test/resource",
            string.Equals(boundaryId, AgentPermissionBoundaryIds.ConfiguredScope, StringComparison.Ordinal)
                ? "/test"
                : null,
            new string('c', 64),
            true,
            "RegularFile",
            "stable-test-target",
            false,
            true,
            "test.mutate",
            context.Workspace?.WorkspaceId ?? string.Empty,
            operation.WorkspaceGeneration,
            context.ExecutionBinding?.BindingId ?? string.Empty,
            operation.BindingGeneration,
            operation.ToolCallId,
            0,
            operation.ToolOwnerPackageId,
            operation.ExecutionTargetOwnerPackageId);
        return permission with
        {
            ResourceClaims = [claim],
            ResourceCapabilities = string.Equals(
                                      boundaryId,
                                      AgentPermissionBoundaryIds.OutsideConfiguredScope,
                                      StringComparison.Ordinal)
                                  && operation.CanIssueOutsideAuthority
                ? [$"test-resource-authority-v1:{operation.AuthorityActivationId}"]
                : [],
        };
    }

    public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
        =>
        [
            new AgentPermissionActionDescriptor(
                "test.mutate",
                "Mutate test state",
                "Mutates deterministic test state.",
                [
                    new AgentPermissionBoundaryDescriptor(
                        boundaryId,
                        "Test boundary",
                        "Requires approval.",
                        AgentPermissionDecision.Ask),
                ]),
        ];
}
