namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Represents a provider failure with separate diagnostic and user-facing content.
/// </summary>
/// <remarks>
/// The runtime can persist <see cref="Content" /> in the transcript and can log this exception,
/// including its inner exception. Providers must redact credentials, authorization headers, and
/// secret-bearing vendor payloads from every supplied value.
/// </remarks>
/// <param name="message">The concise diagnostic message used by standard exception handling and logs.</param>
/// <param name="content">The safe user-facing failure content; Markdown formatting is supported.</param>
/// <param name="errorCode">An optional stable machine-readable code used for classification and retry decisions.</param>
/// <param name="innerException">The underlying failure, with any secret-bearing data already removed.</param>
public sealed class AgentChatProviderException(
    string message,
    string content,
    string? errorCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>Gets the user-facing failure content that can be written to the agent transcript.</summary>
    public string Content { get; } = content;

    /// <summary>Gets the stable provider error code, or <see langword="null" /> when no code is available.</summary>
    public string? ErrorCode { get; } = errorCode;
}
