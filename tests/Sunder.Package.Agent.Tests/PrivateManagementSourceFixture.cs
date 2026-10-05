using System.Reflection;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// The all-family source lane builds the canonical projects. These component
// checks consume their isolated roles and never reconstruct a transport project.
internal sealed class PrivateManagementSourceFixture : IDisposable
{
    private readonly CanonicalPackageRoles _roles = new();
    private readonly Dictionary<string, byte[]> _evidence = new(StringComparer.Ordinal);

    internal Assembly Worker { get; private set; } = null!;
    internal Assembly App { get; private set; } = null!;
    internal SunderRpcContractDescriptor Descriptor { get; private set; } = null!;

    internal static PrivateManagementSourceFixture Load(string family, SunderRpcContractDescriptor oracle)
    {
        var fixture = new PrivateManagementSourceFixture();
        try
        {
            fixture.LoadCore(family, oracle);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private void LoadCore(string family, SunderRpcContractDescriptor oracle)
    {
        var root = CanonicalPackageRoles.Source(family);
        foreach (var name in new[] { "sunder-contracts.owned.json", "sunder-contracts.lock.json" })
        {
            var path = Path.Combine(root, name);
            _evidence.Add(path, File.ReadAllBytes(path));
        }
        Descriptor = CanonicalPackageRoles.OwnedDescriptor(root, oracle.ContractId);
        Assert.Equal(oracle.GetCanonicalUtf8(), Descriptor.GetCanonicalUtf8());
        Assert.Equal(oracle.AbiSha256, Descriptor.AbiSha256);
        Worker = _roles.Load(family, "worker");
        App = _roles.Load(family, "app");
        Assert.DoesNotContain(App.GetReferencedAssemblies(), reference => reference.Name == Worker.GetName().Name || reference.Name == "Sunder.Sdk.Worker");

        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CanonicalPackageRoles.Analysis(root), "authoring-receipt.v1.json")));
        var owned = Assert.Single(receipt.RootElement.GetProperty("contracts").EnumerateArray(),
            contract => contract.GetProperty("contractId").GetString() == oracle.ContractId);
        Assert.Equal(oracle.GetCanonicalUtf8(), Encoding.UTF8.GetBytes(owned.GetProperty("canonicalDescriptor").GetString()!));
        Assert.Single(receipt.RootElement.GetProperty("providers").EnumerateArray(),
            provider => provider.GetProperty("contractKey").GetString() == owned.GetProperty("contractKey").GetString());

        using var appReceipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CanonicalPackageRoles.Analysis(root), "app-authoring-receipt.v1.json")));
        Assert.Equal("app", appReceipt.RootElement.GetProperty("role").GetString());
        Assert.Empty(appReceipt.RootElement.GetProperty("providers").EnumerateArray());
        var use = Assert.Single(appReceipt.RootElement.GetProperty("uses").EnumerateArray());
        Assert.True(use.GetProperty("required").GetBoolean());
        var contract = Assert.Single(appReceipt.RootElement.GetProperty("contracts").EnumerateArray());
        Assert.Equal(contract.GetProperty("contractKey").GetString(), use.GetProperty("contractKey").GetString());
        Assert.Equal(oracle.ContractId, contract.GetProperty("contractId").GetString());
        Assert.Equal(oracle.GetCanonicalUtf8(), Encoding.UTF8.GetBytes(contract.GetProperty("canonicalDescriptor").GetString()!));
        var expectedActions = family.StartsWith("Execution.", StringComparison.Ordinal)
            ? new[] { "discover", "invoke" } : new[] { "discover", "invoke", "subscribe" };
        Assert.Equal(expectedActions, use.GetProperty("actions").EnumerateArray().Select(value => value.GetString()));
    }

    public void Dispose()
    {
        _roles.Dispose();
        foreach (var (path, bytes) in _evidence) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
