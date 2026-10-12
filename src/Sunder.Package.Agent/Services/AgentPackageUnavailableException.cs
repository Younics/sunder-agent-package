namespace Sunder.Package.Agent.Services;

// An Agent operation was interrupted by withdrawal of a required selected package.
// The structured SDK failure remains the cause for retry/failure presentation policy.
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed class AgentPackageUnavailableException(string packageId, Exception? innerException = null)
    : OperationCanceledException($"Package '{packageId}' became unavailable while the operation was running.", innerException)
{
    internal string PackageId { get; } = packageId;
}
