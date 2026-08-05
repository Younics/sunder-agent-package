using Sunder.Package.Agent.Skills.PackageViews;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.Skills.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SkillSettingsRaceTests
{
    [Fact]
    public async Task RuntimeRefresh_StaleInitializationSnapshotCannotOverwriteLatestRows()
    {
        var gateway = new BlockingSkillGateway();
        using var viewModel = new SkillSettingsViewModel(gateway);
        await gateway.First.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var freshApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.Skills.CollectionChanged += (_, _) =>
        {
            if (viewModel.Skills.SingleOrDefault()?.DisplayName == "Fresh skill")
            {
                freshApplied.TrySetResult();
            }
        };

        gateway.RaiseSkillsChanged();
        await gateway.Second.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        gateway.Second.Response.TrySetResult([Skill("fresh", "Fresh skill")]);
        await freshApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));

        gateway.First.Response.TrySetResult([Skill("stale", "Stale skill")]);
        await viewModel.InitializeAsync();

        var skill = Assert.Single(viewModel.Skills);
        Assert.Equal("fresh", skill.SkillId);
        Assert.Equal("Fresh skill", skill.DisplayName);
    }

    [Fact]
    public async Task DeleteThenCreate_DelayedDeleteSnapshotCannotRemoveCreatedSelection()
    {
        var gateway = new DeleteCreateRaceSkillGateway();
        using var viewModel = new SkillSettingsViewModel(gateway);
        await viewModel.InitializeAsync();
        Assert.Equal("deleted", viewModel.SelectedSkill?.SkillId);

        await viewModel.DeleteSelectedSkillCommand.ExecuteAsync(null);
        await gateway.DelayedDeleteSnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.ImportLocalFolderAsync("/ignored");
        var created = Assert.Single(viewModel.Skills);
        Assert.Equal("created", created.SkillId);
        Assert.Same(created, viewModel.SelectedSkill);

        gateway.ReleaseDelayedDeleteSnapshot.TrySetResult();
        await viewModel.RuntimeRefreshIdle.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(created, Assert.Single(viewModel.Skills));
        Assert.Same(created, viewModel.SelectedSkill);
    }

    [Fact]
    public async Task ImportCompletionAndRetryAfterDisposalAreIgnored()
    {
        var gateway = new BlockingImportSkillGateway();
        var viewModel = new SkillSettingsViewModel(gateway);
        await viewModel.InitializeAsync();

        var import = viewModel.ImportLocalFolderAsync("/first");
        await gateway.ImportStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.Dispose();

        await viewModel.ImportLocalFolderAsync("/after-disposal");
        Assert.Equal(1, gateway.ImportCount);

        gateway.ReleaseImport.TrySetResult();
        await import.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(viewModel.Skills);
        Assert.Empty(viewModel.StatusText);
    }

    private static InstalledSkillRecord Skill(string id, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new InstalledSkillRecord(
            id,
            id,
            name,
            null,
            null,
            null,
            "local",
            null,
            null,
            null,
            id,
            now,
            now,
            new Dictionary<string, string>(),
            []);
    }

    private sealed class BlockingSkillGateway : ISkillManagementGateway
    {
        private int _listCount;

        public event Action? SkillsChanged;

        public PendingList First { get; } = new();

        public PendingList Second { get; } = new();

        public async Task<IReadOnlyList<InstalledSkillRecord>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            var pending = Interlocked.Increment(ref _listCount) switch
            {
                1 => First,
                2 => Second,
                _ => throw new InvalidOperationException("Unexpected skill list request."),
            };
            pending.Started.TrySetResult();
            return await pending.Response.Task;
        }

        public Task<IReadOnlyList<InstalledSkillRecord>> ImportGitHubAsync(
            string url,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<InstalledSkillRecord>> ImportCommonAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<InstalledSkillRecord>> ImportLocalAsync(
            string folderPath,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string skillId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void RaiseSkillsChanged() => SkillsChanged?.Invoke();
    }

    private sealed class PendingList
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<IReadOnlyList<InstalledSkillRecord>> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DeleteCreateRaceSkillGateway : ISkillManagementGateway
    {
        private readonly object _syncRoot = new();
        private IReadOnlyList<InstalledSkillRecord> _skills = [Skill("deleted", "Deleted skill")];
        private int _listCount;

        public event Action? SkillsChanged;

        public TaskCompletionSource DelayedDeleteSnapshotStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDelayedDeleteSnapshot { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<InstalledSkillRecord>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _listCount);
            IReadOnlyList<InstalledSkillRecord> snapshot;
            lock (_syncRoot)
            {
                snapshot = _skills.ToArray();
            }

            if (call == 2)
            {
                DelayedDeleteSnapshotStarted.TrySetResult();
                await ReleaseDelayedDeleteSnapshot.Task;
            }
            return snapshot;
        }

        public Task<IReadOnlyList<InstalledSkillRecord>> ImportGitHubAsync(
            string url,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<InstalledSkillRecord>> ImportCommonAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<InstalledSkillRecord>> ImportLocalAsync(
            string folderPath,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<InstalledSkillRecord> created = [Skill("created", "Created skill")];
            lock (_syncRoot)
            {
                _skills = created;
            }
            SkillsChanged?.Invoke();
            return Task.FromResult(created);
        }

        public Task DeleteAsync(string skillId, CancellationToken cancellationToken = default)
        {
            lock (_syncRoot)
            {
                _skills = [];
            }
            SkillsChanged?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingImportSkillGateway : ISkillManagementGateway
    {
        private int _importCount;

        public event Action? SkillsChanged
        {
            add { }
            remove { }
        }

        public int ImportCount => Volatile.Read(ref _importCount);

        public TaskCompletionSource ImportStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseImport { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<InstalledSkillRecord>> ListAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InstalledSkillRecord>>([]);

        public Task<IReadOnlyList<InstalledSkillRecord>> ImportGitHubAsync(
            string url,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<InstalledSkillRecord>> ImportCommonAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<IReadOnlyList<InstalledSkillRecord>> ImportLocalAsync(
            string folderPath,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _importCount);
            ImportStarted.TrySetResult();
            await ReleaseImport.Task;
            return [Skill("late", "Late skill")];
        }

        public Task DeleteAsync(string skillId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
