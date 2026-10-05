using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Rpc;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class OpenAiManagementRpcFaultTests
{
    private const string UnexpectedMessage = "openai-owner-secret-canary";

    [Fact]
    public async Task CredentialStorageFailure_RemainsAnUnexpectedProviderFault()
    {
        var secrets = new ThrowingSecrets();
        using var auth = new CodexConnectedAuthStrategy(new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            secrets: secrets));
        var provider = new ManagementService(new OpenAiRuntimeManagementClient(
            new OpenAiAuthOperationHandler(auth),
            new ProviderCredentialAccessor(secrets, "openai.api-key")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetCredentialStatusAsync(new ManagementQuery()).AsTask());

        Assert.Equal(UnexpectedMessage, exception.Message);
        Assert.IsNotType<SunderRpcException>(exception);
    }

    [Fact]
    public async Task AuthStorageFailure_IsNotConvertedToAStatusMessage()
    {
        var secrets = new ThrowingSecrets();
        using var auth = new CodexConnectedAuthStrategy(new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            secrets: secrets));
        var provider = new ManagementService(new OpenAiRuntimeManagementClient(
            new OpenAiAuthOperationHandler(auth),
            new ProviderCredentialAccessor(secrets, "openai.api-key")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetAuthStatusAsync(new ManagementQuery()).AsTask());

        Assert.Equal(UnexpectedMessage, exception.Message);
        Assert.IsNotType<SunderRpcException>(exception);
    }

    [Fact]
    public async Task EmptyCredential_RemainsADeclaredDomainFailure()
    {
        var secrets = new ThrowingSecrets();
        using var auth = new CodexConnectedAuthStrategy(new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            secrets: secrets));
        var provider = new ManagementService(new OpenAiRuntimeManagementClient(
            new OpenAiAuthOperationHandler(auth),
            new ProviderCredentialAccessor(secrets, "openai.api-key")));

        var exception = await Assert.ThrowsAsync<SunderRpcException>(() =>
            provider.SetCredentialAsync(
                new CredentialRequest { Credential = new() { Value = " " } },
                CancellationToken.None).AsTask());

        Assert.Equal(SunderRpcErrorKind.Domain, exception.Error.Kind);
        Assert.Equal("openai.management.failed", exception.Error.Code);
        Assert.DoesNotContain(UnexpectedMessage, exception.Error.Message, StringComparison.Ordinal);
    }

    private sealed class ThrowingSecrets : IPackageSecrets
    {
        public Task<string?> GetSecretAsync(
            string key,
            CancellationToken cancellationToken = default)
            => Task.FromException<string?>(new InvalidOperationException(UnexpectedMessage));

        public Task SetSecretAsync(
            string key,
            string value,
            CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException(UnexpectedMessage));

        public Task DeleteSecretAsync(
            string key,
            CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException(UnexpectedMessage));
    }
}
