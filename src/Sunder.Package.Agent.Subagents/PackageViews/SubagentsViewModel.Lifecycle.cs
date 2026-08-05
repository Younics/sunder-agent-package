namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubagentsViewModel
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _initialization.RunAsync(InitializeCoreAsync, cancellationToken);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_initializationFailureStatus is not null
                    && string.Equals(StatusText, _initializationFailureStatus, StringComparison.Ordinal))
                {
                    ClearStatus();
                }
                _initializationFailureStatus = null;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    _initializationFailureStatus = ex.Message;
                    SetStatus(_initializationFailureStatus, SubagentStatusKind.Error);
                }
            });
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (_gateway is Runtime.ISubagentPresentationInitialization initialization)
        {
            await initialization.InitializeAsync(cancellationToken)
                .WaitAsync(cancellationToken);
        }

        await ReloadAsync(cancellationToken);
        Task detailLoad = Task.CompletedTask;
        await RunOnUiThreadAsync(() => detailLoad = _currentDetailLoad);
        await detailLoad.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _initialization.Dispose();
        _statusClear.Dispose();
        _tasks.Dispose();
        _operation.Dispose();
        ChatBinding.PropertyChanged -= OnModelBindingPropertyChanged;
        ChatBinding.Changed -= OnEditorSelectionChanged;
        Capabilities.Changed -= OnCapabilitiesChanged;
        _listDetail.SelectionChanging -= OnSubagentSelectionChanging;
        _listDetail.PropertyChanged -= OnListDetailPropertyChanged;
        if (_gateway is not null)
        {
            _gateway.SubagentsChanged -= OnSubagentsChanged;
            _gateway.CatalogChanged -= OnSelectableCapabilitiesChanged;
        }

        ChatBinding.Dispose();
        _runtimeRefresh.Dispose();
        _listDetail.Dispose();
        _requests.Dispose();
        _lifetimeCancellation.Dispose();
    }
}
