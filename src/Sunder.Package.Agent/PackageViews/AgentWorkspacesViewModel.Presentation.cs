using System.ComponentModel;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentWorkspacesViewModel
{
    private void RunOnUiThread(Action action)
    {
        _tasks.Run(_ => _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                action();
            }
        }));
    }

    private void ClearStatus()
        => SetStatus(string.Empty, AgentWorkspaceStatusKind.None);

    internal void ReportPresentationFailure(Exception exception)
        => SetStatus(exception.Message, AgentWorkspaceStatusKind.Error);

    private void SetStatus(string message, AgentWorkspaceStatusKind kind, bool autoClear = false)
    {
        _statusClear.Cancel();
        StatusKind = string.IsNullOrWhiteSpace(message) ? AgentWorkspaceStatusKind.None : kind;
        StatusText = message;
        if (autoClear && StatusKind == AgentWorkspaceStatusKind.Success)
        {
            _tasks.Run(_ => _statusClear.ScheduleAsync(SuccessStatusDisplayDuration, () =>
            {
                if (StatusKind == AgentWorkspaceStatusKind.Success
                    && string.Equals(StatusText, message, StringComparison.Ordinal))
                {
                    ClearStatus();
                }
            }));
        }
    }

    private OperationGeneration BeginOperation(AgentWorkspaceOperation operation)
    {
        var generation = _operation.Begin(operation, canCancel: false);
        OnOperationPropertyChanged(this, new PropertyChangedEventArgs(nameof(OperationState.IsBusy)));
        return generation;
    }

    private void EndOperation(OperationGeneration generation)
        => _operation.TryComplete(generation);

    private void OnOperationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsBusy));
        SaveWorkspaceCommand.NotifyCanExecuteChanged();
        DeleteWorkspaceCommand.NotifyCanExecuteChanged();
    }
}
