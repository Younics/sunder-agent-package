namespace Sunder.Package.Agent.Execution.Docker;

internal sealed class DockerExecutionDomainException : InvalidOperationException
{
    internal DockerExecutionDomainException(
        string code,
        string message,
        bool isTransient = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (string.IsNullOrWhiteSpace(code)
            || code.Length > 128
            || code.Any(character => !char.IsAsciiLetterOrDigit(character)
                                     && character is not ('.' or '-' or '_')))
        {
            throw new ArgumentException("Docker error codes must be bounded portable ASCII tokens.", nameof(code));
        }

        Code = code;
        IsTransient = isTransient;
    }

    internal string Code { get; }

    internal bool IsTransient { get; }
}
