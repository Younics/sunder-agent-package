using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Runtime;

internal sealed class AgentRuntimeTransportBudgetState
{
    private readonly AgentTransportProfile _workerProfile;
    private readonly bool _workerReported;
    private AgentTransportProfile _current;

    internal AgentRuntimeTransportBudgetState() : this(null)
    {
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal AgentRuntimeTransportBudgetState([System.Diagnostics.CodeAnalysis.AllowNull] ISunderRpcClient client)
    {
        if (client?.ResourceProfile is { } resourceProfile)
        {
            _workerProfile = new AgentTransportProfile(
                resourceProfile.MaximumRpcValueBytes,
                resourceProfile.MaximumRpcValueBytes,
                resourceProfile.MaximumRpcValueBytes,
                resourceProfile.MaximumContentReferenceBytes,
                RuntimeReported: true);
            _workerReported = true;
        }
        else
        {
            _workerProfile = AgentRuntimePayloadLimits.StandardFallbackProfile;
        }

        _current = _workerProfile;
    }

    internal AgentTransportProfile Current => Volatile.Read(ref _current);

    internal AgentTransportProfile Negotiate(AgentTransportProfileRequest request)
    {
        ValidateValueBytes(request.RequestValueBytes, nameof(request.RequestValueBytes));
        ValidateValueBytes(request.ResponseValueBytes, nameof(request.ResponseValueBytes));
        ValidateValueBytes(request.EventValueBytes, nameof(request.EventValueBytes));
        if (request.ContentReferenceBytes <= 0)
        {
            throw new AgentOwnerOperationException("The proposed Runtime content-reference budget is invalid.");
        }

        var negotiated = new AgentTransportProfile(
            Math.Min(request.RequestValueBytes, _workerProfile.RequestValueBytes),
            Math.Min(request.ResponseValueBytes, _workerProfile.ResponseValueBytes),
            Math.Min(request.EventValueBytes, _workerProfile.EventValueBytes),
            Math.Min(request.ContentReferenceBytes, _workerProfile.ContentReferenceBytes),
            request.RuntimeReported && _workerReported);
        Volatile.Write(ref _current, negotiated);
        return negotiated;
    }

    private static void ValidateValueBytes(int value, string name)
    {
        if (value <= 0)
        {
            throw new AgentOwnerOperationException($"The proposed Runtime value budget '{name}' is invalid.");
        }
    }
}
