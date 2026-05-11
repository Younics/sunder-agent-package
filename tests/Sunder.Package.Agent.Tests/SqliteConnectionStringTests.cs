using System.Reflection;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SqliteConnectionStringTests
{
    [Fact]
    public void AgentLocalStore_DisablesSqliteConnectionPooling()
    {
        var connectionString = InvokeCreateConnectionString(typeof(AgentLocalStore));

        Assert.Contains("Pooling=False", connectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MemoryLocalStore_DisablesSqliteConnectionPooling()
    {
        var connectionString = InvokeCreateConnectionString(typeof(MemoryLocalStore));

        Assert.Contains("Pooling=False", connectionString, StringComparison.OrdinalIgnoreCase);
    }

    private static string InvokeCreateConnectionString(Type storeType)
    {
        var method = storeType.GetMethod("CreateConnectionString", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsType<string>(method.Invoke(null, [Path.Combine(Path.GetTempPath(), "agent.db")]));
    }
}
