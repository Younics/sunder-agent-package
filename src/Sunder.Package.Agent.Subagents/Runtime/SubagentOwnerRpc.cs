using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents.Runtime;

internal static class SubagentOwnerJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static SunderRpcContentRegistrationOptions ContentOptions(string fileName, long length)
        => new(
            "application/json",
            fileName,
            length,
            Repeatability: SunderRpcContentRepeatability.SingleUse,
            MaximumUses: 1);

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

internal sealed class SubagentOwnerOperationException(
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException);
