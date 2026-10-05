using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal sealed class FileExecutionReference(Func<CancellationToken, ValueTask<FileExecutionLease?>> acquire)
{
    internal ValueTask<FileExecutionLease?> TryAcquireAsync(CancellationToken token) => acquire(token);
}

internal sealed class FileExecutionLease(
    IAgentExecutionTarget service,
    CancellationToken retirementToken,
    Func<CancellationToken, ValueTask<AgentExecutionTargetDescriptor>> describe,
    Func<CancellationToken, ValueTask<bool>> isCurrent,
    Func<ValueTask> dispose) : IAsyncDisposable
{
    private readonly object _gate = new();
    private Task? _dispose;
    internal IAgentExecutionTarget Service => service;
    internal CancellationToken RetirementToken => retirementToken;
    internal ValueTask<AgentExecutionTargetDescriptor> DescribeAsync(CancellationToken token) => describe(token);
    internal ValueTask<bool> IsCurrentAsync(CancellationToken token) => isCurrent(token);
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_dispose ??= dispose().AsTask());
    }
}

internal interface IFilesRuntimeBindings : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken token = default);
}
