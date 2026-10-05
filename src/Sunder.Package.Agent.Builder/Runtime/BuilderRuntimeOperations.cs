namespace Sunder.Package.Agent.Builder;

internal interface IBuilderRuntimeOperations
{
    ValueTask<BuilderRpcResponse> ExecuteAsync(BuilderRpcRequest request, DateTimeOffset deadline, CancellationToken token);
}

internal interface IBuilderRuntimeLifetime : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken token = default);
}

internal sealed class BuilderRpcDomainException(string code, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    internal string Code { get; } = code;
}
