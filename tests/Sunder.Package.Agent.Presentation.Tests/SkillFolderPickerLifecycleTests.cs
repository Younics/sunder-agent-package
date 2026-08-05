using System.Reflection;
using Avalonia.Headless.XUnit;
using Sunder.Package.Agent.Skills.PackageViews;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Tests;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class SkillFolderPickerLifecycleTests
{
    [AvaloniaFact]
    public async Task FolderPickerCompletionAfterViewDisposalIsIgnored()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new SkillStore(scope.Context);
        var viewModel = new SkillSettingsViewModel(
            store,
            new SkillImportService(store, new NoOpGitHubSkillClient(), scope.Context));
        await viewModel.InitializeAsync();
        var view = new SkillSettingsView(viewModel);
        var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuation = Assert.IsAssignableFrom<Task>(typeof(SkillSettingsView)
            .GetMethod(
                "ContinueLocalFolderImportAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [viewModel, picker.Task]));

        view.Dispose();
        picker.TrySetResult("/ignored-after-disposal");
        await continuation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(view.DataContext);
        Assert.Empty(viewModel.Skills);
    }

    private sealed class NoOpGitHubSkillClient : IGitHubSkillClient
    {
        public Task<string?> TryGetDefaultBranchAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<GitHubSkillFolder?> TryGetFolderAsync(
            GitHubSkillFolderRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<GitHubSkillFolder?>(null);

        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(
            GitHubSkillFolderRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<GitHubSkillFolder?>(null);

        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(
            GitHubSkillFolder folder,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GitHubSkillFile>>([]);

        public Task<byte[]> ReadFileAsync(
            GitHubSkillFolder folder,
            GitHubSkillFile file,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<byte>());
    }
}
