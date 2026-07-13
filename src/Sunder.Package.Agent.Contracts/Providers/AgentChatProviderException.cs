namespace Sunder.Package.Agent.Contracts.Models;

public sealed class AgentChatProviderException(
    string message,
    string content,
    string? errorCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Content { get; } = content;

    public string? ErrorCode { get; } = errorCode;
}
