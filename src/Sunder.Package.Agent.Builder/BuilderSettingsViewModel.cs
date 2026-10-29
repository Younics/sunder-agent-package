using System.Collections.ObjectModel;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

/// <summary>Shared activation-owned SDK operations. Closing Settings never cancels an installation.</summary>
public sealed class BuilderSdkManager : IAsyncDisposable
{
    private readonly BuilderProjectApplicationService _application;
    private readonly IBuilderUiDispatcher _ui;
    private readonly PresentationTaskScope _tasks;
    private readonly object _refreshSync = new();
    private Task? _refresh;

    public BuilderSdkManager(BuilderProjectApplicationService application, IBuilderUiDispatcher ui)
    {
        _application = application;
        _ui = ui;
        _tasks = new(PresentationTaskScope.PropagateExpectedFailure, ui.ShutdownToken);
        Cards = [new(BuilderProjectKind.Dotnet), new(BuilderProjectKind.Node)];
    }

    public ObservableCollection<BuilderSdkCardViewModel> Cards { get; }

    public Task RefreshAsync(bool checkUpdates = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task refresh;
        lock (_refreshSync)
        {
            refresh = _refresh is { IsCompleted: false } ? _refresh
                : _refresh = _tasks.Track(token => Task.WhenAll(Cards.Select(card => RefreshCardAsync(card, checkUpdates, token))));
        }
        return cancellationToken.CanBeCanceled ? refresh.WaitAsync(cancellationToken) : refresh;
    }

    private async Task RefreshCardAsync(BuilderSdkCardViewModel card, bool checkUpdates, CancellationToken token)
    {
        if (!await _ui.InvokeAsync(() =>
            {
                if (card.IsRefreshing || card.IsBusy) return false;
                card.IsRefreshing = true;
                return true;
            }, token).ConfigureAwait(false)) return;
        try
        {
            var status = await Task.Run(() => _application.GetToolchainsAsync(card.Kind, checkUpdates, token), token).ConfigureAwait(false);
            await UpdateUiAsync(() => card.Apply(status)).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, token)) { }
        catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
        {
            await UpdateUiAsync(() => card.SetRefreshError(error.Message)).ConfigureAwait(false);
        }
        finally { await UpdateUiAsync(() => card.IsRefreshing = false).ConfigureAwait(false); }
    }

    public Task InstallAsync(BuilderProjectKind kind) => RunAsync(kind, "ensure-sdk", null);
    public Task RepairAsync(BuilderToolchainInstallation installation) => RunAsync(installation.Kind, "repair-sdk", installation);
    public Task RemoveAsync(BuilderToolchainInstallation installation) => RunAsync(installation.Kind, "remove-sdk", installation);

    public void Cancel(BuilderProjectKind kind) => Cards.Single(card => card.Kind == kind).Cancellation?.Cancel();

    private Task RunAsync(BuilderProjectKind kind, string operation, BuilderToolchainInstallation? installation)
        => _tasks.Track(lifetime => Task.Run(async () =>
        {
            var card = Cards.Single(card => card.Kind == kind);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            if (!await _ui.InvokeAsync(() =>
                {
                    if (!card.CanManage) return false;
                    card.IsBusy = true;
                    card.Cancellation = cancellation;
                    return true;
                }, lifetime).ConfigureAwait(false)) return;
            try
            {
                await _application.RunJobAsync(new() { Operation = operation, Kind = kind, Installation = installation },
                    snapshot => UpdateUiAsync(() => card.ApplyJob(snapshot)), cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, cancellation.Token))
            {
                await UpdateUiAsync(() => card.SetError("SDK operation cancelled.")).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
            {
                await UpdateUiAsync(() => card.SetError(error.Message)).ConfigureAwait(false);
            }
            finally
            {
                card.Cancellation = null;
                await UpdateUiAsync(() => card.IsBusy = false).ConfigureAwait(false);
                if (!lifetime.IsCancellationRequested) await RefreshCardAsync(card, checkUpdates: false, lifetime).ConfigureAwait(false);
            }
        }));

    public ValueTask DisposeAsync() => _tasks.DisposeAsync();

    private async Task UpdateUiAsync(Action action)
    {
        try { await _ui.InvokeAsync(action, _tasks.CancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, _tasks.CancellationToken)) { }
    }
}

public sealed class BuilderSettingsViewModel(BuilderSdkManager manager) : IPackageViewNavigationPreparationTarget
{
    public ObservableCollection<BuilderSdkCardViewModel> Cards => manager.Cards;
    public Task RefreshAsync(CancellationToken cancellationToken = default) => manager.RefreshAsync(true, cancellationToken);
    public Task InstallAsync(BuilderProjectKind kind) => manager.InstallAsync(kind);
    public Task RepairAsync(BuilderToolchainInstallation installation) => manager.RepairAsync(installation);
    public Task RemoveAsync(BuilderToolchainInstallation installation) => manager.RemoveAsync(installation);
    public void Cancel(BuilderProjectKind kind) => manager.Cancel(kind);

    public ValueTask<bool> PrepareNavigationAsync(PackageViewNavigationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Both cards (and any cached observations) are already present. SDK probes
        // and remote update checks are activation-owned observations, not a gate
        // on presenting Settings. The manager tracks, coalesces and drains this work.
        _ = manager.RefreshAsync();
        return ValueTask.FromResult(true);
    }
    public ValueTask OnNavigationPresentedAsync(PackageViewNavigationContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

public sealed class BuilderSdkCardViewModel(BuilderProjectKind kind) : BuilderObservable
{
    private BuilderToolchainStatus? _status;
    private bool _busy;
    private bool _refreshing;
    private string _error = string.Empty;
    private string _refreshError = string.Empty;
    private string _message = string.Empty;
    private string _output = string.Empty;
    public BuilderProjectKind Kind { get; } = kind;
    public string Glyph => Kind == BuilderProjectKind.Dotnet ? ".NET" : "JS";
    public string Title => Kind == BuilderProjectKind.Dotnet ? ".NET SDK" : "Node.js + npm";
    public string Description => Kind == BuilderProjectKind.Dotnet ? "UI-ready Avalonia packages" : "UI-ready React / TypeScript packages";
    public ObservableCollection<BuilderSdkInstallationViewModel> Installations { get; } = [];
    public bool IsBusy { get => _busy; internal set { _busy = value; Notify(); } }
    public bool IsRefreshing { get => _refreshing; internal set { _refreshing = value; Notify(); } }
    public bool CanManage => !IsBusy && !IsRefreshing;
    public bool IsEmpty => _status is not null && Installations.Count == 0;
    public string CurrentVersion => _status is null ? IsRefreshing ? "Checking…" : "Not checked" : string.Join(", ", _status.Installed.Where(item => item.IsHealthy).Select(item => item.Version).Distinct()) is { Length: > 0 } versions ? versions : "Not installed";
    public string RecommendedVersion => _status?.RecommendedVersion ?? (IsRefreshing ? "Checking…" : "Unknown");
    public string VersionText => $"Installed: {CurrentVersion} · Sunder version: {RecommendedVersion}";
    public string PrimaryActionText => IsEmpty ? "Install SDK" : _status is { UpdateAvailable: true, CatalogError: null } ? "Update SDK" : string.Empty;
    public bool ShowPrimaryAction => !IsRefreshing && _refreshError.Length == 0 && PrimaryActionText.Length > 0;
    public string Status => IsRefreshing ? "Checking SDKs…" : _status is null ? _refreshError.Length > 0 ? "SDK discovery unavailable" : "Not checked"
        : IsEmpty ? "Not installed" : _status.Installed.Any(item => !item.IsHealthy) ? "One or more SDKs need repair" : UpdateText;
    public string UpdateText => _status?.CatalogError is { } error ? "Update status unknown · " + error
        : _status?.UpdateAvailable switch { true => "Update available", false => "Up to date", null => "Update status unknown" };
    public string LastCheckText => _status?.LastCheckedAtUtc is { } checkedAt ? "Last check: " + checkedAt.ToLocalTime().ToString("g") : "No check yet";
    public string ErrorMessage => string.Join(Environment.NewLine, new[] { _refreshError, _error }.Where(message => message.Length > 0));
    public bool HasError => ErrorMessage.Length > 0;
    public string Message => _message;
    public bool ShowMessage => !HasError && _message.Length > 0;
    public bool ShowOperationStatus => IsBusy || ShowMessage;
    public string Output => _output;
    public bool HasOutput => _output.Length > 0;
    internal CancellationTokenSource? Cancellation { get; set; }

    internal void Apply(BuilderToolchainStatus status)
    {
        _status = status;
        _refreshError = string.Empty;
        var keys = status.Installed.Select(item => item.ExecutablePath + "|" + item.Version).ToHashSet(StringComparer.Ordinal);
        foreach (var row in Installations.Where(row => !keys.Contains(row.Key)).ToArray()) Installations.Remove(row);
        foreach (var installation in status.Installed)
        {
            var key = installation.ExecutablePath + "|" + installation.Version;
            var row = Installations.FirstOrDefault(row => row.Key == key);
            if (row is null) Installations.Add(new(this, installation));
            else row.Apply(installation);
        }
        Notify();
    }
    internal void ApplyJob(BuilderJobSnapshot snapshot)
    {
        _message = snapshot.Message;
        _output = BuilderProjectViewModel.BoundOutput(snapshot.Output);
        _error = snapshot.State switch
        {
            "failed" => snapshot.Error ?? snapshot.Message,
            "cancelled" => "SDK operation cancelled. Retry when ready.",
            _ => string.Empty,
        };
        if (snapshot.State == "running") _refreshError = string.Empty;
        Notify();
    }
    internal void SetError(string message) { _error = message; Notify(); }
    internal void SetRefreshError(string message) { _refreshError = message; Notify(); }
    private void Notify() { ChangedAll(); foreach (var row in Installations) row.Notify(); }
}

public sealed class BuilderSdkInstallationViewModel(BuilderSdkCardViewModel card, BuilderToolchainInstallation installation) : BuilderObservable
{
    public BuilderToolchainInstallation Installation { get; private set; } = installation;
    public string Key => Installation.ExecutablePath + "|" + Installation.Version;
    public string Version => Installation.Version;
    public string Origin => Installation.IsManaged ? "Builder managed" : "External · read-only";
    public string ExecutablePath => Installation.ExecutablePath;
    public string NpmText => Installation.Kind == BuilderProjectKind.Node ? "npm " + (Installation.NpmVersion ?? "unavailable") : string.Empty;
    public string HealthText => Installation.IsHealthy ? "Healthy" : Installation.HealthError ?? "Needs repair";
    public bool CanManage => card.CanManage && Installation.IsManaged;
    internal void Apply(BuilderToolchainInstallation value) { Installation = value; Notify(); }
    internal void Notify() => ChangedAll();
}
