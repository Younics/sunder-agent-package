using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Shell;

internal interface IShellExecutionTarget
{
    AgentExecutionTargetDescriptor Descriptor { get; }
    ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken token);
    ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken token);
    ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken token);
}

internal sealed class ShellExecutionReference(Func<CancellationToken, ValueTask<ShellExecutionLease?>> acquire)
{
    internal ValueTask<ShellExecutionLease?> TryAcquireAsync(CancellationToken token) => acquire(token);
}

internal sealed class ShellExecutionLease(
    IShellExecutionTarget service,
    CancellationToken retirementToken,
    Func<CancellationToken, ValueTask<AgentExecutionTargetDescriptor>> describe,
    Func<CancellationToken, ValueTask<bool>> isCurrent,
    Func<ValueTask> dispose) : IAsyncDisposable
{
    private readonly object _gate = new();
    private Task? _dispose;
    internal IShellExecutionTarget Service => service;
    internal CancellationToken RetirementToken => retirementToken;
    internal ValueTask<AgentExecutionTargetDescriptor> DescribeAsync(CancellationToken token) => describe(token);
    internal ValueTask<bool> IsCurrentAsync(CancellationToken token) => isCurrent(token);
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_dispose ??= dispose().AsTask());
    }
}

internal interface IShellRuntimeBindings : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken token = default);
}
