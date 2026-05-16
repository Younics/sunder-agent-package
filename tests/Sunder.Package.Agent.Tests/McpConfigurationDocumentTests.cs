using System.Text.Json;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Mcp.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class McpConfigurationDocumentTests
{
    [Fact]
    public void CreateLocalTemplate_DoesNotConfigureTimeoutsByDefault()
    {
        using var document = JsonDocument.Parse(McpConfigurationDocument.CreateLocalTemplate());

        Assert.False(document.RootElement.TryGetProperty("timeout", out _));
        Assert.False(document.RootElement.TryGetProperty("discoveryTimeout", out _));
        Assert.False(document.RootElement.TryGetProperty("toolTimeout", out _));
    }

    [Fact]
    public void Parse_StoresSeparateDiscoveryAndToolTimeouts()
    {
        var parsed = McpConfigurationDocument.Parse(
            "server-1",
            "unity",
            """
            {
              "type": "local",
              "enabled": true,
              "command": ["relay.exe", "--mcp"],
              "discoveryTimeout": 120000,
              "toolTimeout": 300000
            }
            """);

        Assert.Null(parsed.Server.TimeoutMilliseconds);
        Assert.Equal(120000, parsed.Server.DiscoveryTimeoutMilliseconds);
        Assert.Equal(300000, parsed.Server.ToolTimeoutMilliseconds);
        Assert.Equal(120000, McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(parsed.Server));
        Assert.Equal(300000, McpTimeoutResolver.ResolveToolTimeoutMilliseconds(parsed.Server));
    }

    [Fact]
    public void Parse_MapsLegacyTimeoutToDiscoveryAndToolTimeouts()
    {
        var parsed = McpConfigurationDocument.Parse(
            "server-1",
            "legacy",
            """
            {
              "type": "remote",
              "enabled": true,
              "url": "https://example.com/mcp",
              "timeout": 45000
            }
            """);

        Assert.Equal(45000, parsed.Server.TimeoutMilliseconds);
        Assert.Equal(45000, parsed.Server.DiscoveryTimeoutMilliseconds);
        Assert.Equal(45000, parsed.Server.ToolTimeoutMilliseconds);
    }

    [Fact]
    public void TimeoutResolver_DefaultsToNoTimeout()
    {
        var server = new ConfiguredMcpServerRecord();

        Assert.Null(McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server));
        Assert.Null(McpTimeoutResolver.ResolveToolTimeoutMilliseconds(server));
        Assert.Null(McpTimeoutResolver.ResolveEffectiveTimeoutMilliseconds(null));
    }
}
