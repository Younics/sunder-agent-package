using System.Text.Json;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Mcp.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class McpConfigurationDocumentTests
{
    [Fact]
    public void CreateTemplates_IncludeOptionalTimeoutExamples()
    {
        using var localDocument = JsonDocument.Parse(McpConfigurationDocument.CreateLocalTemplate());
        using var remoteDocument = JsonDocument.Parse(McpConfigurationDocument.CreateRemoteTemplate());

        Assert.False(localDocument.RootElement.TryGetProperty("timeout", out _));
        Assert.Equal(5000, localDocument.RootElement.GetProperty("discoveryTimeout").GetInt32());
        Assert.Equal(180000, localDocument.RootElement.GetProperty("toolTimeout").GetInt32());
        Assert.False(remoteDocument.RootElement.TryGetProperty("timeout", out _));
        Assert.Equal(5000, remoteDocument.RootElement.GetProperty("discoveryTimeout").GetInt32());
        Assert.Equal(180000, remoteDocument.RootElement.GetProperty("toolTimeout").GetInt32());
    }

    [Fact]
    public void CreateLocalTemplate_IncludesGenericEnvPlaceholder()
    {
        var template = McpConfigurationDocument.CreateLocalTemplate();
        using var document = JsonDocument.Parse(template);

        var env = document.RootElement.GetProperty("env");
        Assert.Equal(JsonValueKind.Object, env.ValueKind);
        Assert.Equal("<insert-your-api-key-here>", env.GetProperty("MY_API_KEY").GetString());
        Assert.False(document.RootElement.TryGetProperty("environment", out _));
        Assert.Contains("<insert-your-api-key-here>", template, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u003C", template, StringComparison.OrdinalIgnoreCase);
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
    public void Parse_StoresLocalEnvVariables()
    {
        var parsed = McpConfigurationDocument.Parse(
            "server-1",
            "elevenlabs",
            """
            {
              "type": "local",
              "enabled": true,
              "command": ["uvx", "elevenlabs-mcp"],
              "env": {
                "ELEVENLABS_API_KEY": "test-api-key"
              }
            }
            """);

        Assert.Contains(parsed.Server.EnvironmentVariableNames, name => name == "ELEVENLABS_API_KEY");
        Assert.Equal("test-api-key", parsed.EnvironmentVariables["ELEVENLABS_API_KEY"]);
        Assert.Null(parsed.Server.TimeoutMilliseconds);
        Assert.Null(parsed.Server.DiscoveryTimeoutMilliseconds);
        Assert.Null(parsed.Server.ToolTimeoutMilliseconds);
    }

    [Fact]
    public void BuildEditorText_EmitsEnvForLocalServers()
    {
        var parsed = McpConfigurationDocument.Parse(
            "server-1",
            "local_mcp",
            """
            {
              "type": "local",
              "enabled": true,
              "command": ["uvx", "example-mcp"],
              "env": {
                "MY_API_KEY": "<insert-your-api-key-here>"
              }
            }
            """);

        var editorText = McpConfigurationDocument.BuildEditorText(parsed.Server, parsed.Headers, parsed.EnvironmentVariables);
        using var document = JsonDocument.Parse(editorText);

        Assert.Equal("<insert-your-api-key-here>", document.RootElement.GetProperty("env").GetProperty("MY_API_KEY").GetString());
        Assert.False(document.RootElement.TryGetProperty("environment", out _));
        Assert.Contains("<insert-your-api-key-here>", editorText, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u003C", editorText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_StillReadsLegacyLocalEnvironmentVariables()
    {
        var parsed = McpConfigurationDocument.Parse(
            "server-1",
            "legacy_env",
            """
            {
              "type": "local",
              "enabled": true,
              "command": ["uvx", "example-mcp"],
              "environment": {
                "MY_API_KEY": "legacy-api-key"
              }
            }
            """);

        Assert.Contains(parsed.Server.EnvironmentVariableNames, name => name == "MY_API_KEY");
        Assert.Equal("legacy-api-key", parsed.EnvironmentVariables["MY_API_KEY"]);
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
    public void Parse_RemoteOAuthRoundTrips()
    {
        var parsed = McpConfigurationDocument.Parse(
            "server-1",
            "higgsfield",
            """
            {
              "type": "remote",
              "enabled": true,
              "url": "https://mcp.higgsfield.ai/mcp",
              "oauth": {
                "enabled": true,
                "scopes": ["openid", "email", "offline_access"],
                "clientId": "sunder-test"
              }
            }
            """);

        Assert.True(parsed.Server.OAuthEnabled);
        Assert.Equal(["openid", "email", "offline_access"], parsed.Server.OAuthScopes);
        Assert.Equal("sunder-test", parsed.Server.OAuthClientId);

        var editorText = McpConfigurationDocument.BuildEditorText(parsed.Server, parsed.Headers, parsed.EnvironmentVariables);
        using var document = JsonDocument.Parse(editorText);
        var oauth = document.RootElement.GetProperty("oauth");
        Assert.True(oauth.GetProperty("enabled").GetBoolean());
        Assert.Equal("openid", oauth.GetProperty("scopes")[0].GetString());
        Assert.Equal("sunder-test", oauth.GetProperty("clientId").GetString());
    }

    [Fact]
    public void CommandResolver_ResolvesBareCommandFromPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-mcp-command-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var commandPath = Path.Combine(root, "npx");
            File.WriteAllText(commandPath, string.Empty);

            Assert.Equal(commandPath, McpCommandResolver.ResolveBareCommand("npx", root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TimeoutResolver_UsesFiniteDefaults()
    {
        var server = new ConfiguredMcpServerRecord();

        Assert.Equal(15_000, McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server));
        Assert.Equal(120_000, McpTimeoutResolver.ResolveToolTimeoutMilliseconds(server));
        Assert.Equal(15_000, McpTimeoutResolver.ResolveEffectiveTimeoutMilliseconds(null));
    }

    [Fact]
    public void TimeoutResolver_ClampsConfiguredTimeouts()
    {
        var server = new ConfiguredMcpServerRecord
        {
            DiscoveryTimeoutMilliseconds = int.MaxValue,
            ToolTimeoutMilliseconds = int.MaxValue,
        };

        Assert.Equal(120_000, McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server));
        Assert.Equal(1_800_000, McpTimeoutResolver.ResolveToolTimeoutMilliseconds(server));
    }

    [Fact]
    public void TransportSecurity_RejectsCredentialsOverRemoteHttp()
    {
        var server = new ConfiguredMcpServerRecord
        {
            TransportType = ConfiguredMcpTransportType.HttpSse,
            EndpointUrl = "http://mcp.example.test/events",
            HeaderNames = ["Authorization"],
        };

        var exception = Assert.Throws<InvalidOperationException>(() => McpTransportSecurity.ValidateRemoteEndpoint(server));

        Assert.Contains("must use HTTPS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransportSecurity_RejectsUrlUserInfo()
    {
        var server = new ConfiguredMcpServerRecord
        {
            TransportType = ConfiguredMcpTransportType.HttpSse,
            EndpointUrl = "https://user:password@mcp.example.test/events",
        };

        var exception = Assert.Throws<InvalidOperationException>(() => McpTransportSecurity.ValidateRemoteEndpoint(server));

        Assert.Contains("user-info credentials", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransportSecurity_AllowsCredentialedLoopbackHttp()
    {
        var server = new ConfiguredMcpServerRecord
        {
            TransportType = ConfiguredMcpTransportType.HttpSse,
            EndpointUrl = "http://127.0.0.1:3000/events",
            HeaderNames = ["Authorization"],
        };

        McpTransportSecurity.ValidateRemoteEndpoint(server);
    }
}
