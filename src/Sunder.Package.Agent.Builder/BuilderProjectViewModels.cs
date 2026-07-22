using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Builder;

public sealed class BuilderPrerequisiteViewModel(BuilderPrerequisiteStatus status)
{
    public BuilderPrerequisiteKind Kind { get; } = status.Kind;

    public string Name { get; } = status.Name;

    public bool IsInstalled { get; } = status.IsInstalled;

    public string Detail { get; } = status.Detail;

    public string StateText => IsInstalled ? "Installed" : "Missing";
}

public sealed class BuilderWorkspacePathOptionViewModel(AgentWorkspacePathRecord path, BuilderPathService pathService)
{
    public string PathId { get; } = path.PathId;

    public string HostPath { get; } = path.HostPath;

    public bool IsDefault { get; } = path.IsDefault;

    public string DisplayPath { get; } = pathService.FormatWorkspacePath(path.HostPath);
}

public sealed class BuilderProjectViewModel(BuilderProjectRecord record) : INotifyPropertyChanged
{
    private string _displayName = record.DisplayName ?? string.Empty;
    private string _packageId = record.PackageId ?? string.Empty;
    private string _workspaceId = record.WorkspaceId ?? string.Empty;
    private string _workspacePathId = record.WorkspacePathId ?? string.Empty;
    private string _executionProjectFolder = record.ExecutionProjectFolder ?? string.Empty;
    private string _projectFolder = record.ProjectFolder ?? string.Empty;
    private DateTimeOffset _updatedAtUtc = record.UpdatedAtUtc;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; } = record.Id;

    public DateTimeOffset CreatedAtUtc { get; } = record.CreatedAtUtc;

    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    public string PackageId
    {
        get => _packageId;
        set => SetField(ref _packageId, value);
    }

    public string WorkspaceId
    {
        get => _workspaceId;
        set => SetField(ref _workspaceId, value);
    }

    public string WorkspacePathId
    {
        get => _workspacePathId;
        set => SetField(ref _workspacePathId, value);
    }

    public string ExecutionProjectFolder
    {
        get => _executionProjectFolder;
        set => SetField(ref _executionProjectFolder, value);
    }

    public string ProjectFolder
    {
        get => _projectFolder;
        set => SetField(ref _projectFolder, value);
    }

    public DateTimeOffset UpdatedAtUtc
    {
        get => _updatedAtUtc;
        private set => SetField(ref _updatedAtUtc, value);
    }

    public void Apply(BuilderProjectRecord value)
    {
        DisplayName = value.DisplayName ?? string.Empty;
        PackageId = value.PackageId ?? string.Empty;
        WorkspaceId = value.WorkspaceId ?? string.Empty;
        WorkspacePathId = value.WorkspacePathId ?? string.Empty;
        ExecutionProjectFolder = value.ExecutionProjectFolder ?? string.Empty;
        ProjectFolder = value.ProjectFolder ?? string.Empty;
        UpdatedAtUtc = value.UpdatedAtUtc;
    }

    public BuilderProjectRecord ToRecord()
        => new(
            Id,
            DisplayName.Trim(),
            PackageId.Trim(),
            WorkspaceId.Trim(),
            ExecutionProjectFolder.Trim(),
            ProjectFolder.Trim(),
            CreatedAtUtc,
            UpdatedAtUtc)
        {
            WorkspacePathId = WorkspacePathId.Trim(),
        };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
