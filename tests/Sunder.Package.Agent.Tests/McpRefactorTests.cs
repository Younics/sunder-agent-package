extern alias McpWorker;
extern alias McpApp;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using McpWorker::Sunder.Package.Agent.Contracts.Models;
using McpWorker::Sunder.Package.Agent.Mcp;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using McpWorker::Sunder.Package.Agent.Mcp.Services;
using AgentMcpSettingsViewModel = McpApp::Sunder.Package.Agent.Mcp.AgentMcpSettingsViewModel;
using McpAppRuntimeGateway = McpApp::Sunder.Package.Agent.Mcp.Runtime.McpAppRuntimeGateway;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class McpRefactorTests
{
    [Fact]
    public async Task OAuthCallbackHandler_RejectsUnknownServerBeforeStartingProviderFlow()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await using var oauth = new McpOAuthService(context);
        var handler = new McpOAuthCallbackHandler(catalog, oauth, context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.StartCallbackAsync(
            new PackageCallbackStartContext(
                "session",
                new Uri("http://localhost:1455/callbacks/session"),
                McpOAuthCallbackHandler.HandlerId,
                new Dictionary<string, string>
                {
                    [McpOAuthCallbackHandler.ServerIdParameter] = "missing",
                })));

        Assert.Contains("not found", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OAuthClientOptions_IgnoreOnlyMalformedStoredRegistration()
    {
        var context = new TestPackageContext();
        context.Secrets.Seed(McpOAuthSecretKeys.ClientRegistration("oauth-server"), "{not-json");
        await using var oauth = new McpOAuthService(context);
        var server = CreateServer("oauth-server", "OAuth server") with { OAuthEnabled = true };

        var options = await oauth.CreateClientOptionsAsync(server, allowInteractive: false);

        Assert.NotNull(options);
    }

    [Fact]
    public void ToolIdentity_UsesStableServerIdAndRoundTripsUnambiguousSegments()
    {
        Assert.Equal("m1_2_memi", McpToolIdentity.CreateStable("a", "b"));

        var first = McpToolIdentity.CreateStable("server_a/1", "tool_with_prefix__segment-9");
        var renamed = McpToolIdentity.CreateStable("server_a/1", "tool_with_prefix__segment-9");
        var second = McpToolIdentity.CreateStable("server_a/10", "tool_with_prefix__segment-9");

        Assert.Equal(first, renamed);
        Assert.NotEqual(first, second);
        Assert.True(McpToolIdentity.TryParseStable(first.ToUpperInvariant(), out var serverId, out var toolName));
        Assert.Equal("server_a/1", serverId);
        Assert.Equal("tool_with_prefix__segment-9", toolName);
    }

    [Fact]
    public void LegacyToolIdentity_IsAliasOnlyAndRejectsPrefixAmbiguity()
    {
        var servers = new[]
        {
            CreateServer("one", "foo"),
            CreateServer("two", "foo_bar"),
        };

        Assert.Equal("foo_run", McpToolIdentity.CreateLegacyAlias("foo", "run"));
        Assert.False(McpToolIdentity.TryParseLegacy("foo_bar_run", servers, out _, out _));
        Assert.True(McpToolIdentity.TryParseLegacy("foo_run", servers, out var server, out var toolName));
        Assert.Equal("one", server?.ServerId);
        Assert.Equal("run", toolName);
    }

    [Fact]
    public async Task CommandResolver_RemovesUnconfiguredWorkerEnvironmentFromTransport()
    {
        const string configuredName = "MCP_EXPLICIT_TEST_TOKEN";
        var allowedNames = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? [
                    "APPDATA", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "PATH", "PATHEXT",
                    "PROCESSOR_ARCHITECTURE", "PROGRAMFILES", "SYSTEMDRIVE", "SYSTEMROOT",
                    "TEMP", "USERNAME", "USERPROFILE",
                ]
                : ["HOME", "LOGNAME", "PATH", "SHELL", "TERM", "USER"],
            StringComparer.OrdinalIgnoreCase);
        var inheritedNames = Environment.GetEnvironmentVariables().Keys
            .OfType<string>()
            .Where(name => !allowedNames.Contains(name))
            .Where(name => !string.Equals(name, configuredName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(inheritedNames);

        var resolution = await McpCommandResolver.ResolveAsync(
            OperatingSystem.IsWindows()
                ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
                : "/bin/sh",
            workingDirectory: null,
            new Dictionary<string, string> { [configuredName] = "configured-secret" },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal("configured-secret", resolution.EnvironmentVariables[configuredName]);
        Assert.All(inheritedNames, name =>
        {
            Assert.True(resolution.EnvironmentVariables.TryGetValue(name, out var value));
            Assert.Null(value);
        });
    }

    [UnixFact("The login-shell process-tree fixture requires a POSIX shell.")]
    public async Task CommandResolver_LoginShellTimeoutKillsAndDrainsProcessTree()
    {
        var fixture = CreateBlockingShellProbe();
        try
        {
            var probe = McpCommandResolver.TryReadLoginShellPathAsync(
                fixture.ScriptPath,
                ShellProbeEnvironment(),
                NullLogger.Instance,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            // Source-build fixtures can delay shell startup beyond the default
            // polling budget. Keep the five-second probe timeout and drain limits.
            await WaitUntilAsync(() => File.Exists(fixture.ProcessIdsPath), TimeSpan.FromSeconds(10));
            fixture.ProcessIds.AddRange(ReadProcessIds(fixture.ProcessIdsPath));

            Assert.Null(await probe.WaitAsync(TimeSpan.FromSeconds(10)));
            await WaitUntilAsync(() => fixture.ProcessIds.All(static processId => !IsProcessRunning(processId)));
        }
        finally
        {
            TerminateProcesses(fixture.ProcessIds);
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [UnixFact("The login-shell process-tree fixture requires a POSIX shell.")]
    public async Task CommandResolver_LoginShellCancellationPropagatesAfterProcessTreeDrain()
    {
        var fixture = CreateBlockingShellProbe();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var probe = McpCommandResolver.TryReadLoginShellPathAsync(
                fixture.ScriptPath,
                ShellProbeEnvironment(),
                NullLogger.Instance,
                TimeSpan.FromSeconds(30),
                cancellation.Token);
            // Process startup competes with source-build fixtures in the full
            // suite. Give readiness its own bounded budget; cancellation and
            // process-tree drain assertions below retain their existing limits.
            await WaitUntilAsync(() => File.Exists(fixture.ProcessIdsPath), TimeSpan.FromSeconds(10));
            fixture.ProcessIds.AddRange(ReadProcessIds(fixture.ProcessIdsPath));

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                probe.WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitUntilAsync(() => fixture.ProcessIds.All(static processId => !IsProcessRunning(processId)));
        }
        finally
        {
            await cancellation.CancelAsync();
            TerminateProcesses(fixture.ProcessIds);
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyToolIdentity_CanonicalizesForPersistedCalls()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "foo"), Empty, Empty);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, new FakeConnectionFactory());
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest("foo_run", "{}"));

        Assert.True(result.IsError);
        Assert.Equal(McpToolIdentity.CreateStable("one", "run"), result.ToolId);
        Assert.Equal("MCP: foo: Run is unavailable.", result.Summary);
        Assert.Equal("mcp-server-unavailable", result.ErrorCode);
    }

    [Fact]
    public async Task ToolExecutionSummary_LongServerDisplayRetainsToolName()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var longDisplayName = new string('s', 52) + "\U0001F600" + new string('s', 202);
        var server = CreateServer("one", "foo") with { DisplayName = longDisplayName };
        await catalog.SaveServerAsync(server, Empty, Empty);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, new FakeConnectionFactory());
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "run"), "{}"));

        Assert.True(result.IsError);
        Assert.StartsWith("MCP: ", result.Summary, StringComparison.Ordinal);
        Assert.Contains(": Run is unavailable.", result.Summary, StringComparison.Ordinal);
        _ = new UTF8Encoding(false, true).GetBytes(result.Summary);
    }

    [Fact]
    public async Task ToolExecution_PreservesStructuredResultBeyondFormerByteCap()
    {
        const int formerMaximumBytes = 4 * 1024 * 1024;
        var payload = new string('x', formerMaximumBytes + 1);
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "stitch"), Empty, Empty);
        var factory = new InvokableConnectionFactory((_, _, _) =>
            ValueTask.FromResult(new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new { payload }),
            }));
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.False(result.WasTruncated);
        Assert.NotNull(result.Content);
        Assert.NotNull(result.StructuredPayloadJson);
        Assert.True(Encoding.UTF8.GetByteCount(result.StructuredPayloadJson) > formerMaximumBytes);
        using var serialized = JsonDocument.Parse(result.StructuredPayloadJson);
        Assert.Equal(payload.Length, serialized.RootElement.GetProperty("payload").GetString()!.Length);
    }

    [Fact]
    public async Task ToolExecution_PreservesAllContentBlocksBeyondFormerItemCap()
    {
        const int formerMaximumItems = 1024;
        var content = Enumerable.Range(0, formerMaximumItems + 1)
            .Select(index => (ContentBlock)new TextContentBlock { Text = $"item-{index}" })
            .ToList();
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "stitch"), Empty, Empty);
        var factory = new InvokableConnectionFactory((_, _, _) =>
            ValueTask.FromResult(new CallToolResult { Content = content }));
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.False(result.WasTruncated);
        Assert.NotNull(result.Content);
        using var serialized = JsonDocument.Parse(result.Content);
        Assert.Equal(content.Count, serialized.RootElement.GetArrayLength());
        Assert.Equal("item-1024", serialized.RootElement[formerMaximumItems].GetProperty("text").GetString());
    }

    [Fact]
    public void ToolTimeoutMetadata_UsesFlatMetadataOnlyForAClosedDirectObject()
    {
        var originalSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                prompt = new { type = "string" },
            },
            additionalProperties = false,
        });

        var augmented = McpToolTimeoutMetadata.AugmentSchema(originalSchema);

        Assert.Equal(McpToolArgumentSchemaMode.Flat, augmented.Mode);
        Assert.False(originalSchema.GetProperty("properties").TryGetProperty(McpToolTimeoutMetadata.MetadataMarkerPropertyName, out _));
        Assert.False(originalSchema.GetProperty("properties").TryGetProperty(McpToolTimeoutMetadata.TimeoutPropertyName, out _));
        var properties = augmented.Schema.GetProperty("properties");
        var timeoutSchema = properties.GetProperty(McpToolTimeoutMetadata.TimeoutPropertyName);
        Assert.Equal(["integer", "null"], timeoutSchema.GetProperty("type").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(1, timeoutSchema.GetProperty("minimum").GetInt32());
        Assert.Equal(1800, timeoutSchema.GetProperty("maximum").GetInt32());
        Assert.Contains("Sunder-only", timeoutSchema.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains(
            McpToolTimeoutMetadata.MetadataMarkerPropertyName,
            augmented.Schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));

        Assert.True(AgentToolArgumentObject.TryParse(
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["prompt"] = "Build a dashboard",
                [McpToolTimeoutMetadata.MetadataMarkerPropertyName] = McpToolTimeoutMetadata.MetadataMarkerValue,
                [McpToolTimeoutMetadata.TimeoutPropertyName] = 90,
            }),
            out var parsedArguments,
            out _));
        Assert.True(McpToolTimeoutMetadata.TryExtract(
            parsedArguments!,
            out var forwardedArguments,
            out var timeoutSeconds,
            out _));
        Assert.Equal(90, timeoutSeconds);
        Assert.Equal("Build a dashboard", Assert.IsType<JsonElement>(forwardedArguments["prompt"]).GetString());
        Assert.DoesNotContain(McpToolTimeoutMetadata.MetadataMarkerPropertyName, forwardedArguments.Keys);
        Assert.DoesNotContain(McpToolTimeoutMetadata.TimeoutPropertyName, forwardedArguments.Keys);
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"name":{"type":"string"}}}""")]
    [InlineData("""{"type":"object","properties":{},"patternProperties":{"^x-":{"type":"string"}},"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{},"propertyNames":{"pattern":"^[a-z]+$"},"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{"__sunder_mcp_metadata_version":{"type":"string"}},"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{},"required":["__sunder_expected_timeout_seconds"],"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{"__sunder_mcp_call":{"type":"string"}},"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{},"required":["__sunder_mcp_call"],"additionalProperties":false}""")]
    [InlineData("""{"type":"object","$defs":{"name":{"type":"string"}},"properties":{"name":{"$ref":"#/$defs/name"}},"additionalProperties":false}""")]
    [InlineData("""{"$id":"https://example.test/args","type":"object","properties":{"name":{"type":"string"}},"additionalProperties":false}""")]
    [InlineData("""{"$ref":"#/$defs/args","$defs":{"args":{"type":"object","properties":{"name":{"type":"string"}},"additionalProperties":false}}}""")]
    [InlineData("""{"oneOf":[{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":false},{"type":"object","properties":{"b":{"type":"integer"}},"additionalProperties":false}]}""")]
    [InlineData("""{"allOf":[{"type":"object","properties":{"a":{"type":"string"}}},{"type":"object","properties":{"b":{"type":"integer"}}}],"unevaluatedProperties":false}""")]
    public void ToolTimeoutMetadata_UsesEnvelopeWhenFlatOwnershipCannotBeProven(string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        var originalSchema = document.RootElement.Clone();

        var augmented = McpToolTimeoutMetadata.AugmentSchema(originalSchema);

        Assert.Equal(McpToolArgumentSchemaMode.Envelope, augmented.Mode);
        var envelope = augmented.Schema
            .GetProperty("properties")
            .GetProperty(McpToolTimeoutMetadata.EnvelopePropertyName);
        Assert.Equal(
            [McpToolTimeoutMetadata.MetadataMarkerPropertyName, McpToolTimeoutMetadata.EnvelopeArgumentsPropertyName],
            envelope.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            McpToolTimeoutMetadata.MetadataMarkerValue,
            envelope.GetProperty("properties")
                .GetProperty(McpToolTimeoutMetadata.MetadataMarkerPropertyName)
                .GetProperty("const")
                .GetString());
        Assert.Equal(schemaJson, originalSchema.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToolTimeoutMetadata_FlatTreatsOmittedOrNullTimeoutAsNoLimit(bool includeNullTimeout)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["prompt"] = "Build a dashboard",
            [McpToolTimeoutMetadata.MetadataMarkerPropertyName] = McpToolTimeoutMetadata.MetadataMarkerValue,
        };
        if (includeNullTimeout)
        {
            arguments[McpToolTimeoutMetadata.TimeoutPropertyName] = null;
        }
        Assert.True(AgentToolArgumentObject.TryParse(
            JsonSerializer.Serialize(arguments),
            out var parsedArguments,
            out _));

        Assert.True(McpToolTimeoutMetadata.TryExtract(
            parsedArguments!,
            out var forwardedArguments,
            out var timeoutSeconds,
            out _));

        Assert.Null(timeoutSeconds);
        Assert.Contains("prompt", forwardedArguments.Keys);
        Assert.DoesNotContain(McpToolTimeoutMetadata.MetadataMarkerPropertyName, forwardedArguments.Keys);
        Assert.DoesNotContain(McpToolTimeoutMetadata.TimeoutPropertyName, forwardedArguments.Keys);
    }

    [Fact]
    public void ToolTimeoutMetadata_EnvelopePreservesRootRefAndClosedCombinators()
    {
        using var document = JsonDocument.Parse("""
            {
              "$ref": "#/$defs/args",
              "$defs": {
                "args": {
                  "oneOf": [
                    { "type": "object", "properties": { "prompt": { "type": "string" } }, "additionalProperties": false },
                    { "allOf": [
                      { "type": "object", "properties": { "path": { "type": "string" } } },
                      { "type": "object", "properties": { "depth": { "type": "integer" } } }
                    ], "unevaluatedProperties": false }
                  ]
                }
              }
            }
            """);

        var augmented = McpToolTimeoutMetadata.AugmentSchema(document.RootElement);
        var argumentsSchema = augmented.Schema
            .GetProperty("properties")
            .GetProperty(McpToolTimeoutMetadata.EnvelopePropertyName)
            .GetProperty("properties")
            .GetProperty(McpToolTimeoutMetadata.EnvelopeArgumentsPropertyName);

        Assert.Equal("#/$defs/args", argumentsSchema.GetProperty("$ref").GetString());
        Assert.Equal("urn:sunder:mcp:arguments:1", argumentsSchema.GetProperty("$id").GetString());
        Assert.Equal(2, argumentsSchema.GetProperty("$defs").GetProperty("args").GetProperty("oneOf").GetArrayLength());
    }

    [Fact]
    public void ToolTimeoutMetadata_EnvelopePreservesAnExistingSchemaResourceIdentity()
    {
        using var document = JsonDocument.Parse("""
            {
              "$id": "https://example.test/mcp-arguments",
              "$ref": "#/$defs/args",
              "$defs": {
                "args": { "type": "object", "properties": { "prompt": { "type": "string" } } }
              }
            }
            """);

        var augmented = McpToolTimeoutMetadata.AugmentSchema(document.RootElement);
        var embedded = augmented.Schema
            .GetProperty("properties")
            .GetProperty(McpToolTimeoutMetadata.EnvelopePropertyName)
            .GetProperty("properties")
            .GetProperty(McpToolTimeoutMetadata.EnvelopeArgumentsPropertyName);

        Assert.Equal("sunder:mcp-arguments:1", embedded.GetProperty("$comment").GetString());
        var originalResource = embedded.GetProperty("allOf")[0];
        Assert.Equal("https://example.test/mcp-arguments", originalResource.GetProperty("$id").GetString());
        Assert.Equal("#/$defs/args", originalResource.GetProperty("$ref").GetString());
    }

    [Fact]
    public void ToolTimeoutMetadata_EnvelopeKeepsReservedServerArgumentsAndUnwrapsNullAsOmitted()
    {
        Assert.True(AgentToolArgumentObject.TryParse(
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [McpToolTimeoutMetadata.EnvelopePropertyName] = new Dictionary<string, object?>
                {
                    [McpToolTimeoutMetadata.MetadataMarkerPropertyName] = McpToolTimeoutMetadata.MetadataMarkerValue,
                    [McpToolTimeoutMetadata.EnvelopeArgumentsPropertyName] = new Dictionary<string, object?>
                    {
                        [McpToolTimeoutMetadata.TimeoutPropertyName] = "server-owned-value",
                        ["prompt"] = "Build a dashboard",
                    },
                    [McpToolTimeoutMetadata.TimeoutPropertyName] = null,
                },
            }),
            out var parsedArguments,
            out _));

        Assert.True(McpToolTimeoutMetadata.TryExtract(
            parsedArguments!,
            out var forwardedArguments,
            out var timeoutSeconds,
            out _));
        Assert.Null(timeoutSeconds);
        Assert.Equal(
            "server-owned-value",
            Assert.IsType<JsonElement>(forwardedArguments[McpToolTimeoutMetadata.TimeoutPropertyName]).GetString());
        Assert.Equal("Build a dashboard", Assert.IsType<JsonElement>(forwardedArguments["prompt"]).GetString());
    }

    [Fact]
    public void ToolTimeoutMetadata_AugmentsDescriptorAndNativeDeclarationWithSameSchema()
    {
        var originalSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                prompt = new { type = "string" },
            },
            additionalProperties = false,
        });

        var converted = McpToolSource.CreateRuntimeTool(
            CreateServer("one", "stitch"),
            "edit_screens",
            "Edit screens",
            "Edits screens.",
            originalSchema,
            returnJsonSchema: null);

        Assert.Equal(McpToolArgumentSchemaMode.Flat, converted.ArgumentSchemaMode);
        Assert.Equal(converted.RuntimeTool.Descriptor.ToolId, converted.RuntimeTool.Declaration.Name);
        Assert.NotNull(converted.RuntimeTool.Descriptor.ArgumentsJsonSchema);
        using var descriptorSchema = JsonDocument.Parse(converted.RuntimeTool.Descriptor.ArgumentsJsonSchema!);
        Assert.Equal(
            descriptorSchema.RootElement.GetRawText(),
            converted.RuntimeTool.Declaration.JsonSchema.GetRawText());
        Assert.Equal(
            ["integer", "null"],
            descriptorSchema.RootElement
                .GetProperty("properties")
                .GetProperty(McpToolTimeoutMetadata.TimeoutPropertyName)
                .GetProperty("type")
                .EnumerateArray()
                .Select(item => item.GetString()));
        Assert.False(
            originalSchema.GetProperty("properties").TryGetProperty(
                McpToolTimeoutMetadata.TimeoutPropertyName,
                out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1801)]
    public void ToolTimeoutMetadata_RejectsOutOfRangeValues(int value)
    {
        Assert.True(AgentToolArgumentObject.TryParse(
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [McpToolTimeoutMetadata.MetadataMarkerPropertyName] = McpToolTimeoutMetadata.MetadataMarkerValue,
                [McpToolTimeoutMetadata.TimeoutPropertyName] = value,
            }),
            out var parsedArguments,
            out _));

        Assert.False(McpToolTimeoutMetadata.TryExtract(
            parsedArguments!,
            out var forwardedArguments,
            out var timeoutSeconds,
            out var error));
        Assert.DoesNotContain(McpToolTimeoutMetadata.MetadataMarkerPropertyName, forwardedArguments.Keys);
        Assert.Null(timeoutSeconds);
        Assert.Contains("1 to 1800 seconds", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolExecution_InvalidSunderTimeoutMetadataFailsBeforeDispatch()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "stitch"), Empty, Empty);
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);
        var dispatchCount = 0;

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid())
            {
                DispatchStarted = () => Interlocked.Increment(ref dispatchCount),
            },
            new AgentToolRequest(
                McpToolIdentity.CreateStable("one", "edit_screens"),
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    [McpToolTimeoutMetadata.MetadataMarkerPropertyName] = McpToolTimeoutMetadata.MetadataMarkerValue,
                    [McpToolTimeoutMetadata.TimeoutPropertyName] = "60",
                })));

        Assert.Equal(0, dispatchCount);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Equal("mcp-arguments-invalid", result.ErrorCode);
        Assert.Contains("whole number from 1 to 1800 seconds", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolExecution_StripsSunderTimeoutMetadataBeforeMcpCall()
    {
        IReadOnlyDictionary<string, object?>? forwardedArguments = null;
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "stitch"), Empty, Empty);
        var factory = new InvokableConnectionFactory((_, arguments, _) =>
        {
            forwardedArguments = arguments;
            return ValueTask.FromResult(new CallToolResult());
        });
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(
                McpToolIdentity.CreateStable("one", "edit_screens"),
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["prompt"] = "Build a dashboard",
                    [McpToolTimeoutMetadata.MetadataMarkerPropertyName] = McpToolTimeoutMetadata.MetadataMarkerValue,
                    [McpToolTimeoutMetadata.TimeoutPropertyName] = 60,
                })));

        Assert.False(result.IsError);
        Assert.NotNull(forwardedArguments);
        Assert.Contains("prompt", forwardedArguments.Keys);
        Assert.DoesNotContain(McpToolTimeoutMetadata.MetadataMarkerPropertyName, forwardedArguments.Keys);
        Assert.DoesNotContain(McpToolTimeoutMetadata.TimeoutPropertyName, forwardedArguments.Keys);
    }

    [Fact]
    public async Task ToolExecution_ReservedLookingArgumentWithoutMarkerIsForwarded()
    {
        IReadOnlyDictionary<string, object?>? forwardedArguments = null;
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "stitch"), Empty, Empty);
        var factory = new InvokableConnectionFactory((_, arguments, _) =>
        {
            forwardedArguments = arguments;
            return ValueTask.FromResult(new CallToolResult());
        });
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(
                McpToolIdentity.CreateStable("one", "edit_screens"),
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    [McpToolTimeoutMetadata.TimeoutPropertyName] = "server-owned-value",
                    [McpToolTimeoutMetadata.EnvelopePropertyName] = new Dictionary<string, object?>
                    {
                        [McpToolTimeoutMetadata.MetadataMarkerPropertyName] = "server-version",
                        [McpToolTimeoutMetadata.EnvelopeArgumentsPropertyName] = "server-owned-envelope",
                    },
                })));

        Assert.False(result.IsError);
        Assert.NotNull(forwardedArguments);
        Assert.Equal(
            "server-owned-value",
            Assert.IsType<JsonElement>(forwardedArguments[McpToolTimeoutMetadata.TimeoutPropertyName]).GetString());
        var forwardedEnvelope = Assert.IsType<JsonElement>(forwardedArguments[McpToolTimeoutMetadata.EnvelopePropertyName]);
        Assert.Equal("server-version", forwardedEnvelope.GetProperty(McpToolTimeoutMetadata.MetadataMarkerPropertyName).GetString());
        Assert.Equal("server-owned-envelope", forwardedEnvelope.GetProperty(McpToolTimeoutMetadata.EnvelopeArgumentsPropertyName).GetString());
    }

    [Fact]
    public async Task ToolExecution_UnwrapsSelfIdentifyingEnvelopeBeforeMcpCall()
    {
        IReadOnlyDictionary<string, object?>? forwardedArguments = null;
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "stitch"), Empty, Empty);
        var factory = new InvokableConnectionFactory((_, arguments, _) =>
        {
            forwardedArguments = arguments;
            return ValueTask.FromResult(new CallToolResult());
        });
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(
                McpToolIdentity.CreateStable("one", "edit_screens"),
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    [McpToolTimeoutMetadata.EnvelopePropertyName] = new Dictionary<string, object?>
                    {
                        [McpToolTimeoutMetadata.MetadataMarkerPropertyName] = McpToolTimeoutMetadata.MetadataMarkerValue,
                        [McpToolTimeoutMetadata.EnvelopeArgumentsPropertyName] = new Dictionary<string, object?>
                        {
                            ["prompt"] = "Build a dashboard",
                        },
                        [McpToolTimeoutMetadata.TimeoutPropertyName] = null,
                    },
                })));

        Assert.False(result.IsError);
        Assert.NotNull(forwardedArguments);
        Assert.Equal("Build a dashboard", Assert.IsType<JsonElement>(forwardedArguments["prompt"]).GetString());
        Assert.DoesNotContain(McpToolTimeoutMetadata.EnvelopePropertyName, forwardedArguments.Keys);
    }

    [Fact]
    public async Task ToolExecution_NoLimitsPassesCallerTokenWithoutLocalTimer()
    {
        bool? canBeCanceled = null;
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "stitch"), Empty, Empty);
        var factory = new InvokableConnectionFactory((_, _, cancellationToken) =>
        {
            canBeCanceled = cancellationToken.CanBeCanceled;
            return ValueTask.FromResult(new CallToolResult());
        });
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.False(result.IsError);
        Assert.False(canBeCanceled);
    }

    [Fact]
    public async Task ToolExecution_NoLimitsRemainsPendingUntilCallerCancellation()
    {
        var invocationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "stitch"), Empty, Empty);
        var factory = new InvokableConnectionFactory(async (_, _, cancellationToken) =>
        {
            invocationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);
        using var cancellation = new CancellationTokenSource();

        var invocation = source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"),
            cancellation.Token).AsTask();
        await invocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(150);
        Assert.False(invocation.IsCompleted);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
    }

    [Fact]
    public async Task ToolExecution_ConfiguredTimerProducesMatchingAmbiguousTimeout()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer("one", "stitch") with { ToolTimeoutMilliseconds = 50 };
        await catalog.SaveServerAsync(server, Empty, Empty);
        var factory = new InvokableConnectionFactory(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var dispatchCount = 0;
        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid())
            {
                DispatchStarted = () => Interlocked.Increment(ref dispatchCount),
            },
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.Equal(1, dispatchCount);
        Assert.True(result.IsError);
        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.ErrorCode);
        Assert.Contains("timed out after 50 milliseconds", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("server configuration", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HttpClient", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("External effects may have occurred", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolExecution_ServerAndAgentTimeoutReportsBothSourcesAndEffectiveDuration()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer("one", "stitch") with { ToolTimeoutMilliseconds = 50 };
        await catalog.SaveServerAsync(server, Empty, Empty);
        var factory = new InvokableConnectionFactory(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(
                McpToolIdentity.CreateStable("one", "edit_screens"),
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    [McpToolTimeoutMetadata.MetadataMarkerPropertyName] = McpToolTimeoutMetadata.MetadataMarkerValue,
                    [McpToolTimeoutMetadata.TimeoutPropertyName] = 1,
                })));

        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.ErrorCode);
        Assert.Contains("50 milliseconds", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("both server configuration and the agent request", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("both server configuration and the agent request", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolExecution_AgentTimeoutReportsAgentSourceAndDuration()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "stitch"), Empty, Empty);
        var factory = new InvokableConnectionFactory(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(
                McpToolIdentity.CreateStable("one", "edit_screens"),
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    [McpToolTimeoutMetadata.MetadataMarkerPropertyName] = McpToolTimeoutMetadata.MetadataMarkerValue,
                    [McpToolTimeoutMetadata.TimeoutPropertyName] = 1,
                })));

        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.ErrorCode);
        Assert.Contains("1 second", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agent request", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agent request", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolExecution_TransportCancellationIsNotMislabeledAsConfiguredTimeout()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer("one", "stitch") with { ToolTimeoutMilliseconds = 120_000 };
        await catalog.SaveServerAsync(server, Empty, Empty);
        var factory = new InvokableConnectionFactory((_, _, _) =>
            ValueTask.FromException<CallToolResult>(new TaskCanceledException("Transport canceled.")));
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.True(result.IsError);
        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.ErrorCode);
        Assert.Contains("outcome is uncertain", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("120 seconds", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(McpErrorCode.ParseError)]
    [InlineData(McpErrorCode.InvalidRequest)]
    [InlineData(McpErrorCode.MethodNotFound)]
    [InlineData(McpErrorCode.InvalidParams)]
    public async Task ToolExecution_ProtocolRequestErrorIsDefinitiveFailureAfterDispatch(McpErrorCode errorCode)
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer("one", "stitch");
        await catalog.SaveServerAsync(server, Empty, Empty);
        var factory = new InvokableConnectionFactory((_, _, _) =>
            ValueTask.FromException<CallToolResult>(new McpProtocolException(
                "Unknown tool.",
                errorCode)));
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);
        var dispatchCount = 0;

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid())
            {
                DispatchStarted = () => Interlocked.Increment(ref dispatchCount),
            },
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.Equal(1, dispatchCount);
        Assert.True(result.IsError);
        Assert.Equal("mcp-protocol-error", result.ErrorCode);
        Assert.NotEqual(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.ErrorCode);
    }

    [Theory]
    [InlineData(McpErrorCode.InternalError)]
    [InlineData((McpErrorCode)(-32099))]
    public async Task ToolExecution_InternalOrUnknownProtocolErrorAfterDispatchIsAmbiguous(McpErrorCode errorCode)
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "stitch"), Empty, Empty);
        var factory = new InvokableConnectionFactory((_, _, _) =>
            ValueTask.FromException<CallToolResult>(new McpProtocolException("Protocol failed.", errorCode)));
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.ErrorCode);
    }

    [Fact]
    public async Task ToolExecution_ProtocolWrappedConfiguredCancellationUsesTimeoutOutcome()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(
            CreateServer("one", "stitch") with { ToolTimeoutMilliseconds = 50 },
            Empty,
            Empty);
        var factory = new InvokableConnectionFactory(async (_, _, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                throw new McpProtocolException("Wrapped cancellation.", ex, McpErrorCode.InternalError);
            }
            throw new InvalidOperationException("Unreachable.");
        });
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.ErrorCode);
        Assert.Contains("timed out after 50 milliseconds", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolExecution_ProtocolWrappedIndependentCancellationIsAmbiguous()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "stitch"), Empty, Empty);
        var factory = new InvokableConnectionFactory((_, _, _) =>
            ValueTask.FromException<CallToolResult>(new McpProtocolException(
                "Wrapped cancellation.",
                new TaskCanceledException("Transport canceled."),
                McpErrorCode.InternalError)));
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.ErrorCode);
        Assert.DoesNotContain("timed out", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolExecution_ProtocolWrappedCallerCancellationPropagates()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "stitch"), Empty, Empty);
        using var cancellation = new CancellationTokenSource();
        var factory = new InvokableConnectionFactory((_, _, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromException<CallToolResult>(new McpProtocolException(
                "Wrapped cancellation.",
                new TaskCanceledException("Caller canceled."),
                McpErrorCode.InternalError));
        });
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"),
            cancellation.Token).AsTask());
    }

    [Fact]
    public async Task ToolExecution_ProtocolWrappedTransportLossIsAmbiguous()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer("one", "stitch");
        await catalog.SaveServerAsync(server, Empty, Empty);
        var factory = new InvokableConnectionFactory((_, _, _) =>
            ValueTask.FromException<CallToolResult>(new McpProtocolException(
                "Protocol stream ended.",
                new IOException("Transport lost."))));
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.True(result.IsError);
        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.ErrorCode);
    }

    [Fact]
    public async Task ToolExecution_UnexpectedFailureAfterDispatchIsAmbiguous()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer("one", "stitch");
        await catalog.SaveServerAsync(server, Empty, Empty);
        var factory = new InvokableConnectionFactory((_, _, _) =>
            ValueTask.FromException<CallToolResult>(new InvalidOperationException("Unexpected decode failure.")));
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.True(result.IsError);
        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.ErrorCode);
    }

    [Fact]
    public async Task ToolExecution_ConfiguredTimeoutStartsAfterDispatchAcknowledgment()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer("one", "stitch") with { ToolTimeoutMilliseconds = 50 };
        await catalog.SaveServerAsync(server, Empty, Empty);
        var factory = new InvokableConnectionFactory((_, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<CallToolResult>(new McpProtocolException(
                "Definitive response error.",
                McpErrorCode.InvalidParams));
        });
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid())
            {
                DispatchStarted = () => Thread.Sleep(100),
            },
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.Equal("mcp-protocol-error", result.ErrorCode);
        Assert.DoesNotContain("timed out", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolExecution_DiscoveryFailureDoesNotSignalDispatch()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer("one", "stitch");
        await catalog.SaveServerAsync(server, Empty, Empty);
        var factory = new FakeConnectionFactory
        {
            DiscoveryFailure = new InvalidOperationException("Discovery failed."),
        };
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);
        var dispatchCount = 0;

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid())
            {
                DispatchStarted = () => Interlocked.Increment(ref dispatchCount),
            },
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"));

        Assert.Equal(0, dispatchCount);
        Assert.True(result.IsError);
        Assert.Equal("mcp-server-unavailable", result.ErrorCode);
    }

    [Fact]
    public async Task ToolExecution_ConnectionCancellationPropagatesWithoutSignalingDispatch()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer("one", "stitch");
        await catalog.SaveServerAsync(server, Empty, Empty);
        var factory = new FakeConnectionFactory { BlockConnect = true };
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var source = new McpToolSource(catalog, manager);
        var dispatchCount = 0;
        using var cancellation = new CancellationTokenSource();

        var invocation = source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid())
            {
                DispatchStarted = () => Interlocked.Increment(ref dispatchCount),
            },
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"),
            cancellation.Token).AsTask();
        await factory.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.Equal(0, dispatchCount);
    }

    [Fact]
    public async Task ToolExecution_HeaderCancellationPropagatesWithoutSignalingDispatch()
    {
        var secrets = new FaultingSecrets();
        var context = new TestPackageContext(secrets: secrets);
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer("one", "stitch") with
        {
            HeaderNames = ["Authorization"],
        };
        await catalog.SaveServerAsync(
            server,
            new Dictionary<string, string> { ["Authorization"] = "secret" },
            Empty);
        secrets.BlockReads = true;
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, new FakeConnectionFactory());
        var source = new McpToolSource(catalog, manager);
        var dispatchCount = 0;
        using var cancellation = new CancellationTokenSource();

        var invocation = source.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid())
            {
                DispatchStarted = () => Interlocked.Increment(ref dispatchCount),
            },
            new AgentToolRequest(McpToolIdentity.CreateStable("one", "edit_screens"), "{}"),
            cancellation.Token).AsTask();
        await secrets.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.Equal(0, dispatchCount);
    }

    [Fact]
    public async Task Catalog_RejectsDuplicateNormalizedNames()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "Foo Bar"), Empty, Empty);

        var error = await Assert.ThrowsAsync<McpManagementOperationException>(() =>
            catalog.SaveServerAsync(CreateServer("two", "foo-bar"), Empty, Empty));

        Assert.Contains("already exists", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await catalog.ListServersAsync());
    }

    [Fact]
    public async Task Catalog_UsesPortablePhysicalKeysForImportedServerAndSecretIdentifiers()
    {
        var serverId = " imported:/\u65E5\u672C\u8A9E/" + new string('s', 300);
        const string headerName = "X Imported / \u00E9";
        const string environmentName = "TOKEN / \u65E5";
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer(serverId, "portable") with
        {
            HeaderNames = [headerName],
            EnvironmentVariableNames = [environmentName],
        };

        await catalog.SaveServerAsync(
            server,
            new Dictionary<string, string> { [headerName] = "header-secret" },
            new Dictionary<string, string> { [environmentName] = "environment-secret" });

        Assert.Equal(serverId, Assert.Single(await catalog.ListServersAsync()).ServerId);
        Assert.All(await context.State.ListKeysAsync(), key => Assert.True(PackageStorageValidation.IsValidKey(key)));
        Assert.All(context.Secrets.Keys, key => Assert.True(PackageStorageValidation.IsValidKey(key)));
        Assert.True(PackageStorageValidation.IsValidKey(McpOAuthSecretKeys.TokenCache(serverId)));
        Assert.True(PackageStorageValidation.IsValidKey(McpOAuthSecretKeys.ClientRegistration(serverId)));
        Assert.True(PackageStorageValidation.IsValidKey(McpOAuthSecretKeys.ClientSecret(serverId)));
    }

    [Fact]
    public async Task RuntimeEditorDocuments_RedactSecretsAndSaveTreatsValuesAsCommands()
    {
        const string retainedCanary = "retained-header-secret-canary";
        const string clearedCanary = "cleared-header-secret-canary";
        const string replacedCanary = "replaced-header-secret-canary";
        const string replacementCanary = "replacement-header-command-canary";
        const string retainedEnvironmentCanary = "retained-environment-secret-canary";
        const string clearedEnvironmentCanary = "cleared-environment-secret-canary";
        const string replacedEnvironmentCanary = "replaced-environment-secret-canary";
        const string environmentReplacementCanary = "replacement-environment-command-canary";
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var remote = McpConfigurationDocument.Parse(
            "remote-server",
            "remote_server",
            $$"""
            {
              "type": "remote",
              "url": "https://example.com/mcp",
              "headers": {
                "X-Retain": "{{retainedCanary}}",
                "X-Clear": "{{clearedCanary}}",
                "X-Replace": "{{replacedCanary}}"
              }
            }
            """);
        var local = McpConfigurationDocument.Parse(
            "local-server",
            "local_server",
            $$"""
            {
              "type": "local",
              "command": ["example-mcp"],
              "env": {
                "ENV_RETAIN": "{{retainedEnvironmentCanary}}",
                "ENV_CLEAR": "{{clearedEnvironmentCanary}}",
                "ENV_REPLACE": "{{replacedEnvironmentCanary}}"
              }
            }
            """);
        await catalog.SaveServerAsync(remote.Server, remote.Headers, remote.EnvironmentVariables);
        await catalog.SaveServerAsync(local.Server, local.Headers, local.EnvironmentVariables);
        var editor = new McpSettingsEditorService(catalog);
        await using var connections = new McpClientConnectionManager(
            NullLoggerFactory.Instance,
            new FakeConnectionFactory());
        var connectionService = new McpServerConnectionService(catalog, connections, oauthService: null);
        var handler = new McpRuntimeHandler(
            catalog,
            editor,
            configuration: null!,
            connectionService,
            oauth: null!,
            context);
        using var appGateway = new McpAppRuntimeGateway(
            AgentRoleBoundary.As<McpApp::Sunder.Package.Agent.Mcp.Runtime.IMcpOwnerManagementClient>(new McpHandlerManagementClient(handler)),
            NullPackageCallbackClient.Instance);

        var remoteDocument = (await appGateway.LoadDocumentAsync(remote.Server.ServerId))!;
        var localDocument = (await appGateway.LoadDocumentAsync(local.Server.ServerId))!;

        Assert.DoesNotContain(retainedCanary, remoteDocument, StringComparison.Ordinal);
        Assert.DoesNotContain(clearedCanary, remoteDocument, StringComparison.Ordinal);
        Assert.DoesNotContain(replacedCanary, remoteDocument, StringComparison.Ordinal);
        Assert.DoesNotContain(retainedEnvironmentCanary, localDocument, StringComparison.Ordinal);
        Assert.DoesNotContain(clearedEnvironmentCanary, localDocument, StringComparison.Ordinal);
        Assert.DoesNotContain(replacedEnvironmentCanary, localDocument, StringComparison.Ordinal);
        using (var remoteJson = JsonDocument.Parse(remoteDocument))
        {
            Assert.All(
                remoteJson.RootElement.GetProperty("headers").EnumerateObject(),
                property => Assert.Equal(string.Empty, property.Value.GetString()));
        }
        using (var localJson = JsonDocument.Parse(localDocument))
        {
            Assert.All(
                localJson.RootElement.GetProperty("env").EnumerateObject(),
                property => Assert.Equal(string.Empty, property.Value.GetString()));
        }

        var persistedRemote = Assert.IsType<ConfiguredMcpServerRecord>(
            await catalog.GetServerAsync(remote.Server.ServerId));
        var clearedSecretKey = McpServerCatalogService.BuildHeaderSecretKey(
            persistedRemote.ServerId,
            persistedRemote.PersistenceVersion,
            "X-Clear");
        var update = McpConfigurationDocument.Parse(
            persistedRemote.ServerId,
            persistedRemote.Name,
            $$"""
            {
              "type": "remote",
              "url": "https://example.com/mcp",
              "headers": {
                "X-Retain": "",
                "X-Clear": null,
                "X-Replace": "{{replacementCanary}}"
              }
            }
            """,
            persistedRemote);
        var command = new McpCommand(McpCommandKind.Save, Configuration: update);
        var commandJson = JsonSerializer.Serialize(command);

        var saveProjection = await handler.HandleAsync(command);
        var responseJson = JsonSerializer.Serialize(saveProjection);

        Assert.Contains(replacementCanary, commandJson, StringComparison.Ordinal);
        Assert.DoesNotContain(retainedCanary, responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(clearedCanary, responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(replacedCanary, responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(replacementCanary, responseJson, StringComparison.Ordinal);
        var saved = Assert.IsType<ConfiguredMcpServerRecord>(
            await catalog.GetServerAsync(remote.Server.ServerId));
        var storedHeaders = await catalog.GetHeadersAsync(saved);
        Assert.Equal(retainedCanary, storedHeaders["X-Retain"]);
        Assert.False(storedHeaders.ContainsKey("X-Clear"));
        Assert.Equal(replacementCanary, storedHeaders["X-Replace"]);
        Assert.Null(await context.Secrets.GetSecretAsync(clearedSecretKey));
        using var savedDocument = JsonDocument.Parse(saveProjection.Document!);
        var savedHeaders = savedDocument.RootElement.GetProperty("headers");
        Assert.Equal(string.Empty, savedHeaders.GetProperty("X-Retain").GetString());
        Assert.False(savedHeaders.TryGetProperty("X-Clear", out _));
        Assert.Equal(string.Empty, savedHeaders.GetProperty("X-Replace").GetString());

        var persistedLocal = Assert.IsType<ConfiguredMcpServerRecord>(
            await catalog.GetServerAsync(local.Server.ServerId));
        var clearedEnvironmentKey = McpServerCatalogService.BuildEnvironmentSecretKey(
            persistedLocal.ServerId,
            persistedLocal.PersistenceVersion,
            "ENV_CLEAR");
        var environmentUpdate = McpConfigurationDocument.Parse(
            persistedLocal.ServerId,
            persistedLocal.Name,
            $$"""
            {
              "type": "local",
              "command": ["example-mcp"],
              "env": {
                "ENV_RETAIN": "",
                "ENV_CLEAR": null,
                "ENV_REPLACE": "{{environmentReplacementCanary}}"
              }
            }
            """,
            persistedLocal);
        var environmentCommand = new McpCommand(
            McpCommandKind.Save,
            Configuration: environmentUpdate);

        var environmentProjection = await handler.HandleAsync(environmentCommand);
        var environmentResponseJson = JsonSerializer.Serialize(environmentProjection);

        Assert.Contains(
            environmentReplacementCanary,
            JsonSerializer.Serialize(environmentCommand),
            StringComparison.Ordinal);
        Assert.DoesNotContain(retainedEnvironmentCanary, environmentResponseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(clearedEnvironmentCanary, environmentResponseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(replacedEnvironmentCanary, environmentResponseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(environmentReplacementCanary, environmentResponseJson, StringComparison.Ordinal);
        var savedLocal = Assert.IsType<ConfiguredMcpServerRecord>(
            await catalog.GetServerAsync(local.Server.ServerId));
        var storedEnvironment = await catalog.GetEnvironmentVariablesAsync(savedLocal);
        Assert.Equal(retainedEnvironmentCanary, storedEnvironment["ENV_RETAIN"]);
        Assert.False(storedEnvironment.ContainsKey("ENV_CLEAR"));
        Assert.Equal(environmentReplacementCanary, storedEnvironment["ENV_REPLACE"]);
        Assert.Null(await context.Secrets.GetSecretAsync(clearedEnvironmentKey));
    }

    [Fact]
    public async Task ConnectionPool_ReusesByServerAndDisposesOnVersionChangeAndShutdown()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one") with { PersistenceVersion = 1 };

        await manager.GetToolsAsync(server, Empty, Empty, null);
        await manager.GetToolsAsync(server, Empty, Empty, null);
        Assert.Equal(1, factory.ConnectCount);

        await manager.GetToolsAsync(server with { PersistenceVersion = 2 }, Empty, Empty, null);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(1, factory.Connections[0].DisposeCount);

        await manager.DisposeAsync();
        Assert.Equal(1, factory.Connections[1].DisposeCount);
    }

    [Fact]
    public async Task ConnectionPool_IsolatesLiveClientsBySessionAndWorkspaceWhileSharingTools()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one");
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();

        await AcquireAndReleaseAsync(manager, server, McpConnectionScope.For(firstSession, "workspace-a"));
        await AcquireAndReleaseAsync(manager, server, McpConnectionScope.For(firstSession, "workspace-a"));
        await AcquireAndReleaseAsync(manager, server, McpConnectionScope.For(secondSession, "workspace-a"));
        await AcquireAndReleaseAsync(manager, server, McpConnectionScope.For(firstSession, "workspace-b"));

        Assert.Equal(3, factory.ConnectCount);
        Assert.NotNull(manager.GetCachedTools(server));
        Assert.Equal(3, manager.GetStatus(server).ActiveConnectionCount);
    }

    [Fact]
    public async Task ConnectionPool_EvictsLeastRecentlyUsedIdleSessionConnectionAtCapacity()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(
            NullLoggerFactory.Instance,
            factory,
            maxSessionScopedConnections: 2);
        var server = CreateServer("one", "one");
        var firstScope = McpConnectionScope.For(Guid.NewGuid(), "workspace");
        var secondScope = McpConnectionScope.For(Guid.NewGuid(), "workspace");

        await AcquireAndReleaseAsync(manager, server, firstScope);
        await Task.Delay(10);
        await AcquireAndReleaseAsync(manager, server, secondScope);
        await Task.Delay(10);
        await AcquireAndReleaseAsync(manager, server, firstScope);
        await Task.Delay(10);
        await AcquireAndReleaseAsync(
            manager,
            server,
            McpConnectionScope.For(Guid.NewGuid(), "workspace"));

        Assert.Equal(3, factory.ConnectCount);
        Assert.Equal(2, manager.CachedConnectionCount);
        Assert.Equal(0, factory.Connections[0].DisposeCount);
        Assert.Equal(1, factory.Connections[1].DisposeCount);
        Assert.Equal(0, factory.Connections[2].DisposeCount);
    }

    [Fact]
    public async Task ConnectionPool_RejectsNewSessionConnectionWhenEveryEntryIsLeased()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(
            NullLoggerFactory.Instance,
            factory,
            maxSessionScopedConnections: 1);
        var server = CreateServer("one", "one");
        await using var lease = await manager.AcquireClientLeaseAsync(
            server,
            Empty,
            Empty,
            null,
            McpConnectionScope.For(Guid.NewGuid(), "workspace"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.AcquireClientLeaseAsync(
                server,
                Empty,
                Empty,
                null,
                McpConnectionScope.For(Guid.NewGuid(), "workspace")));

        Assert.Contains("every connection is in use", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, manager.CachedConnectionCount);
    }

    [Fact]
    public async Task SessionConnectionCleaner_DrainsOnlyConnectionsForDeletedSession()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one");
        var deletedSessionId = Guid.NewGuid();
        var retainedSessionId = Guid.NewGuid();
        await using var deletedSessionLease = await manager.AcquireClientLeaseAsync(
            server,
            Empty,
            Empty,
            null,
            McpConnectionScope.For(deletedSessionId, "workspace"));
        Assert.NotNull(deletedSessionLease);
        await AcquireAndReleaseAsync(
            manager,
            server,
            McpConnectionScope.For(retainedSessionId, "workspace"));
        var cleaner = new McpSessionConnectionCleaner(manager);

        var cleanup = Task.Run(() => cleaner.DeleteSessionData(deletedSessionId));
        await WaitUntilAsync(() => manager.CachedConnectionCount == 1);

        Assert.False(cleanup.IsCompleted);
        Assert.Equal(0, factory.Connections[0].DisposeCount);
        Assert.Equal(0, factory.Connections[1].DisposeCount);

        await deletedSessionLease.DisposeAsync();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, factory.Connections[0].DisposeCount);
        Assert.Equal(0, factory.Connections[1].DisposeCount);
        Assert.Equal(1, manager.CachedConnectionCount);
        Assert.Equal(McpConnectionStatusKind.Connected, manager.GetStatus(server).Kind);
    }

    [Fact]
    public async Task SessionConnectionCleaner_DisposesLateConnectionAndRejectsRecreation()
    {
        var factory = new LateConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one");
        var sessionId = Guid.NewGuid();
        var scope = McpConnectionScope.For(sessionId, "workspace");
        var discovery = manager.GetToolsAsync(server, Empty, Empty, null, scope);
        await factory.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var cleanup = manager.DisconnectSessionAsync(sessionId);
        await factory.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.ReleaseConnection.TrySetResult();

        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => discovery);
        Assert.Equal(1, factory.Connection.DisposeCount);
        Assert.Equal(0, manager.CachedConnectionCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.GetToolsAsync(server, Empty, Empty, null, scope));
        Assert.Equal(1, factory.ConnectCount);
    }

    [Fact]
    public async Task ConnectionPool_DisconnectRetiresConnectionUntilInvocationLeaseDrains()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one");
        await using var lease = await manager.AcquireClientLeaseAsync(server, Empty, Empty, null, McpConnectionScope.For(Guid.NewGuid(), "workspace"));
        Assert.NotNull(lease);
        Assert.Equal(1, manager.GetStatus(server).ActiveConnectionCount);

        var disconnect = manager.DisconnectServerAsync(server.ServerId);
        await WaitUntilAsync(() => manager.GetStatus(server).ActiveConnectionCount == 0);

        Assert.False(disconnect.IsCompleted);
        Assert.Equal(0, factory.Connections[0].DisposeCount);

        await lease.DisposeAsync();
        await disconnect.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, factory.Connections[0].DisposeCount);
    }

    [Fact]
    public async Task ConnectionPool_ForceReconnectInvalidatesMetadataAndCreatesNewConnection()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one");
        await manager.GetToolsAsync(server, Empty, Empty, null);
        Assert.NotNull(manager.GetCachedTools(server));

        await manager.ReconnectServerAsync(server.ServerId);

        Assert.Null(manager.GetCachedTools(server));
        await manager.GetToolsAsync(server, Empty, Empty, null);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(1, factory.Connections[0].DisposeCount);
        Assert.Equal(0, manager.KeyedLockCount);
    }

    [Fact]
    public async Task ConnectionPool_LeakedInvocationLeaseDoesNotBlockDisconnectForever()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one");
        var lease = await manager.AcquireClientLeaseAsync(server, Empty, Empty, null, McpConnectionScope.Shared);
        Assert.NotNull(lease);

        await manager.DisconnectServerAsync(server.ServerId).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, factory.Connections[0].DisposeCount);
        Assert.Equal(0, manager.KeyedLockCount);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionPool_ReconfigurationWaitsForInvocationLeaseBeforeReplacingConnection()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        var server = CreateServer("one", "one") with { PersistenceVersion = 1 };
        await using var lease = await manager.AcquireClientLeaseAsync(server, Empty, Empty, null, McpConnectionScope.Shared);
        Assert.NotNull(lease);

        var reconfigure = manager.GetToolsAsync(server with { PersistenceVersion = 2 }, Empty, Empty, null);
        await WaitUntilAsync(() => manager.GetStatus(server).ActiveConnectionCount == 0);

        Assert.False(reconfigure.IsCompleted);
        Assert.Equal(0, factory.Connections[0].DisposeCount);
        Assert.Equal(1, factory.ConnectCount);

        await lease.DisposeAsync();
        await factory.SecondConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await reconfigure;
        Assert.Equal(1, factory.Connections[0].DisposeCount);
        Assert.Equal(2, factory.ConnectCount);
    }

    [Fact]
    public async Task ToolDiscovery_AwaitsSharedConfigurationInitializationBarrier()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-mcp-initialization-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configRoot = Path.Combine(root, ".config", "sunder");
            Directory.CreateDirectory(configRoot);
            await File.WriteAllTextAsync(Path.Combine(configRoot, "mcp.json"), """
                { "mcp": { "barrier": { "type": "remote", "url": "https://example.com/mcp" } } }
                """);
            var state = new TestState { BlockOnNextSet = true };
            var context = new TestPackageContext(state);
            var catalog = new McpServerCatalogService(context);
            var importer = new McpEcosystemConfigurationImporter(catalog);
            using var extensions = new RegressionTestExtensionCatalog();
            await using var workspaces = new McpWorker::Sunder.Package.Agent.Mcp.Rpc.RuntimeWorkspaceCatalog(extensions.RpcForCaller("sunder.package.agent.mcp"));
            await workspaces.StartAsync();
            var sync = new McpSunderConfigurationSyncService(importer, workspaces, userProfilePath: root);
            var coordinator = new McpConfigurationCoordinator(importer, sync);
            await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, new FakeConnectionFactory());
            var source = new McpToolSource(catalog, manager, coordinator);

            var firstDiscovery = source.ListConfiguredServersAsync().AsTask();
            var secondDiscovery = source.ListConfiguredServersAsync().AsTask();
            await state.SetStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(firstDiscovery.IsCompleted);
            Assert.False(secondDiscovery.IsCompleted);

            state.ReleaseSet.TrySetResult();
            var results = await Task.WhenAll(firstDiscovery, secondDiscovery);

            Assert.All(results, servers => Assert.Equal("barrier", Assert.Single(servers).DisplayName));
            Assert.Equal(1, state.SetCount);
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
    public async Task ConnectionPool_PropagatesConnectionCancellation()
    {
        var factory = new FakeConnectionFactory { BlockConnect = true };
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        using var cancellation = new CancellationTokenSource();
        var pending = manager.GetToolsAsync(CreateServer("one", "one"), Empty, Empty, null, cancellation.Token);
        await factory.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ToolQueries_DoNotSynchronizeOrMutateConfiguration()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "one"), Empty, Empty);
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, new FakeConnectionFactory());
        var source = new McpToolSource(catalog, manager);
        var writesBefore = context.State.SetCount;

        var servers = await source.ListConfiguredServersAsync();

        Assert.Single(servers);
        Assert.Equal(writesBefore, context.State.SetCount);
    }

    [Fact]
    public async Task Sync_SerializesConcurrentImportsOfTheSameManagedFile()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var importer = new McpEcosystemConfigurationImporter(catalog);
        using var extensions = new RegressionTestExtensionCatalog();
        await using var workspaces = new McpWorker::Sunder.Package.Agent.Mcp.Rpc.RuntimeWorkspaceCatalog(extensions.RpcForCaller("sunder.package.agent.mcp"));
        await workspaces.StartAsync();
        var sync = new McpSunderConfigurationSyncService(importer, workspaces);
        var path = Path.Combine(Path.GetTempPath(), "sunder-mcp-sync-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """
                { "mcp": { "one": { "type": "remote", "url": "https://example.com/mcp" } } }
                """);

            var results = await Task.WhenAll(sync.SyncFilesAsync([path]), sync.SyncFilesAsync([path]));

            Assert.Equal(1, results.Sum(result => result.ImportedCount));
            Assert.Single(await catalog.ListServersAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Catalog_SecretFailureKeepsPriorVisibleVersionAndCredentials()
    {
        var secrets = new FaultingSecrets();
        var context = new TestPackageContext(secrets: secrets);
        var catalog = new McpServerCatalogService(context);
        var original = CreateServer("one", "one") with { HeaderNames = ["Authorization"] };
        await catalog.SaveServerAsync(original, new Dictionary<string, string> { ["Authorization"] = "old" }, Empty);
        var persisted = Assert.Single(await catalog.ListServersAsync());
        secrets.FailOnSetNumber = secrets.SetCount + 2;
        var update = persisted with { HeaderNames = ["Authorization", "X-Api-Key"], UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveServerAsync(
            update,
            new Dictionary<string, string> { ["Authorization"] = "new", ["X-Api-Key"] = "new-key" },
            Empty));

        var after = Assert.Single(await catalog.ListServersAsync());
        Assert.Equal(persisted.PersistenceVersion, after.PersistenceVersion);
        Assert.Equal("old", (await catalog.GetHeadersAsync(after))["Authorization"]);
        Assert.DoesNotContain(secrets.Keys, key => key.Contains($".v{persisted.PersistenceVersion + 1}.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Catalog_StateFailureCompensatesStagedSecrets()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var original = CreateServer("one", "one") with { HeaderNames = ["Authorization"] };
        await catalog.SaveServerAsync(original, new Dictionary<string, string> { ["Authorization"] = "old" }, Empty);
        var persisted = Assert.Single(await catalog.ListServersAsync());
        context.State.ThrowOnNextSet = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveServerAsync(
            persisted with { UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) },
            new Dictionary<string, string> { ["Authorization"] = "new" },
            Empty));

        var after = Assert.Single(await catalog.ListServersAsync());
        Assert.Equal(persisted.PersistenceVersion, after.PersistenceVersion);
        Assert.Equal("old", (await catalog.GetHeadersAsync(after))["Authorization"]);
        Assert.DoesNotContain(context.Secrets.Keys, key => key.Contains($".v{persisted.PersistenceVersion + 1}.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Catalog_IsolatesMalformedEntriesAndPreservesThemWithDiagnostics()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "one"), Empty, Empty);
        var storageKey = McpServerCatalogService.BuildServerKey("broken");
        await context.State.SetValueAsync(storageKey, "{ not-json");

        var servers = await catalog.ListServersAsync();

        Assert.Single(servers);
        Assert.Contains(catalog.LastDiagnostics, diagnostic => diagnostic.StorageKey == storageKey);
        Assert.Equal("{ not-json", await context.State.GetValueAsync(storageKey));
    }

    [Fact]
    public async Task Catalog_WritesDocumentSchemaSeparatelyFromRecordRevision()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "one"), Empty, Empty);
        var persisted = Assert.Single(await catalog.ListServersAsync());
        var payload = await context.State.GetValueAsync(McpServerCatalogService.BuildServerKey(persisted.ServerId));

        using var document = JsonDocument.Parse(payload!);
        Assert.Equal(McpServerRecordSerializer.DocumentSchemaId, document.RootElement.GetProperty("DocumentSchemaId").GetString());
        Assert.Equal(McpServerRecordSerializer.DocumentSchemaVersion, document.RootElement.GetProperty("DocumentSchemaVersion").GetInt32());
        Assert.Equal(persisted.PersistenceVersion, document.RootElement.GetProperty("Server").GetProperty("PersistenceVersion").GetInt32());
    }

    [Fact]
    public async Task Catalog_ReadsReleasedFlatRecordDocument()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer("legacy", "legacy");
        await context.State.SetValueAsync(
            McpServerCatalogService.BuildServerKey(server.ServerId),
            JsonSerializer.Serialize(server));

        var loaded = Assert.Single(await catalog.ListServersAsync());

        Assert.Equal(server.ServerId, loaded.ServerId);
        Assert.Empty(catalog.LastDiagnostics);
    }

    [Fact]
    public async Task Catalog_RejectsFutureDocumentSchemaAndPreservesPayload()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var server = CreateServer("future", "future");
        var key = McpServerCatalogService.BuildServerKey(server.ServerId);
        var payload = JsonSerializer.Serialize(new
        {
            DocumentSchemaId = McpServerRecordSerializer.DocumentSchemaId,
            DocumentSchemaVersion = McpServerRecordSerializer.DocumentSchemaVersion + 1,
            Server = server,
        });
        await context.State.SetValueAsync(key, payload);

        Assert.Empty(await catalog.ListServersAsync());
        Assert.Contains(catalog.LastDiagnostics, diagnostic => diagnostic.StorageKey == key);
        Assert.Equal(payload, await context.State.GetValueAsync(key));
    }

    [Fact]
    public async Task ImportCancellationPropagatesAndDoesNotCreateServers()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        var importer = new McpEcosystemConfigurationImporter(catalog);
        var path = Path.Combine(Path.GetTempPath(), "sunder-mcp-cancel-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """
                { "mcp": { "one": { "type": "remote", "url": "https://example.com/mcp" } } }
                """);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportFileAsync(path, cancellation.Token));
            Assert.Empty(await catalog.ListServersAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SettingsCommands_DisableOverlappingOperationsUntilCancellationSettles()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "one"), Empty, Empty);
        var factory = new FakeConnectionFactory { BlockConnect = true };
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        using var viewModel = CreateSettingsViewModel(catalog, manager);
        await WaitUntilAsync(() => viewModel.SelectedServer is not null);

        var discovery = viewModel.DiscoverToolsCommand.ExecuteAsync(null);
        await factory.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.IsDiscovering);

        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.ImportCommonConfigurationsCommand.CanExecute(null));
        Assert.False(viewModel.DisconnectMcpServerCommand.CanExecute(null));
        viewModel.CancelDiscoveryCommand.Execute(null);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        await discovery;
        Assert.True(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SettingsCatalogChange_WhileBusyReloadsAfterOperationSettles()
    {
        var context = new TestPackageContext();
        var catalog = new McpServerCatalogService(context);
        await catalog.SaveServerAsync(CreateServer("one", "one"), Empty, Empty);
        var factory = new FakeConnectionFactory { BlockConnect = true };
        await using var manager = new McpClientConnectionManager(NullLoggerFactory.Instance, factory);
        using var viewModel = CreateSettingsViewModel(catalog, manager);
        await viewModel.Initialization;

        var discovery = viewModel.DiscoverToolsCommand.ExecuteAsync(null);
        await factory.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.IsBusy);

        await catalog.SaveServerAsync(CreateServer("two", "two"), Empty, Empty);
        Assert.DoesNotContain(viewModel.Servers, server => server.ServerId == "two");

        viewModel.CancelDiscoveryCommand.Execute(null);
        await discovery;
        await WaitUntilAsync(() => viewModel.Servers.Count == 2);

        Assert.Contains(viewModel.Servers, server => server.ServerId == "two");
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string?> ShellProbeEnvironment()
        => new Dictionary<string, string?>
        {
            ["HOME"] = Path.GetTempPath(),
            ["PATH"] = "/usr/bin:/bin",
        };

    private static BlockingShellProbeFixture CreateBlockingShellProbe()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-mcp-shell-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "blocking-shell");
        var processIdsPath = Path.Combine(root, "process-ids");
        File.WriteAllText(scriptPath, $$"""
            #!/bin/sh
            sleep 30 &
            child=$!
            printf '%s %s' "$$" "$child" > '{{processIdsPath}}'
            wait "$child"
            """);
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new BlockingShellProbeFixture(root, scriptPath, processIdsPath, []);
    }

    private static IReadOnlyList<int> ReadProcessIds(string path)
        => File.ReadAllText(path)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => int.Parse(value))
            .ToArray();

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TerminateProcesses(IEnumerable<int> processIds)
    {
        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed record BlockingShellProbeFixture(
        string RootPath,
        string ScriptPath,
        string ProcessIdsPath,
        List<int> ProcessIds);

    private static AgentMcpSettingsViewModel CreateSettingsViewModel(
        McpServerCatalogService catalog,
        McpClientConnectionManager connections)
        => new(
            AgentRoleBoundary.As<McpApp::Sunder.Package.Agent.Mcp.Runtime.IMcpManagementGateway>(new McpLocalManagementGateway(
                new McpSettingsEditorService(catalog),
                new McpConfigurationCoordinator(
                    new McpEcosystemConfigurationImporter(catalog),
                    syncService: null),
                new McpServerConnectionService(catalog, connections, oauthService: null),
                new McpOAuthCoordinator(oauthService: null, connections))));

    private static ConfiguredMcpServerRecord CreateServer(string id, string name)
        => new()
        {
            ServerId = id,
            Name = name,
            DisplayName = name,
            IsEnabled = true,
            TransportType = ConfiguredMcpTransportType.HttpSse,
            EndpointUrl = "https://example.com/mcp",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? maximumWait = null)
    {
        var started = Stopwatch.StartNew();
        var timeout = maximumWait ?? TimeSpan.FromSeconds(3);
        while (!predicate())
        {
            if (started.Elapsed >= timeout)
            {
                throw new TimeoutException("Condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task AcquireAndReleaseAsync(
        McpClientConnectionManager manager,
        ConfiguredMcpServerRecord server,
        McpConnectionScope scope)
    {
        await using var lease = await manager.AcquireClientLeaseAsync(server, Empty, Empty, null, scope);
        Assert.NotNull(lease);
    }

    private sealed class FakeConnectionFactory : IMcpClientConnectionFactory
    {
        public int ConnectCount { get; private set; }
        public bool BlockConnect { get; init; }
        public Exception? DiscoveryFailure { get; init; }
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FakeConnection> Connections { get; } = [];

        public async Task<IMcpClientConnection> ConnectAsync(
            ConfiguredMcpServerRecord server,
            IReadOnlyDictionary<string, string> headers,
            IReadOnlyDictionary<string, string> environmentVariables,
            int? discoveryTimeoutMilliseconds,
            Action<string> standardError,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            ConnectStarted.TrySetResult();
            if (ConnectCount == 2)
            {
                SecondConnectStarted.TrySetResult();
            }
            if (BlockConnect)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var connection = new FakeConnection(DiscoveryFailure);
            Connections.Add(connection);
            return connection;
        }
    }

    private sealed class FakeConnection(Exception? discoveryFailure = null) : IMcpClientConnection
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public McpClient? Client => null;
        public Task Completion => _completion.Task;
        public int DisposeCount { get; private set; }

        public Task<IReadOnlyList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken)
            => discoveryFailure is null
                ? Task.FromResult<IReadOnlyList<McpClientTool>>([])
                : Task.FromException<IReadOnlyList<McpClientTool>>(discoveryFailure);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InvokableConnectionFactory(
        Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, ValueTask<CallToolResult>> invoke) : IMcpClientConnectionFactory
    {
        public Task<IMcpClientConnection> ConnectAsync(
            ConfiguredMcpServerRecord server,
            IReadOnlyDictionary<string, string> headers,
            IReadOnlyDictionary<string, string> environmentVariables,
            int? discoveryTimeoutMilliseconds,
            Action<string> standardError,
            CancellationToken cancellationToken)
            => Task.FromResult<IMcpClientConnection>(new InvokableConnection(invoke));
    }

    private sealed class InvokableConnection(
        Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, ValueTask<CallToolResult>> invoke) : IMcpClientConnection
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public McpClient? Client => null;
        public bool CanInvokeTools => true;
        public Task Completion => _completion.Task;

        public Task<IReadOnlyList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<McpClientTool>>([]);

        public ValueTask<CallToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
            => invoke(toolName, arguments, cancellationToken);

        public ValueTask DisposeAsync()
        {
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LateConnectionFactory : IMcpClientConnectionFactory
    {
        public int ConnectCount { get; private set; }
        public FakeConnection Connection { get; } = new();
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseConnection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IMcpClientConnection> ConnectAsync(
            ConfiguredMcpServerRecord server,
            IReadOnlyDictionary<string, string> headers,
            IReadOnlyDictionary<string, string> environmentVariables,
            int? discoveryTimeoutMilliseconds,
            Action<string> standardError,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            using var registration = cancellationToken.Register(
                () => CancellationObserved.TrySetResult());
            ConnectStarted.TrySetResult();
            await ReleaseConnection.Task;
            return Connection;
        }
    }

    private sealed class TestPackageContext : IPackageContext
    {
        private readonly TestStorage _storage;

        public TestPackageContext(TestState? state = null, FaultingSecrets? secrets = null)
        {
            State = state ?? new TestState();
            Secrets = secrets ?? new FaultingSecrets();
            _storage = new TestStorage(State);
        }

        public TestState State { get; }
        public FaultingSecrets Secrets { get; }
        public string PackageId => "test.mcp";
        public string Version { get; } = "1.0.0";
        public string ContentRootPath => AppContext.BaseDirectory;
        public IPackageStorageContext Storage => _storage;
        public IPackageSettings Settings { get; } = new EmptySettings();
        IPackageSecrets IPackageContext.Secrets => Secrets;
        public IPackageLogging Logging { get; } = NullPackageLogging.Instance;
    }

    private sealed class McpHandlerManagementClient(McpRuntimeHandler handler) : IMcpOwnerManagementClient
    {
        public ValueTask<McpProjection> QueryAsync(
            McpQuery query,
            CancellationToken cancellationToken = default)
            => handler.HandleAsync(query, cancellationToken);

        public ValueTask<McpProjection> CommandAsync(
            McpCommand command,
            CancellationToken cancellationToken = default)
            => handler.HandleAsync(command, cancellationToken);

        public IAsyncEnumerable<McpChanged> WatchChangesAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestStorage(IPackageKeyValueStore state) : IPackageStorageContext
    {
        public IPackageFileStore Files { get; } = new TestFiles();
        public IPackageKeyValueStore State { get; } = state;
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = new TestPackageRoleLocalWorkspace(Path.GetTempPath());
    }

    private sealed class TestFiles : TestPackageFileStoreBase
    {
        public TestFiles() : base(Path.GetTempPath()) { }
    }

    private sealed class TestState : IPackageKeyValueStore, IPackageStorageKeyMigrator
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        public int SetCount { get; private set; }
        public bool ThrowOnNextSet { get; set; }
        public bool BlockOnNextSet { get; set; }
        public TaskCompletionSource SetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSet { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            SetCount++;
            if (ThrowOnNextSet)
            {
                ThrowOnNextSet = false;
                throw new InvalidOperationException("Injected state failure.");
            }

            if (BlockOnNextSet)
            {
                BlockOnNextSet = false;
                SetStarted.TrySetResult();
                await ReleaseSet.Task.WaitAsync(cancellationToken);
            }

            _values[key] = value;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.ContainsKey(key));
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Prefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>(_values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
        }

        public Task MigrateKeysAsync(
            IReadOnlyList<PackageStorageKeyMigration> migrations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ApplyKeyMigrations(_values, migrations);
            if (result.Changed)
            {
                _values.Clear();
                foreach (var pair in result.Values)
                {
                    _values[pair.Key] = pair.Value;
                }
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingSecrets : IPackageSecrets, IPackageStorageKeyMigrator
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public int SetCount { get; private set; }
        public int? FailOnSetNumber { get; set; }
        public bool BlockReads { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<string> Keys => _values.Keys;
        public void Seed(string key, string value) => _values[key] = value;
        public string? GetRaw(string key) => _values.GetValueOrDefault(key);
        public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            if (BlockReads)
            {
                ReadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return _values.GetValueOrDefault(key);
        }

        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            SetCount++;
            if (SetCount == FailOnSetNumber)
            {
                return Task.FromException(new InvalidOperationException("Injected secret failure."));
            }

            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task MigrateKeysAsync(
            IReadOnlyList<PackageStorageKeyMigration> migrations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ApplyKeyMigrations(_values, migrations);
            if (result.Changed)
            {
                _values.Clear();
                foreach (var pair in result.Values)
                {
                    _values[pair.Key] = pair.Value;
                }
            }
            return Task.CompletedTask;
        }
    }

    private static (Dictionary<string, string> Values, bool Changed) ApplyKeyMigrations(
        IEnumerable<KeyValuePair<string, string>> values,
        IReadOnlyList<PackageStorageKeyMigration> migrations)
    {
        var source = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var mutations = new List<(string Source, string? Destination, string Value, int Precedence, bool Cleanup)>();
        foreach (var pair in source)
        {
            PackageStorageKeyMigrationAction? resolved = null;
            var cleanup = false;
            foreach (var migration in migrations)
            {
                var candidate = migration.Resolve(pair.Key, pair.Value);
                if (candidate.Kind == PackageStorageKeyMigrationActionKind.NoMatch)
                {
                    continue;
                }
                if (resolved is not null)
                {
                    throw new InvalidOperationException("Legacy package key matches multiple migration rules.");
                }
                resolved = candidate;
                cleanup = migration.IsDynamicCleanup;
            }
            if (resolved is null)
            {
                if (!PackageStorageValidation.IsValidKey(pair.Key))
                {
                    throw new InvalidDataException($"Stored package key '{pair.Key}' is invalid and has no declared migration.");
                }
                continue;
            }

            mutations.Add((
                pair.Key,
                resolved.DestinationKey,
                pair.Value,
                resolved.Precedence,
                cleanup));
        }

        if (mutations.Count == 0)
        {
            return (source, false);
        }
        foreach (var mutation in mutations)
        {
            source.Remove(mutation.Source);
        }
        foreach (var group in mutations
                     .Where(mutation => mutation.Destination is not null)
                     .GroupBy(mutation => mutation.Destination!, StringComparer.Ordinal))
        {
            var rewrites = group.ToArray();
            if (rewrites.Any(rewrite => !rewrite.Cleanup))
            {
                var expected = rewrites[0].Value;
                if (rewrites.Any(rewrite => !string.Equals(rewrite.Value, expected, StringComparison.Ordinal))
                    || source.TryGetValue(group.Key, out var existing)
                    && !string.Equals(existing, expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Package storage key migration collision at '{group.Key}' contains nonidentical values.");
                }
                source[group.Key] = expected;
                continue;
            }

            if (source.ContainsKey(group.Key))
            {
                continue;
            }
            var precedence = rewrites.Max(rewrite => rewrite.Precedence);
            var preferred = rewrites
                .Where(rewrite => rewrite.Precedence == precedence)
                .Select(rewrite => rewrite.Value)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (preferred.Length == 1)
            {
                source[group.Key] = preferred[0];
            }
        }
        if (source.Any(pair => !PackageStorageValidation.IsValidKey(pair.Key)
                               || !PackageStorageValidation.IsValidValue(pair.Value)))
        {
            throw new InvalidDataException("Migrated package storage is invalid.");
        }
        return (source, true);
    }

    private sealed class EmptySettings : EmptyPackageSettings;
}
