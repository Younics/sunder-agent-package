namespace Sunder.Package.Agent.Memory.Semantic.Rpc;

public sealed partial class SessionCleanerProvider
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal SessionCleanerProvider(MemorySemanticFeature feature) : this(feature.CleanerId, feature.DeleteSessionData) { }
}
