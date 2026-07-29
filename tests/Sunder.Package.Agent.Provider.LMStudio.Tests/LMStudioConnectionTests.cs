using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Provider.LMStudio.Tests;

public sealed class LMStudioConnectionTests
{
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///tmp/lmstudio")]
    [InlineData("http://localhost:1234/v1?tenant=a")]
    [InlineData("http://user:password@localhost:1234/v1")]
    public async Task InvalidUri_IsRejectedAtSettingsAndReadinessBoundaries(string invalidUrl)
    {
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = LMStudioProviderConfiguration.DefaultBaseUrl,
            });
        var viewModel = new LMStudioSettingsViewModel(context);
        await viewModel.InitializeAsync();
        viewModel.BaseUrl = invalidUrl;

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Invalid", viewModel.ConnectionStatusLabel);
        Assert.Equal(
            LMStudioProviderConfiguration.DefaultBaseUrl,
            await context.Settings.GetStoredValueAsync(LMStudioProviderConfiguration.BaseUrlKey));

        await context.Settings.SetValueAsync(LMStudioProviderConfiguration.BaseUrlKey, invalidUrl);
        using var provider = new LMStudioAgentProvider(context);
        var readiness = await provider.GetReadinessAsync();

        Assert.Equal(AgentProviderReadinessStatus.NeedsConfiguration, readiness.Status);
        Assert.Contains("invalid", readiness.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EndpointAndCredentialRotation_UsesCurrentSnapshotWithOneHandler()
    {
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "https://first.test/v1",
            },
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.ApiKeyKey] = "first-key",
            });
        var handler = new LMStudioTestHttpHandler((_, _, _) => LMStudioTestHttpHandler.Json("{\"data\":[]}"));
        using var connection = new LMStudioConnection(context, handler);
        var catalog = new LMStudioModelCatalogService(connection);

        Assert.True((await catalog.GetCatalogAsync()).IsSuccess);
        await context.Settings.SetValueAsync(LMStudioProviderConfiguration.BaseUrlKey, "https://second.test/api/v1/");
        await context.Secrets.SetSecretAsync(LMStudioProviderConfiguration.ApiKeyKey, "second-key");
        Assert.True((await catalog.GetCatalogAsync()).IsSuccess);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://first.test/v1/models", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("first-key", handler.Requests[0].Authorization?.Parameter);
        Assert.Equal("https://second.test/api/v1/models", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal("second-key", handler.Requests[1].Authorization?.Parameter);
    }

    [Fact]
    public async Task ChatClientDisposal_DoesNotDisposePackageConnection()
    {
        var context = CreateContext();
        var handler = new LMStudioTestHttpHandler((_, _, _) => LMStudioTestHttpHandler.Json("{\"data\":[]}"));
        using var connection = new LMStudioConnection(context, handler);
        using (var client = new LMStudioChatClient(
                   new AgentChatClientContext("lmstudio", "lmstudio/model"),
                   connection))
        {
        }

        var result = await new LMStudioModelCatalogService(connection).GetCatalogAsync();

        Assert.True(result.IsSuccess);
        Assert.False(handler.IsDisposed);
    }

    [Fact]
    public void ConnectionDisposal_DisposesOwnedHandler()
    {
        var handler = new LMStudioTestHttpHandler((_, _, _) => LMStudioTestHttpHandler.Json("{\"data\":[]}"));
        var connection = new LMStudioConnection(CreateContext(), handler);

        connection.Dispose();

        Assert.True(handler.IsDisposed);
    }

    [Fact]
    public async Task OptionalBearerKey_Clear_RemovesItFromFutureConnectionSnapshots()
    {
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = LMStudioProviderConfiguration.DefaultBaseUrl,
            },
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.ApiKeyKey] = "local-key",
            });
        var credentials = new ProviderCredentialAccessor(context.Secrets, LMStudioProviderConfiguration.ApiKeyKey);
        using var settings = new LMStudioSettingsViewModel(context, credentials);
        using var connection = new LMStudioConnection(
            context,
            credentials,
            new LMStudioTestHttpHandler((_, _, _) => LMStudioTestHttpHandler.Json("{\"data\":[]}")));
        await settings.InitializeAsync();

        Assert.Equal("local-key", (await connection.GetRequiredOptionsAsync()).ApiKey);
        settings.ApiKeySettings.RequestClearCredentialCommand.Execute(null);
        await settings.ApiKeySettings.ClearCredentialCommand.ExecuteAsync(null);

        Assert.Null(await context.Secrets.GetSecretAsync(LMStudioProviderConfiguration.ApiKeyKey));
        Assert.Null((await connection.GetRequiredOptionsAsync()).ApiKey);
    }

    [Fact]
    public async Task FirstOpenRuntimeFailure_IsContainedAndRetryHydratesAuthoritativeSettings()
    {
        var settings = new FailOnceKeyValueStore(
            new PackageRuntimeInvocationException(
                "runtime.v1.unavailable",
                isTransient: true,
                statusCode: 503,
                correlationId: "lm-retry"),
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "https://lmstudio.test/v1",
                [LMStudioProviderConfiguration.UtilityModelKey] = "lmstudio/utility",
            });
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.lmstudio",
            settings: settings);
        using var viewModel = new LMStudioSettingsViewModel(context);

        var prepared = await viewModel.PrepareNavigationAsync(CreateNavigationContext());

        Assert.True(prepared);
        Assert.True(viewModel.IsError);
        Assert.False(viewModel.CanMutate);
        Assert.False(viewModel.SaveSettingsCommand.CanExecute(null));
        Assert.Equal("runtime.v1.unavailable", viewModel.RuntimeErrorCode);
        Assert.Equal("lm-retry", viewModel.RuntimeCorrelationId);

        await viewModel.RetryInitializationCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsReady);
        Assert.True(viewModel.CanMutate);
        Assert.Equal("https://lmstudio.test/v1", viewModel.BaseUrl);
        Assert.Equal("lmstudio/utility", viewModel.UtilityModelId);
        Assert.Null(viewModel.RuntimeErrorCode);
        Assert.Equal(3, settings.ReadCount);
    }

    [Fact]
    public async Task FirstOpenTransportFailure_IsContainedWithSafeDiagnosticAndCanRetry()
    {
        var settings = new FailOnceKeyValueStore(
            new IOException("sensitive transport detail"),
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "https://lmstudio.test/v1",
            });
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.lmstudio",
            settings: settings);
        using var viewModel = new LMStudioSettingsViewModel(context);

        Assert.True(await viewModel.PrepareNavigationAsync(CreateNavigationContext()));

        Assert.True(viewModel.IsError);
        Assert.Equal("lmstudio.settings.load-failed", viewModel.RuntimeErrorCode);
        Assert.NotNull(viewModel.RuntimeCorrelationId);
        Assert.DoesNotContain("sensitive", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        await viewModel.RetryInitializationCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsReady);
        Assert.Equal("https://lmstudio.test/v1", viewModel.BaseUrl);
    }

    [Fact]
    public async Task CanceledNavigationRefresh_RestoresCachedReadyStateAndNextRefreshReenters()
    {
        var settings = new CancelNextReadKeyValueStore(new Dictionary<string, string>
        {
            [LMStudioProviderConfiguration.BaseUrlKey] = "https://first.test/v1",
        });
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.lmstudio",
            settings: settings);
        using var viewModel = new LMStudioSettingsViewModel(context);
        await viewModel.InitializeAsync();
        await settings.SetValueAsync(
            LMStudioProviderConfiguration.BaseUrlKey,
            "https://second.test/v1");
        settings.CancelNextRead();
        using var cancellation = new CancellationTokenSource();

        var canceledRefresh = viewModel.PrepareNavigationAsync(
            CreateNavigationContext(),
            cancellation.Token).AsTask();
        await settings.CanceledReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRefresh);
        Assert.True(viewModel.IsReady);
        Assert.Equal("https://first.test/v1", viewModel.BaseUrl);

        Assert.True(await viewModel.PrepareNavigationAsync(CreateNavigationContext()));
        Assert.True(viewModel.IsReady);
        Assert.Equal("https://second.test/v1", viewModel.BaseUrl);
    }

    [Fact]
    public async Task ImmediateReentryDuringCancellationUnwind_RestartsAuthoritativeRefresh()
    {
        var settings = new CancellationUnwindKeyValueStore(new Dictionary<string, string>
        {
            [LMStudioProviderConfiguration.BaseUrlKey] = "https://first.test/v1",
        });
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.lmstudio",
            settings: settings);
        using var viewModel = new LMStudioSettingsViewModel(context);
        await viewModel.InitializeAsync();
        await settings.SetValueAsync(
            LMStudioProviderConfiguration.BaseUrlKey,
            "https://second.test/v1");
        settings.BlockNextRead();
        using var cancellation = new CancellationTokenSource();

        var canceledRefresh = viewModel.PrepareNavigationAsync(
            CreateNavigationContext(),
            cancellation.Token).AsTask();
        await settings.BlockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await settings.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var reentry = viewModel.PrepareNavigationAsync(CreateNavigationContext()).AsTask();
        Assert.False(reentry.IsCompleted);
        settings.ReleaseCancellationUnwind.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRefresh);
        Assert.True(await reentry);
        Assert.True(viewModel.IsReady);
        Assert.Equal("https://second.test/v1", viewModel.BaseUrl);
    }

    [Fact]
    public async Task Disposal_CancelsBlockedInitializationWithoutLateApply()
    {
        var settings = new BlockingKeyValueStore(new Dictionary<string, string>
        {
            [LMStudioProviderConfiguration.BaseUrlKey] = "https://late.test/v1",
        });
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.lmstudio",
            settings: settings);
        var viewModel = new LMStudioSettingsViewModel(context);

        var initialization = viewModel.InitializeAsync();
        await settings.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.CanMutate);
        viewModel.Dispose();
        settings.Release.TrySetResult();
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(LMStudioProviderConfiguration.DefaultBaseUrl, viewModel.BaseUrl);
        Assert.False(viewModel.IsReady);
    }

    [Fact]
    public void AuthenticatedNonLoopbackHttp_IsRejectedButLoopbackHttpIsAllowed()
    {
        Assert.False(LMStudioConnectionOptions.TryCreate(
            "http://lmstudio.test/v1",
            "secret",
            TimeSpan.FromSeconds(1),
            out _,
            out var error));
        Assert.Contains("HTTPS", error, StringComparison.Ordinal);

        Assert.True(LMStudioConnectionOptions.TryCreate(
            "http://127.0.0.1:1234/v1",
            "secret",
            TimeSpan.FromSeconds(1),
            out _,
            out _));
    }

    private static ProviderTestPackageContext CreateContext()
        => new(
            "sunder.package.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "http://lmstudio.test/v1",
            });

    private static PackageViewNavigationContext CreateNavigationContext()
        => new(
            "settings:sunder.package.agent.provider.lmstudio",
            new Dictionary<string, string?>());

    private sealed class FailOnceKeyValueStore(
        Exception failure,
        IReadOnlyDictionary<string, string> values) : IPackageKeyValueStore
    {
        private readonly ProviderTestKeyValueStore _inner = new(values);
        private int _readCount;

        internal int ReadCount => Volatile.Read(ref _readCount);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                return Task.FromException<string?>(failure);
            }
            return _inner.GetValueAsync(key, cancellationToken);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
            => _inner.SetValueAsync(key, value, cancellationToken);

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
            => _inner.ContainsKeyAsync(key, cancellationToken);

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
            => _inner.DeleteValueAsync(key, cancellationToken);

        public Task<IReadOnlyList<string>> ListKeysAsync(
            string? prefix = null,
            CancellationToken cancellationToken = default)
            => _inner.ListKeysAsync(prefix, cancellationToken);
    }

    private sealed class BlockingKeyValueStore(IReadOnlyDictionary<string, string> values) : IPackageKeyValueStore
    {
        private readonly ProviderTestKeyValueStore _inner = new(values);
        private int _blocked;

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Started.TrySetResult();
                await Release.Task;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return await _inner.GetValueAsync(key, cancellationToken);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
            => _inner.SetValueAsync(key, value, cancellationToken);

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
            => _inner.ContainsKeyAsync(key, cancellationToken);

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
            => _inner.DeleteValueAsync(key, cancellationToken);

        public Task<IReadOnlyList<string>> ListKeysAsync(
            string? prefix = null,
            CancellationToken cancellationToken = default)
            => _inner.ListKeysAsync(prefix, cancellationToken);
    }

    private sealed class CancelNextReadKeyValueStore(IReadOnlyDictionary<string, string> values) : IPackageKeyValueStore
    {
        private readonly ProviderTestKeyValueStore _inner = new(values);
        private int _cancelNextRead;

        internal TaskCompletionSource CanceledReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void CancelNextRead() => Interlocked.Exchange(ref _cancelNextRead, 1);

        public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _cancelNextRead, 0) != 0)
            {
                CanceledReadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return await _inner.GetValueAsync(key, cancellationToken);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
            => _inner.SetValueAsync(key, value, cancellationToken);

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
            => _inner.ContainsKeyAsync(key, cancellationToken);

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
            => _inner.DeleteValueAsync(key, cancellationToken);

        public Task<IReadOnlyList<string>> ListKeysAsync(
            string? prefix = null,
            CancellationToken cancellationToken = default)
            => _inner.ListKeysAsync(prefix, cancellationToken);
    }

    private sealed class CancellationUnwindKeyValueStore(IReadOnlyDictionary<string, string> values) : IPackageKeyValueStore
    {
        private readonly ProviderTestKeyValueStore _inner = new(values);
        private int _blockNextRead;

        internal TaskCompletionSource BlockedReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseCancellationUnwind { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void BlockNextRead() => Interlocked.Exchange(ref _blockNextRead, 1);

        public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blockNextRead, 0) != 0)
            {
                BlockedReadStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved.TrySetResult();
                    await ReleaseCancellationUnwind.Task;
                    throw;
                }
            }
            return await _inner.GetValueAsync(key, cancellationToken);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
            => _inner.SetValueAsync(key, value, cancellationToken);

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
            => _inner.ContainsKeyAsync(key, cancellationToken);

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
            => _inner.DeleteValueAsync(key, cancellationToken);

        public Task<IReadOnlyList<string>> ListKeysAsync(
            string? prefix = null,
            CancellationToken cancellationToken = default)
            => _inner.ListKeysAsync(prefix, cancellationToken);
    }
}
