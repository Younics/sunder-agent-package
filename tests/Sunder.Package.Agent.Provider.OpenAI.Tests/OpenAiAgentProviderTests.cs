using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class OpenAiAgentProviderTests
{
    [Fact]
    public async Task CreateChatClientAsync_ApiKeyMode_UsesOfficialResponsesClientEvenWhenCodexSessionExists()
    {
        var packageContext = new TestPackageContext(
            new Dictionary<string, string>
            {
                ["auth.mode"] = OpenAiAuthMode.ApiKey,
            },
            new Dictionary<string, string>
            {
                ["auth.apiKey"] = "sk-test",
                ["auth.codex.session"] = JsonSerializer.Serialize(new OpenAiCodexSession(
                    "access-token",
                    "refresh-token",
                    DateTimeOffset.UtcNow.AddHours(1),
                    "account-id")),
            });
        var provider = CreateProvider(packageContext);

        var client = await provider.CreateChatClientAsync(new AgentChatClientContext("openai", "openai/gpt-5.5"));

        Assert.Contains("OpenAIResponsesChatClient", client.GetType().FullName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateChatClientAsync_CodexMode_UsesCodexClientEvenWhenApiKeyExists()
    {
        var packageContext = new TestPackageContext(
            new Dictionary<string, string>
            {
                ["auth.mode"] = OpenAiAuthMode.CodexConnected,
            },
            new Dictionary<string, string>
            {
                ["auth.apiKey"] = "sk-test",
                ["auth.codex.session"] = JsonSerializer.Serialize(new OpenAiCodexSession(
                    "access-token",
                    "refresh-token",
                    DateTimeOffset.UtcNow.AddHours(1),
                    "account-id")),
            });
        var provider = CreateProvider(packageContext);

        var client = await provider.CreateChatClientAsync(new AgentChatClientContext("openai", "openai/gpt-5.5"));

        Assert.IsType<OpenAiCodexChatClient>(client);
    }

    [Fact]
    public void ExtractChatGptAccountId_ReadsTopLevelClaim()
    {
        var jwt = CreateJwt(new Dictionary<string, object?>
        {
            ["chatgpt_account_id"] = "account-top-level",
        });

        Assert.Equal("account-top-level", CodexConnectedAuthStrategy.ExtractChatGptAccountId(jwt));
    }

    [Fact]
    public void ExtractChatGptAccountId_ReadsOpenAiAuthClaim()
    {
        var jwt = CreateJwt(new Dictionary<string, object?>
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "account-auth-claim",
            },
        });

        Assert.Equal("account-auth-claim", CodexConnectedAuthStrategy.ExtractChatGptAccountId(jwt));
    }

    [Fact]
    public void ExtractChatGptAccountId_FallsBackToFirstOrganizationId()
    {
        var jwt = CreateJwt(new Dictionary<string, object?>
        {
            ["organizations"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "organization-id",
                },
            },
        });

        Assert.Equal("organization-id", CodexConnectedAuthStrategy.ExtractChatGptAccountId(jwt));
    }

    private static OpenAiAgentProvider CreateProvider(TestPackageContext packageContext)
    {
        var codexConnectedAuthStrategy = new CodexConnectedAuthStrategy(packageContext);
        var codexConnectedTransport = new CodexConnectedTransport(
            codexConnectedAuthStrategy,
            new HttpClient { BaseAddress = new Uri("https://chatgpt.com/backend-api/") });
        return new OpenAiAgentProvider(
            new ApiKeyAuthStrategy(packageContext),
            codexConnectedAuthStrategy,
            codexConnectedTransport,
            packageContext);
    }

    private static string CreateJwt(IReadOnlyDictionary<string, object?> claims)
        => Base64UrlEncode("{}") + "." + Base64UrlEncode(JsonSerializer.Serialize(claims)) + ".signature";

    private static string Base64UrlEncode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class TestPackageContext(
        IReadOnlyDictionary<string, string> configurationValues,
        IReadOnlyDictionary<string, string> secretValues) : IPackageContext
    {
        public string PackageId { get; } = "sunder.package.agent.provider.openai";

        public Version Version { get; } = new(1, 0, 0);

        public string InstallPath { get; } = AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestPackageStorageContext();

        public IPackageConfiguration Configuration { get; } = new TestPackageConfiguration(configurationValues);

        public IPackageSecrets Secrets { get; } = new TestPackageSecrets(secretValues);

        public ILoggerFactory LoggerFactory => Logging.LoggerFactory;

        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } = Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    private sealed class TestPackageConfiguration(IReadOnlyDictionary<string, string> values) : IPackageConfiguration
    {
        public string? GetValue(string key) => values.TryGetValue(key, out var value) ? value : null;
    }

    private sealed class TestPackageSecrets(IReadOnlyDictionary<string, string> values) : IPackageSecrets
    {
        private readonly Dictionary<string, string> _values = new(values, StringComparer.OrdinalIgnoreCase);

        public string? GetSecret(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public void SetSecret(string key, string value) => _values[key] = value;

        public void DeleteSecret(string key) => _values.Remove(key);
    }

    private sealed class TestPackageStorageContext : IPackageStorageContext
    {
        public string DataRootPath { get; } = AppContext.BaseDirectory;

        public string CacheRootPath { get; } = AppContext.BaseDirectory;

        public string LogsRootPath { get; } = AppContext.BaseDirectory;

        public IPackageFileStore Files => throw new NotSupportedException();

        public IPackageKeyValueStore State => throw new NotSupportedException();
    }
}
