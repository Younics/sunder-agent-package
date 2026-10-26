using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

// The activation-owned reload snapshot is shared by the page and operation
// observations. A completed build or RPC is never an applied-output receipt.
[SunderPackageCodeRole(SunderPackageCodeRole.App)]
internal sealed record BuilderReloadProgress(string OperationId, string State, string Phase, string? Error = null);
