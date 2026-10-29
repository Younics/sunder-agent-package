namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderProjectViewModel
{
    private string _dismissedAttention = string.Empty;
    private string _dismissedStatus = string.Empty;

    public string AttentionMessage => string.Join(Environment.NewLine,
        new[] { ErrorMessage, RuntimeError }.Where(message => message.Length > 0).Distinct());
    public bool ShowAttentionNotice => AttentionMessage.Length > 0 && AttentionMessage != _dismissedAttention;
    public bool ShowStatusNotice => !IsBusy && !HasError && !HasRuntimeError && StatusText.Length > 0
        && StatusText != "Ready to load" && StatusText != _dismissedStatus;
    public string ToolchainNoticeText => (_latestInspection ?? Inspection)?.ToolchainAssessment?.Message ?? string.Empty;
    public bool HasToolchainNotice => ToolchainNoticeText.Length > 0;
    public bool CanUpgradeToolchain => CanOperate && (_latestInspection ?? Inspection)?.ToolchainAssessment is { RequiresUpgrade: true, CanUpgrade: true };
    public bool ShowUpgradeToolchain => (_latestInspection ?? Inspection)?.ToolchainAssessment is { RequiresUpgrade: true, CanUpgrade: true };
    public bool IsReadinessWarning => _releasePending || HasSession && (!IsLoaded
        || _developmentStatus?.AppReadiness is not (Sunder.Sdk.Abstractions.PackageDevelopmentAppReadiness.Activated
            or Sunder.Sdk.Abstractions.PackageDevelopmentAppReadiness.NotApplicable));
    public bool ShowActivityNotice => IsBusy || IsReadinessWarning || _reloadProgress?.State is "running" or "unknown";
    public bool HasNotices => IsMissing || HasToolchainNotice || SetupIncomplete || HasSourceConflict
        || ShowAttentionNotice || ShowStatusNotice || ShowActivityNotice;

    public void DismissAttentionNotice() { _dismissedAttention = AttentionMessage; ChangedAll(); }
    public void DismissStatusNotice() { _dismissedStatus = StatusText; ChangedAll(); }
}
