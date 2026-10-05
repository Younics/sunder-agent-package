using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tests;

internal sealed partial class RegressionTestRpcState
{
    private void ValidateProviderOutput(Entry entry, string serviceId, string methodId, JsonElement payload)
    {
        try { ValidatePayload(entry, serviceId, methodId, payload, output: true); }
        catch (Exception exception)
        {
            Volatile.Write(ref _lastInvocationFailure, exception);
            var projected = ProjectProviderFailure(entry, serviceId, methodId, exception);
            if (ReferenceEquals(projected, exception)) throw;
            throw projected;
        }
    }

    private Exception ProjectProviderFailure(
        Entry entry,
        string serviceId,
        string methodId,
        Exception exception)
    {
        if (!_workerStyleProviderBoundary
            || exception is SunderRpcException
            {
                IsHostAuthenticated: false,
                Error.Kind: SunderRpcErrorKind.Domain,
            } rpcException
            && entry.Descriptor.FindService(serviceId)?.FindMethod(methodId)?.Errors.Contains(
                rpcException.Error.Code,
                StringComparer.Ordinal) == true)
        {
            return exception;
        }

        return new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.ProviderFaulted,
            "rpc.provider.handler-fault",
            "The process provider handler failed."));
    }

    private static void ValidatePayload(
        Entry entry,
        string serviceId,
        string methodId,
        JsonElement payload,
        bool output)
    {
        var method = entry.Descriptor.FindService(serviceId)?.FindMethod(methodId);
        var schemaReference = output ? method?.OutputSchemaReference : method?.RequestSchemaReference;
        string? error = null;
        if (schemaReference is null
            || !entry.Descriptor.IsValid(schemaReference, payload, out error))
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.Validation,
                "test.rpc.schema-validation",
                $"The RPC {(output ? "output" : "request")} does not match "
                + $"'{entry.Descriptor.ContractId}/{serviceId}/{methodId}': {error ?? "method not found"}"));
        }
    }
}
