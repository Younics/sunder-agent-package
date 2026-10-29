using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Services;
using IToolSourceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService;

namespace Sunder.Package.Agent.Builder;

/// <summary>Optional Agent integration. All local operations cross the generated owner-only App boundary.</summary>
[SunderPackageCodeRole(SunderPackageCodeRole.Worker)]
[SunderServiceProvider(typeof(IToolSourceService), ProviderId = "maker.tools")]
public sealed partial class BuilderToolSource(BuilderRuntimeService runtime) : IToolSourceService
{
    private readonly ConcurrentDictionary<string, Captured> _accepted = new(StringComparer.Ordinal);
    private readonly object _admission = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyList<AgentToolDescriptor> Tools = CreateToolset();

    public ValueTask<AgentToolSourceDescriptor> DescribeAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentToolSourceDescriptor { SourceId = "package-maker", DisplayName = "Package Maker", SourceKind = "workspace" }); }
    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Tools); }
    public ValueTask<AgentToolReadiness?> GetReadinessAsync(CYounics_Ai_Agent_Tool_Source_ToolSourceGetReadinessRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!IsToolset(request.ToolId)) return ValueTask.FromResult<AgentToolReadiness?>(null);
        var local = request.Context.Workspace is not null && request.Context.ExecutionBinding is { IsEnabled: true, ContributionId: "local" } binding
            && binding.WorkspaceId == request.Context.Workspace.WorkspaceId;
        var ready = local && runtime.HasDevelopmentApp;
        return ValueTask.FromResult<AgentToolReadiness?>(new AgentToolReadiness { ToolId = request.ToolId, Status = ready ? AgentToolReadinessStatus.Ready : AgentToolReadinessStatus.Failed,
            Message = ready ? "The local Maker App activation is available." : "Maker tools require a selected local workspace and one active Maker App integration; opening its page is not required." });
    }
    public ValueTask<AgentToolResult?> ValidateArgumentsAsync(AgentToolRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(ValidateToolset(request)); }

    public async ValueTask<AgentToolResult?> PreflightAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePreflightRequest invocation, CancellationToken token = default)
    {
        try { return await PrepareToolsetAsync(invocation.Context, invocation.Request, token).ConfigureAwait(false); }
        catch (BuilderDevelopmentRequestException error) { return RequestFailure(invocation.Request.ToolId, error.Failure); }
        catch (Sunder.Sdk.Rpc.SunderRpcException error) when (IsExpectedUnavailable(error))
        { return RequestFailure(invocation.Request.ToolId, new("maker.development.unavailable", error.Error.Message)); }
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(CYounics_Ai_Agent_Tool_Source_ToolSourceExecuteRequest invocation, CancellationToken token = default)
    {
        try { return await ExecuteToolsetAsync(invocation.Context, invocation.Request, token).ConfigureAwait(false); }
        catch (BuilderDevelopmentRequestException error)
        { return RequestFailure(invocation.Request.ToolId, error.Failure, OperationId(invocation.Context, invocation.Request)); }
        catch (Sunder.Sdk.Rpc.SunderRpcException error) when (IsExpectedUnavailable(error))
        {
            var attempted = _accepted.ContainsKey(OperationId(invocation.Context, invocation.Request));
            return RequestFailure(invocation.Request.ToolId, new("maker.development.unavailable", error.Error.Message, attempted), OperationId(invocation.Context, invocation.Request));
        }
    }

    public ValueTask<AgentPermissionRequest?> PermissionAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePermissionRequest invocation, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ValidateToolset(invocation.Request) is null
            ? ToolsetPermission(invocation.Context, invocation.Request) : null);
    }
    public ValueTask<AgentToolPresentation?> PresentationAsync(AgentToolPresentationRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentToolPresentation?>(null); }

    private static bool LocalContext(AgentToolExecutionContext context) => context.SessionId is not null && context.Workspace is not null
        && context.ExecutionBinding is { IsEnabled: true, ContributionId: "local" } binding && binding.WorkspaceId == context.Workspace.WorkspaceId && context.ExecutionTarget is not null;
    private static bool Matches(Captured captured, AgentToolExecutionContext context, string entry)
        => captured.SessionId == context.SessionId && captured.WorkspaceId == context.Workspace?.WorkspaceId && captured.EntryPoint == entry
            && captured.ProfileId == context.ProfileId && captured.Binding == context.ExecutionBinding && captured.ConfigurationGeneration == context.ExecutionTargetConfigurationGeneration
            && captured.WorkspaceUpdatedAtUtc == context.Workspace?.UpdatedAtUtc;
    private static async ValueTask<string?> ValidateTargetAsync(AgentToolExecutionContext context,
        ServiceReference<Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Execution_Target.IExecutionTargetService> reference,
        CancellationToken token)
    {
        if (reference.RetirementToken.IsCancellationRequested)
            return "The original execution target was retired. Refresh the workspace before accepting new work.";
        await reference.EnsureCurrentAsync(token).ConfigureAwait(false);
        var target = await reference.Service.DescribeAsync(token).ConfigureAwait(false);
        if (target.TargetId != context.ExecutionBinding!.ContributionId || target.TargetKind != "local")
            return "Maker cannot reinterpret paths belonging to a non-local or stale execution target.";
        var targetContext = new AgentExecutionTargetContext
        {
            SessionId = context.SessionId, ProfileId = context.ProfileId, Workspace = context.Workspace!, Binding = context.ExecutionBinding,
            ExpectedConfigurationGeneration = context.ExecutionTargetConfigurationGeneration, AllowOutsideConfiguredScope = false,
            ApprovedResourceReferences = [], ApprovedResourceClaims = [], ApprovedResourceCapabilities = [], CapturePostMutationResource = false,
        };
        if (await reference.Service.ConfigurationGenerationAsync(targetContext, token).ConfigureAwait(false) != context.ExecutionTargetConfigurationGeneration)
            return "The local execution target configuration changed after authorized preflight. Refresh the workspace before accepting new work.";
        var ready = await reference.Service.ReadinessAsync(targetContext, token).ConfigureAwait(false);
        if (ready.TargetKind != "local" || ready.TargetId != target.TargetId)
            return "The execution target readiness does not identify the selected local target.";
        return ready.Status == AgentExecutionTargetReadinessStatus.Ready ? null : ready.Message;
    }
    private static string Key(AgentToolExecutionContext context, AgentToolRequest request)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.SessionId}\n{context.RunId}\n{context.RunRevision}\n{context.ToolCallId}\n{request.ToolId}\n{request.ArgumentsJson}")));
    private static string OperationId(AgentToolExecutionContext context, AgentToolRequest request)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.SessionId}\n{context.RunId}\n{context.RunRevision}\n{context.ToolCallId}\n{request.ToolId}")))[..32];
    private static AgentToolResult Failure(string tool, string error) => new()
    { ToolId = tool, Summary = "Package Maker request rejected", Content = error, IsError = true, ErrorCode = "maker.invalid-request", WasTruncated = false, RequiresPromptContextRefresh = false };
    private static AgentToolResult RequestFailure(string tool, BuilderDevelopmentFailure error, string? operationId = null) => Failure(tool, error.Message) with
    {
        ErrorCode = error.OutcomeUnknown ? Sunder.Package.Agent.Contracts.Models.AgentToolResultErrorCodes.ToolOutcomeAmbiguous : error.Code,
        Summary = error.OutcomeUnknown ? "Package operation outcome unconfirmed; inspect the original operationId and do not replay its mutation." : "Package Maker request rejected",
        Content = error.OutcomeUnknown ? JsonSerializer.Serialize(new { operationId, error.Code, error.Message, outcomeUnknown = true }, Json) : error.Message,
    };
    private static bool IsExpectedUnavailable(Sunder.Sdk.Rpc.SunderRpcException error)
        => error.Error is { Kind: Sunder.Sdk.Rpc.SunderRpcErrorKind.Unavailable, Code: "maker.development.unavailable" }
            || error.Error.Kind is Sunder.Sdk.Rpc.SunderRpcErrorKind.Unavailable
                or Sunder.Sdk.Rpc.SunderRpcErrorKind.StaleEndpoint or Sunder.Sdk.Rpc.SunderRpcErrorKind.ResourceExhausted
                or Sunder.Sdk.Rpc.SunderRpcErrorKind.DeadlineExceeded;
    private static bool IsProjectEntryPoint(string path) => Path.IsPathFullyQualified(path)
        && (Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Equals("sunder.package.json", StringComparison.OrdinalIgnoreCase));
    private sealed record Captured(string AppId, string RequestJson, Guid? SessionId, string WorkspaceId, string EntryPoint,
        string? ProfileId, AgentWorkspaceBindingRecord Binding, string? ConfigurationGeneration, Sunder.Sdk.Rpc.SunderRpcOffsetDateTime WorkspaceUpdatedAtUtc,
        ServiceReference<Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Execution_Target.IExecutionTargetService> Target);
}
