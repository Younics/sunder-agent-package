namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentProfilesViewModel
{
    private void OnProfileChanged(string profileId) => RunOnUiThread(() =>
    {
        if (!_disposed && _isInitialized)
        {
            _tasks.Run(_ => _runtimeRefresh.MarkDirty());
        }
        else if (!_disposed)
        {
            _initializationRefreshPending = true;
        }
    });

    private void OnDashboardInvalidated() => RunOnUiThread(() =>
    {
        if (!_disposed && _isInitialized)
        {
            _tasks.Run(async cancellationToken =>
            {
                await _runtimeRefresh.MarkDirty().WaitAsync(cancellationToken).ConfigureAwait(false);
                await RefreshSelectedProfileCapabilitiesOnUiThreadAsync(cancellationToken)
                    .ConfigureAwait(false);
            });
        }
        else if (!_disposed)
        {
            _initializationRefreshPending = true;
            _initializationCapabilityRefreshPending = true;
        }
    });

    private void OnSelectableCapabilitiesChanged()
        => RunOnUiThread(() =>
        {
            if (_isInitialized)
            {
                _tasks.Run(_ => RefreshSelectedProfileCapabilitiesAsync());
            }
            else if (!_disposed)
            {
                _initializationCapabilityRefreshPending = true;
            }
        });

    private async Task RefreshSelectedProfileCapabilitiesOnUiThreadAsync(
        CancellationToken cancellationToken)
    {
        Task refresh = Task.CompletedTask;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                refresh = RefreshSelectedProfileCapabilitiesAsync();
            }
        }).ConfigureAwait(false);
        await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
