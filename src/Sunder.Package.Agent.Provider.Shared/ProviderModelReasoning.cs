using Sunder.Generated.Packages.Younics_Ai_Agent_Models;

namespace Sunder.Package.Agent.Provider.Shared;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class ProviderModelReasoning
{
    internal static IReadOnlyList<AgentModelVariantDescriptor> Create(params string[] ids)
        => ids.Select(id => new AgentModelVariantDescriptor
        {
            VariantId = id,
            DisplayName = id switch { "xhigh" => "Extra high", "none" => "None", _ => char.ToUpperInvariant(id[0]) + id[1..] },
            Description = id switch
            {
                "none" => "Explicitly disable thinking where the model supports it.",
                "max" => "Use the highest supported effort. This can substantially increase latency and token cost.",
                "minimal" => "Use the model's minimal thinking setting; this does not necessarily disable thinking.",
                _ => $"Request {id} effort. Effort is a model-specific signal, not a fixed token budget.",
            },
            ReasoningEffort = id switch
            {
                "none" => AgentReasoningEffort.None,
                "low" => AgentReasoningEffort.Low,
                "medium" => AgentReasoningEffort.Medium,
                "high" => AgentReasoningEffort.High,
                "xhigh" => AgentReasoningEffort.ExtraHigh,
                _ => null,
            },
        }).ToArray();
}
