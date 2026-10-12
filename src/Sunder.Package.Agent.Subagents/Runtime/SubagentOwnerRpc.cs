using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class SubagentOwnerJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new SunderRpcJsonConverterFactory() },
    };

    internal static bool ShouldMap(Exception exception)
        => exception is SubagentOwnerOperationException;

    internal static SunderRpcException Map(SubagentOwnerOperationException exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "The requested Subagents operation failed."
            : exception.Message;
        if (message.Length > 2_048)
        {
            message = message[..2_048];
        }
        return new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            "subagents.owner.operation-failed",
            message));
    }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed class SubagentOwnerOperationException(
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException);
