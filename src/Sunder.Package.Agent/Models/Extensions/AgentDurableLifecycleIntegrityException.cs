namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>Represents a permanent durable-lifecycle identity or payload-integrity violation.</summary>
public sealed class AgentDurableLifecycleIntegrityException : InvalidOperationException
{
    /// <summary>Creates an integrity failure with a bounded diagnostic message.</summary>
    /// <param name="message">The non-sensitive integrity diagnostic.</param>
    public AgentDurableLifecycleIntegrityException(string message)
        : base(message)
    {
    }
}
