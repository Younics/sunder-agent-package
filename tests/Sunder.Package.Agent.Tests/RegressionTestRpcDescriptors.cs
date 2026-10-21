using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tests;

// Shared loopback utilities. Repository discovery has no artifact/history input.
internal static class RegressionTestRpcDescriptors
{
    internal static string RepositoryRoot { get; } = FindRepositoryRoot();

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> ErrorMap(SunderRpcContractDescriptor descriptor)
        => descriptor.Services.ToDictionary(service => service.ServiceId,
            service => (IReadOnlyDictionary<string, IReadOnlyList<string>>)service.Methods.ToDictionary(
                method => method.MethodId, method => method.Errors, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.GetEnvironmentVariable("SUNDER_AGENT_REPOSITORY_ROOT"), AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Sunder.AgentPackage.slnx"))
                    && File.Exists(Path.Combine(directory.FullName, "packages.json"))) return directory.FullName;
        }
        throw new InvalidOperationException("Could not locate the Agent repository from SUNDER_AGENT_REPOSITORY_ROOT, test output, or working directory.");
    }
}
