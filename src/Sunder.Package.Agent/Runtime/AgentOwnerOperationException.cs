namespace Sunder.Package.Agent.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed class AgentOwnerOperationException(
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException);
