using Avalonia.Threading;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    private CancellationTokenSource? _workspaceWarmupCts;
    private int _workspaceWarmupVersion;

    private void ScheduleSelectedWorkspaceWarmup()
    {
        if (_warmupService is null)
        {
            return;
        }

        _workspaceWarmupCts?.Cancel();
        _workspaceWarmupCts?.Dispose();
        if (SelectedWorkspace is null)
        {
            _workspaceWarmupCts = null;
            return;
        }

        var warmupCts = new CancellationTokenSource();
        _workspaceWarmupCts = warmupCts;
        var workspace = SelectedWorkspace;
        var version = ++_workspaceWarmupVersion;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _warmupService.WarmWorkspaceAsync(workspace, warmupCts.Token).ConfigureAwait(false);
                if (result.Status == AgentExecutionTargetWarmupStatus.Failed)
                {
                    ApplyWorkspaceWarmupFailure(workspace.WorkspaceId, version, result.Message);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ApplyWorkspaceWarmupFailure(workspace.WorkspaceId, version, ex.Message);
            }
        }, CancellationToken.None);
    }

    private void ApplyWorkspaceWarmupFailure(string workspaceId, int version, string message)
    {
        void Apply()
        {
            if (version != _workspaceWarmupVersion
                || SelectedWorkspace is null
                || !string.Equals(SelectedWorkspace.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                || SelectedSession is not null)
            {
                return;
            }

            SetGlobalStatus($"Execution target is not ready: {message}");
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.UIThread.Post(Apply);
        }
    }
}
