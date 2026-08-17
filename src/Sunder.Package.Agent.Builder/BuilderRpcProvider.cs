using Sunder.Package.Agent.Builder.Protocol.Generated;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Builder;

internal sealed class BuilderRpcProvider(AgentRpcCatalog rpcCatalog) : IBuilderRuntimeProvider
{
    public async ValueTask<BuilderResultContent> ExecuteAsync(
        SunderRpcInvocationContext context,
        BuilderExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        var domainRequest = BuilderRpcWireMapper.ToDomain(request);
        if (BuilderRpcPayloadLimits.GetSerializedSize(domainRequest)
            >= BuilderRpcPayloadLimits.MaximumRequestBytes)
        {
            throw Domain(
                "agent.builder.request-too-large",
                "Package Builder RPC request exceeds the host transport limit.");
        }

        var reference = rpcCatalog
            .GetServiceReferences(AgentRpcServices.WorkspaceExecutionResolvers)
            .FirstOrDefault();
        if (reference is null || !reference.TryAcquire(out var lease))
        {
            throw WorkspaceExecutionUnavailable();
        }

        BuilderRpcResponse response;
        using (lease)
        using (var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   lease.RetirementToken))
        {
            try
            {
                response = await BuilderRpcExecutor
                    .ExecuteAsync(lease.Service, domainRequest, invocation.Token)
                    .ConfigureAwait(false);
                if (lease.RetirementToken.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    throw WorkspaceExecutionUnavailable(
                        $"Package '{lease.PackageId}' became unavailable during workspace execution.");
                }
            }
            catch (OperationCanceledException) when (
                lease.RetirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw WorkspaceExecutionUnavailable(
                    $"Package '{lease.PackageId}' became unavailable during workspace execution.");
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                var domainException = (BuilderRpcDomainException)exception;
                throw Domain(domainException.Code, domainException.Message);
            }
        }

        byte[] bytes;
        try
        {
            bytes = BuilderRpcPayloadLimits.SerializeResponse(response);
        }
        catch (InvalidDataException exception)
        {
            throw Domain("agent.builder.response-too-large", exception.Message);
        }

        await using var stream = new MemoryStream(bytes, writable: false);
        var content = await context.RegisterContentAsync(
            stream,
            new SunderRpcContentRegistrationOptions(
                "application/json",
                "builder-result.json",
                bytes.LongLength,
                Repeatability: SunderRpcContentRepeatability.SingleUse,
                MaximumUses: 1),
            cancellationToken).ConfigureAwait(false);
        BuilderRpcPayloadLimits.ValidateRegisteredContent(content, bytes, context.DeadlineUtc);
        return new BuilderResultContent { Content = content };
    }

    private SunderRpcException WorkspaceExecutionUnavailable(string? message = null)
        => Domain(
            "agent.builder.workspace-execution-unavailable",
            message
            ?? rpcCatalog.AvailabilityError?.Message
            ?? "Agent workspace execution service is unavailable.");

    internal static bool ShouldMap(Exception exception)
        => exception is BuilderRpcDomainException;

    private static SunderRpcException Domain(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.Domain, code, message));
}

internal sealed class BuilderRpcDomainException(string code, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    internal string Code { get; } = code;
}
