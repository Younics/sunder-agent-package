using Sunder.Sdk.Rpc;
using Wire = Sunder.Package.Agent.Builder.Rpc;

namespace Sunder.Package.Agent.Builder;

[SunderRpcService(SunderRpcExposure.Package,
    ContractId = "sunder.package.agent.builder.runtime", ServiceId = "builder-runtime", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "builder.runtime", Audience = SunderRpcProviderAudience.Owner)]
[SunderRpcDomainError("agent.builder.execution-target-unavailable", Description = "The selected execution-target activation became unavailable.")]
[SunderRpcDomainError("agent.builder.invalid-request", Description = "The Builder request is incomplete or invalid.")]
[SunderRpcDomainError("agent.builder.request-too-large", Description = "The Builder request exceeds its package-owned payload limit.")]
[SunderRpcDomainError("agent.builder.response-too-large", Description = "The Builder response exceeds its package-owned content limit.")]
[SunderRpcDomainError("agent.builder.unsupported-operation", Description = "The selected execution target does not implement the requested Builder operation.")]
[SunderRpcDomainError("agent.builder.workspace-execution-unavailable", Description = "The Agent workspace execution resolver is unavailable.")]
internal sealed partial class BuilderRuntimeService(IBuilderRuntimeOperations operations)
{
    [SunderRpcMethod(Errors = [
        "agent.builder.execution-target-unavailable", "agent.builder.invalid-request",
        "agent.builder.request-too-large", "agent.builder.response-too-large",
        "agent.builder.unsupported-operation", "agent.builder.workspace-execution-unavailable"])]
    public async ValueTask<Wire.BuilderResultContent> ExecuteAsync(
        Wire.BuilderExecuteRequest request,
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        var domainRequest = request.ToDomain();
        if (BuilderRpcPayloadLimits.GetSerializedSize(domainRequest)
            >= BuilderRpcPayloadLimits.MaximumRequestBytes)
        {
            throw Domain("agent.builder.request-too-large",
                "Package Builder RPC request exceeds the host transport limit.");
        }

        BuilderRpcResponse response;
        try
        {
            response = await operations.ExecuteAsync(domainRequest, context.DeadlineUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (BuilderRpcDomainException exception) { throw Domain(exception.Code, exception.Message); }

        byte[] bytes;
        try { bytes = BuilderRpcPayloadLimits.SerializeResponse(response); }
        catch (InvalidDataException exception) { throw Domain("agent.builder.response-too-large", exception.Message); }

        await using var stream = new MemoryStream(bytes, writable: false);
        var content = await context.RegisterContentAsync(stream,
            new SunderRpcContentRegistrationOptions("application/json", "builder-result.json", bytes.LongLength,
                Repeatability: SunderRpcContentRepeatability.SingleUse, MaximumUses: 1), cancellationToken).ConfigureAwait(false);
        BuilderRpcPayloadLimits.ValidateRegisteredContent(content, bytes, context.DeadlineUtc);
        return new Wire.BuilderResultContent { Content = new Wire.ContentReference { Value = content } };
    }

    private static SunderRpcException Domain(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.Domain, code, message));
}
