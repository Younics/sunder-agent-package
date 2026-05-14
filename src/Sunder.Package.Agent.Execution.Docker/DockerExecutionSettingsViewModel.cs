using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed partial class DockerExecutionSettingsViewModel : ObservableObject
{
    private const string TimeoutKey = "docker.timeoutSeconds.default";
    private const string DefaultTimeoutSeconds = "300";

    private readonly DockerImageCatalogService _imageCatalogService;
    private readonly IPackageContext _packageContext;

    public DockerExecutionSettingsViewModel(
        DockerImageCatalogService imageCatalogService,
        IPackageContext packageContext)
    {
        _imageCatalogService = imageCatalogService;
        _packageContext = packageContext;
        TimeoutSeconds = _packageContext.Storage.State.GetValue(TimeoutKey)
                         ?? _packageContext.Configuration.GetValue(TimeoutKey)
                         ?? DefaultTimeoutSeconds;
        ReloadImages();
    }

    public ObservableCollection<DockerImageRowViewModel> Images { get; } = [];

    public bool HasImages => Images.Count > 0;

    public bool CanAddImage => !IsBusy && !string.IsNullOrWhiteSpace(NewImageReference);

    public bool CanUseSelectedImage => !IsBusy && SelectedImage is not null;

    [ObservableProperty]
    private DockerImageRowViewModel? _selectedImage;

    [ObservableProperty]
    private string _newImageReference = string.Empty;

    [ObservableProperty]
    private string _timeoutSeconds;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    partial void OnSelectedImageChanged(DockerImageRowViewModel? value)
        => NotifySelectedImageCommands();

    partial void OnNewImageReferenceChanged(string value)
        => AddImageCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        AddImageCommand.NotifyCanExecuteChanged();
        NotifySelectedImageCommands();
        RefreshAllImagesCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddImage));
        OnPropertyChanged(nameof(CanUseSelectedImage));
    }

    [RelayCommand(CanExecute = nameof(CanAddImage))]
    private void AddImage()
    {
        try
        {
            var image = _imageCatalogService.AddImage(NewImageReference);
            NewImageReference = string.Empty;
            ReloadImages(image.ImageReference);
            StatusText = $"Added Docker image '{image.ImageReference}'. Pull it before assigning it to workspaces.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedImage))]
    private void DeleteSelectedImage()
    {
        if (SelectedImage is null)
        {
            return;
        }

        var imageReference = SelectedImage.ImageReference;
        _imageCatalogService.DeleteImage(imageReference);
        ReloadImages();
        StatusText = $"Deleted Docker image '{imageReference}' from Sunder settings. Existing Docker images on disk were not removed.";
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedImage))]
    private async Task PullSelectedImageAsync()
    {
        if (SelectedImage is null)
        {
            return;
        }

        var selected = SelectedImage;
        IsBusy = true;
        selected.Status = DockerImageStatus.Pulling;
        selected.ErrorMessage = string.Empty;
        StatusText = $"Pulling Docker image '{selected.ImageReference}'...";
        try
        {
            var progress = new Progress<string>(line =>
            {
                StatusText = line;
            });
            var result = await _imageCatalogService.PullImageAsync(selected.ImageReference, progress);
            ReloadImages(selected.ImageReference);
            StatusText = result.Message;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ReloadImages(selected.ImageReference);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedImage))]
    private async Task RefreshSelectedImageAsync()
    {
        if (SelectedImage is null)
        {
            return;
        }

        var selected = SelectedImage;
        IsBusy = true;
        try
        {
            var image = await _imageCatalogService.RefreshImageAsync(selected.ImageReference);
            ReloadImages(image.ImageReference);
            StatusText = image.LastMessage ?? $"Refreshed Docker image '{image.ImageReference}'.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBusyCommand))]
    private async Task RefreshAllImagesAsync()
    {
        IsBusy = true;
        try
        {
            await _imageCatalogService.RefreshImagesAsync();
            ReloadImages(SelectedImage?.ImageReference);
            StatusText = "Docker image status refreshed.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBusyCommand))]
    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(TimeoutSeconds, out var timeoutSeconds) || timeoutSeconds <= 0)
        {
            StatusText = "Docker command timeout must be a positive number of seconds.";
            return;
        }

        IsBusy = true;
        try
        {
            await _packageContext.Storage.State.SetValueAsync(TimeoutKey, timeoutSeconds.ToString());
            TimeoutSeconds = timeoutSeconds.ToString();
            StatusText = "Docker execution settings saved.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunBusyCommand() => !IsBusy;

    private void ReloadImages(string? selectedImageReference = null)
    {
        Images.Clear();
        foreach (var image in _imageCatalogService.ListImages())
        {
            Images.Add(new DockerImageRowViewModel(image));
        }

        SelectedImage = !string.IsNullOrWhiteSpace(selectedImageReference)
            ? Images.FirstOrDefault(image => string.Equals(image.ImageReference, selectedImageReference, StringComparison.OrdinalIgnoreCase)) ?? Images.FirstOrDefault()
            : Images.FirstOrDefault();
        OnPropertyChanged(nameof(HasImages));
        NotifySelectedImageCommands();
    }

    private void NotifySelectedImageCommands()
    {
        DeleteSelectedImageCommand.NotifyCanExecuteChanged();
        PullSelectedImageCommand.NotifyCanExecuteChanged();
        RefreshSelectedImageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUseSelectedImage));
    }
}

public sealed partial class DockerImageRowViewModel : ObservableObject
{
    public DockerImageRowViewModel(DockerImageDefinition image)
    {
        ImageReference = image.ImageReference;
        _status = image.Status;
        _errorMessage = image.Status == DockerImageStatus.Failed ? image.LastMessage ?? string.Empty : string.Empty;
        DateText = image.LastPulledAtUtc is null
            ? string.Empty
            : $"Pulled {image.LastPulledAtUtc.Value.LocalDateTime:g}";
    }

    public string ImageReference { get; }

    public string DateText { get; }

    public bool HasDateText => !string.IsNullOrWhiteSpace(DateText);

    public string StatusText => Status switch
    {
        DockerImageStatus.Ready => "Ready",
        DockerImageStatus.Pulling => "Pulling",
        DockerImageStatus.Failed => "Failed",
        _ => "Not pulled",
    };

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private DockerImageStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string _errorMessage;
}
