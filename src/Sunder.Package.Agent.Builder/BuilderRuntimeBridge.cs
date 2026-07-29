using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Builder;

internal static class BuilderRuntimeOperations
{
    internal static readonly PackageRuntimeOperation<BuilderRuntimeRequest, BuilderRuntimeResponse> Execute =
        new("agent.builder.execute.v1");
}

internal static class BuilderRuntimePayloadLimits
{
    internal const int MaximumRequestBytes = 1024 * 1024;
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    internal const int ResponseSafetyBytes = 64 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static int GetSerializedSize<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Length;
}

internal enum BuilderRuntimeOperationKind
{
    ListWorkspaces,
    ResolveWorkspace,
    ExecuteProcess,
    ExecuteShell,
    MapToHostPath,
    AddPathEntry,
}

internal sealed record BuilderRuntimeRequest(
    BuilderRuntimeOperationKind Kind,
    string? WorkspaceId = null,
    string? FileName = null,
    IReadOnlyList<string>? Arguments = null,
    string? Command = null,
    string? WorkingDirectory = null,
    int TimeoutSeconds = 600,
    string? ExecutionPath = null);

internal sealed record BuilderRuntimeResponse(
    IReadOnlyList<AgentWorkspaceRecord>? Workspaces = null,
    BuilderWorkspaceExecutionProjection? Execution = null,
    BuilderProcessResult? Process = null,
    AgentExecutionPathMapping? PathMapping = null);

internal sealed record BuilderWorkspaceExecutionProjection(
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding,
    AgentExecutionTargetDescriptor Target,
    AgentExecutionScopeDescriptor Scope,
    AgentExecutionShellDescriptor Shell,
    bool SupportsPathMapping,
    bool SupportsPathEnvironment);

internal interface IBuilderRuntimeGateway
{
    ValueTask<BuilderRuntimeResponse> InvokeAsync(
        BuilderRuntimeRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class BuilderAppRuntimeGateway(IPackageRuntimeClient client) : IBuilderRuntimeGateway
{
    public ValueTask<BuilderRuntimeResponse> InvokeAsync(
        BuilderRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (BuilderRuntimePayloadLimits.GetSerializedSize(request)
            >= BuilderRuntimePayloadLimits.MaximumRequestBytes)
        {
            throw new InvalidOperationException("Package Builder Runtime request exceeds the host transport limit.");
        }
        return client.InvokeAsync(BuilderRuntimeOperations.Execute, request, cancellationToken);
    }
}

internal sealed class BuilderLocalRuntimeGateway(IAgentWorkspaceExecutionResolver resolver)
    : IBuilderRuntimeGateway
{
    public ValueTask<BuilderRuntimeResponse> InvokeAsync(
        BuilderRuntimeRequest request,
        CancellationToken cancellationToken = default)
        => BuilderRuntimeExecutor.ExecuteAsync(resolver, request, cancellationToken);
}

internal sealed class BuilderRuntimeHandler : IPackageRuntimeOperationHandler<BuilderRuntimeRequest, BuilderRuntimeResponse>
{
    private readonly IPackageExtensionInvocationCatalog _invocations;

    public BuilderRuntimeHandler(IPackageExtensionCatalog extensionCatalog)
    {
        _invocations = extensionCatalog as IPackageExtensionInvocationCatalog
            ?? throw new InvalidOperationException(
                "The host extension catalog does not support activation-scoped invocation leases.");
    }

    public async ValueTask<BuilderRuntimeResponse> HandleAsync(
        BuilderRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        var reference = _invocations
            .GetExtensionReferences(PackageExtensionPoints.WorkspaceExecutionResolvers)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Agent workspace execution service is unavailable.");
        if (!reference.TryAcquire(out var lease))
        {
            throw new InvalidOperationException("Agent workspace execution service is unavailable.");
        }

        BuilderRuntimeResponse response;
        using (lease)
        using (var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   lease.RetirementToken))
        {
            try
            {
                response = await BuilderRuntimeExecutor
                    .ExecuteAsync(lease.Contribution, request, invocation.Token)
                    .ConfigureAwait(false);
                if (lease.RetirementToken.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException(
                        $"Package '{lease.PackageId}' became unavailable during workspace execution.");
                }
            }
            catch (OperationCanceledException exception) when (
                lease.RetirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Package '{lease.PackageId}' became unavailable during workspace execution.",
                    exception);
            }
        }
        if (BuilderRuntimePayloadLimits.GetSerializedSize(response)
            >= BuilderRuntimePayloadLimits.MaximumResponseBytes - BuilderRuntimePayloadLimits.ResponseSafetyBytes)
        {
            throw new InvalidOperationException("Package Builder Runtime response exceeds the host transport limit.");
        }
        return response;
    }
}

internal static class BuilderRuntimeExecutor
{
    private const int MaximumWorkspaces = 100;
    private const int MaximumWorkspacePaths = 64;
    private const int MaximumOutputCharacters = 512 * 1024;

    internal static async ValueTask<BuilderRuntimeResponse> ExecuteAsync(
        IAgentWorkspaceExecutionResolver resolver,
        BuilderRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Kind == BuilderRuntimeOperationKind.ListWorkspaces)
        {
            return new BuilderRuntimeResponse(Workspaces: ProjectWorkspaceList(resolver.ListWorkspaces()));
        }

        var workspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId)
            ? throw new InvalidOperationException("Workspace id is required.")
            : request.WorkspaceId;
        var resolution = await resolver.ResolveAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var context = new AgentExecutionTargetContext(
            null,
            null,
            resolution.Workspace,
            resolution.Binding);
        var targetReference = resolution.ExecutionTargetReference
            ?? new CompatibilityTargetReference(resolution.ExecutionTarget);
        if (!targetReference.TryAcquire(out var targetLease))
        {
            throw new InvalidOperationException("The selected execution-target package is unavailable.");
        }

        using (targetLease)
        {
            using var targetInvocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                targetLease.RetirementToken);
            var target = targetLease.Contribution;
            var invocationToken = targetInvocation.Token;
            BuilderRuntimeResponse response;
            try
            {
                switch (request.Kind)
                {
                    case BuilderRuntimeOperationKind.ResolveWorkspace:
                        var shell = await target
                            .GetShellAsync(context, invocationToken)
                            .ConfigureAwait(false);
                        response = new BuilderRuntimeResponse(Execution: new BuilderWorkspaceExecutionProjection(
                            ProjectWorkspace(resolution.Workspace),
                            resolution.Binding,
                            resolution.Target,
                            resolution.Scope,
                            shell,
                            target is IAgentExecutionPathMapper,
                            target is IAgentExecutionPathEnvironment));
                        break;
                    case BuilderRuntimeOperationKind.ExecuteProcess:
                        var processResult = target is IAgentProcessExecutionTarget processTarget
                            ? await processTarget.ExecuteProcessAsync(
                                context,
                                new AgentProcessCommandRequest(
                                    Require(request.FileName, "Process file name"),
                                    request.Arguments ?? [],
                                    request.WorkingDirectory,
                                    request.TimeoutSeconds),
                                invocationToken).ConfigureAwait(false)
                            : await target.ExecuteShellAsync(
                                context,
                                new AgentShellCommandRequest(
                                    BuildShellCommand(
                                        Require(request.FileName, "Process file name"),
                                        request.Arguments ?? [],
                                        await target.GetShellAsync(context, invocationToken)
                                            .ConfigureAwait(false)),
                                    request.WorkingDirectory,
                                    request.TimeoutSeconds),
                                invocationToken).ConfigureAwait(false);
                        response = new BuilderRuntimeResponse(Process: ProjectProcessResult(processResult));
                        break;
                    case BuilderRuntimeOperationKind.ExecuteShell:
                        var shellResult = await target.ExecuteShellAsync(
                            context,
                            new AgentShellCommandRequest(
                                Require(request.Command, "Shell command"),
                                request.WorkingDirectory,
                                request.TimeoutSeconds),
                            invocationToken).ConfigureAwait(false);
                        response = new BuilderRuntimeResponse(Process: ProjectProcessResult(shellResult));
                        break;
                    case BuilderRuntimeOperationKind.MapToHostPath:
                        if (target is not IAgentExecutionPathMapper mapper)
                        {
                            throw new NotSupportedException("The selected execution target does not map execution paths to host paths.");
                        }
                        response = new BuilderRuntimeResponse(PathMapping: await mapper.MapToHostPathAsync(
                            context,
                            Require(request.ExecutionPath, "Execution path"),
                            invocationToken).ConfigureAwait(false));
                        break;
                    case BuilderRuntimeOperationKind.AddPathEntry:
                        if (target is IAgentExecutionPathEnvironment pathEnvironment)
                        {
                            await pathEnvironment.AddPathEntryAsync(
                                context,
                                Require(request.ExecutionPath, "Execution path"),
                                invocationToken).ConfigureAwait(false);
                        }
                        response = new BuilderRuntimeResponse();
                        break;
                    default:
                        throw new InvalidOperationException("Unknown Package Builder Runtime operation.");
                }
            }
            catch (OperationCanceledException exception) when (
                targetLease.RetirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Package '{targetLease.PackageId}' became unavailable during execution-target invocation.",
                    exception);
            }

            if (targetLease.RetirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Package '{targetLease.PackageId}' became unavailable during execution-target invocation.");
            }

            return response;
        }
    }

    private static AgentWorkspaceRecord ProjectWorkspace(AgentWorkspaceRecord workspace)
        => workspace with
        {
            DisplayName = Truncate(workspace.DisplayName, 256),
            Description = TruncateNullable(workspace.Description, 1024),
            Paths = workspace.Paths
                .OrderBy(path => path.SortOrder)
                .Take(MaximumWorkspacePaths)
                .Select(path => path with { HostPath = Truncate(path.HostPath, 2048) })
                .ToArray(),
            Documents = [],
        };

    private static IReadOnlyList<AgentWorkspaceRecord> ProjectWorkspaceList(
        IReadOnlyList<AgentWorkspaceRecord> workspaces)
    {
        var projected = new List<AgentWorkspaceRecord>(Math.Min(workspaces.Count, MaximumWorkspaces));
        foreach (var workspace in workspaces.Take(MaximumWorkspaces))
        {
            projected.Add(ProjectWorkspace(workspace));
            var response = new BuilderRuntimeResponse(Workspaces: projected);
            if (BuilderRuntimePayloadLimits.GetSerializedSize(response)
                >= BuilderRuntimePayloadLimits.MaximumResponseBytes - BuilderRuntimePayloadLimits.ResponseSafetyBytes)
            {
                projected.RemoveAt(projected.Count - 1);
                break;
            }
        }
        return projected;
    }

    private static BuilderProcessResult ProjectProcessResult(AgentShellCommandResult result)
    {
        var output = Truncate(result.Output, MaximumOutputCharacters);
        return new BuilderProcessResult(
            result.ExitCode,
            output,
            string.Empty,
            result.WasTruncated || output.Length != result.Output.Length);
    }

    private static string BuildShellCommand(
        string fileName,
        IReadOnlyList<string> arguments,
        AgentExecutionShellDescriptor shell)
        => string.Join(' ', new[]
        {
            shell.SyntaxKind is AgentShellSyntaxKinds.PowerShell or AgentShellSyntaxKinds.Cmd
                ? BuilderWorkspaceExecution.QuoteWindows(fileName)
                : BuilderWorkspaceExecution.QuotePosix(fileName),
        }.Concat(arguments.Select(argument =>
            shell.SyntaxKind is AgentShellSyntaxKinds.PowerShell or AgentShellSyntaxKinds.Cmd
                ? BuilderWorkspaceExecution.QuoteWindows(argument)
                : BuilderWorkspaceExecution.QuotePosix(argument))));

    private static string Require(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is required.")
            : value;

    private static string Truncate(string value, int maximumCharacters)
        => value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static string? TruncateNullable(string? value, int maximumCharacters)
        => value is null ? null : Truncate(value, maximumCharacters);

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
                return "sunder.package.agent.builder.compatibility";
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

internal sealed class BuilderRuntimeExecutionTargetProxy(
    IBuilderRuntimeGateway runtime,
    BuilderWorkspaceExecutionProjection projection)
    : IAgentProcessExecutionTarget, IAgentExecutionPathMapper, IAgentExecutionPathEnvironment
{
    public AgentExecutionTargetDescriptor Descriptor => projection.Target;

    public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AgentExecutionTargetReadiness(
            Descriptor.TargetKind,
            Descriptor.TargetId,
            AgentExecutionTargetReadinessStatus.Ready,
            "Execution target was resolved by Runtime."));

    public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(projection.Shell);

    public async ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
        AgentExecutionTargetContext context,
        AgentProcessCommandRequest request,
        CancellationToken cancellationToken = default)
        => ToShellResult((await runtime.InvokeAsync(new BuilderRuntimeRequest(
            BuilderRuntimeOperationKind.ExecuteProcess,
            projection.Workspace.WorkspaceId,
            request.FileName,
            request.Arguments,
            WorkingDirectory: request.WorkingDirectory,
            TimeoutSeconds: request.TimeoutSeconds ?? 600), cancellationToken).ConfigureAwait(false)).Process);

    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken = default)
        => ToShellResult((await runtime.InvokeAsync(new BuilderRuntimeRequest(
            BuilderRuntimeOperationKind.ExecuteShell,
            projection.Workspace.WorkspaceId,
            Command: request.Command,
            WorkingDirectory: request.WorkingDirectory,
            TimeoutSeconds: request.TimeoutSeconds ?? 600), cancellationToken).ConfigureAwait(false)).Process);

    public async ValueTask<AgentExecutionPathMapping> MapToHostPathAsync(
        AgentExecutionTargetContext context,
        string executionPath,
        CancellationToken cancellationToken = default)
        => (await runtime.InvokeAsync(new BuilderRuntimeRequest(
            BuilderRuntimeOperationKind.MapToHostPath,
            projection.Workspace.WorkspaceId,
            ExecutionPath: executionPath), cancellationToken).ConfigureAwait(false)).PathMapping
           ?? throw new InvalidDataException("Runtime did not return a path mapping.");

    public ValueTask<IReadOnlyList<string>> ListPathEntriesAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<string>>([]);

    public async ValueTask AddPathEntryAsync(
        AgentExecutionTargetContext context,
        string executionPath,
        CancellationToken cancellationToken = default)
        => _ = await runtime.InvokeAsync(new BuilderRuntimeRequest(
            BuilderRuntimeOperationKind.AddPathEntry,
            projection.Workspace.WorkspaceId,
            ExecutionPath: executionPath), cancellationToken).ConfigureAwait(false);

    public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Package Builder does not expose file operations through its Runtime proxy.");

    public ValueTask<AgentFileReadResult> ReadFileAsync(
        AgentExecutionTargetContext context,
        AgentFileReadRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Package Builder does not expose file operations through its Runtime proxy.");

    public ValueTask<AgentFileMutationResult> WriteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Package Builder does not expose file operations through its Runtime proxy.");

    public ValueTask<AgentFileMutationResult> DeleteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Package Builder does not expose file operations through its Runtime proxy.");

    private static AgentShellCommandResult ToShellResult(BuilderProcessResult? result)
        => result is null
            ? throw new InvalidDataException("Runtime did not return a process result.")
            : new AgentShellCommandResult(
                result.ExitCode,
                result.CombinedOutput,
                WasTruncated: result.WasTruncated);
}
