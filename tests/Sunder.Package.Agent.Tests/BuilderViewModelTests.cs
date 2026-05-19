using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderViewModelTests
{
    [Fact]
    public async Task DeleteSelectedProjectAsync_WhenCompactLayout_ClearsSelectionAndReturnsToList()
    {
        var viewModel = CreateViewModel();
        var deletedProject = CreateProject("one", "One Package");
        var remainingProject = CreateProject("two", "Two Package");
        viewModel.Projects.Add(deletedProject);
        viewModel.Projects.Add(remainingProject);
        viewModel.IsCompactLayout = true;
        viewModel.ActivateProject(deletedProject);

        await viewModel.DeleteSelectedProjectAsync();

        Assert.DoesNotContain(deletedProject, viewModel.Projects);
        Assert.Contains(remainingProject, viewModel.Projects);
        Assert.Null(viewModel.SelectedProject);
        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactList);
        Assert.Equal(string.Empty, viewModel.StatusText);
        Assert.False(viewModel.ShowStatusMessage);
    }

    [Fact]
    public async Task DeleteSelectedProjectAsync_WhenWideLayout_SelectsNextProjectAndShowsSuccess()
    {
        var viewModel = CreateViewModel();
        var deletedProject = CreateProject("one", "One Package");
        var remainingProject = CreateProject("two", "Two Package");
        viewModel.Projects.Add(deletedProject);
        viewModel.Projects.Add(remainingProject);
        viewModel.ActivateProject(deletedProject);

        await viewModel.DeleteSelectedProjectAsync();

        Assert.DoesNotContain(deletedProject, viewModel.Projects);
        Assert.Same(remainingProject, viewModel.SelectedProject);
        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowEditorPane);
        Assert.Equal("Deleted package project 'One Package'.", viewModel.StatusText);
        Assert.True(viewModel.ShowStatusMessage);
    }

    private static BuilderViewModel CreateViewModel()
    {
        var packageContext = new TestPackageContext();
        return new BuilderViewModel(
            new BuilderSetupService(),
            new BuilderProjectStore(packageContext),
            NullPackageSessionService.Instance,
            new TestBackgroundProcessQueue());
    }

    private static BuilderProjectViewModel CreateProject(string id, string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        return new BuilderProjectViewModel(new BuilderProjectRecord(
            id,
            displayName,
            $"local.{id}",
            $"/tmp/{id}",
            $"/tmp/{id}/bin/Debug/net10.0/sunder-dev",
            Watch: true,
            now,
            now));
    }

    private sealed class TestPackageContext : IPackageContext
    {
        public string PackageId => "local.test.builder";

        public Version Version { get; } = new(1, 0, 0);

        public string InstallPath => AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestStorageContext();

        public IPackageConfiguration Configuration { get; } = new TestConfiguration();

        public IPackageSecrets Secrets { get; } = new TestSecrets();

        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

        public IPackageLogging Logging { get; } = NullPackageLogging.Instance;
    }

    private sealed class TestStorageContext : IPackageStorageContext
    {
        public string DataRootPath => AppContext.BaseDirectory;

        public string CacheRootPath => AppContext.BaseDirectory;

        public string LogsRootPath => AppContext.BaseDirectory;

        public IPackageFileStore Files { get; } = new TestFileStore();

        public IPackageKeyValueStore State { get; } = new TestKeyValueStore();
    }

    private sealed class TestFileStore : IPackageFileStore
    {
        public string RootPath => AppContext.BaseDirectory;

        public string GetPath(string relativePath)
            => string.IsNullOrWhiteSpace(relativePath)
                ? RootPath
                : Path.Combine([RootPath, .. relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);
    }

    private sealed class TestKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? GetValue(string key) => _values.GetValueOrDefault(key);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(GetValue(key));

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.ContainsKey(key));

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_values.Keys.Where(key => prefix is null || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private sealed class TestConfiguration : IPackageConfiguration
    {
        public string? GetValue(string key) => null;
    }

    private sealed class TestSecrets : IPackageSecrets
    {
        public string? GetSecret(string key) => null;

        public void SetSecret(string key, string value)
        {
        }

        public void DeleteSecret(string key)
        {
        }
    }

    private sealed class TestBackgroundProcessQueue : IBackgroundProcessQueue
    {
        public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged
        {
            add { }
            remove { }
        }

        public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request)
            => new(
                Guid.NewGuid(),
                request.Title,
                request.GroupKey,
                request.Indicator,
                request.ConcurrencyMode,
                BackgroundProcessState.Queued,
                string.Empty,
                ProgressPercent: null,
                request.CanCancel,
                request.Metadata ?? new Dictionary<string, string>(),
                ErrorMessage: null,
                DateTimeOffset.UtcNow,
                StartedAtUtc: null,
                CompletedAtUtc: null);

        public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null) => [];

        public bool Cancel(Guid processId) => false;
    }
}
