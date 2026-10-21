using IWorkspaceEditorService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Workspace_Editor.IWorkspaceEditorService;
using Sunder.Sdk.Services;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Docker;

[SunderServiceProvider(typeof(IWorkspaceEditorService), ProviderId = "docker.workspace.editor")]
public sealed partial class DockerExecutionWorkspaceEditorContributor(
    DockerExecutionWorkspaceConfigService configService,
    DockerImageCatalogService imageCatalogService)
    : IWorkspaceEditorService
{
    private const string PackageId = "younics.ai.agent.execution.docker";
    private const string TargetId = "docker";
    private const string SectionId = "docker-execution-settings";
    private const string ImageFieldId = "image";
    private const string ShellPathFieldId = "shell-path";

    public string ContributorId => "younics.ai.agent.execution.docker.workspace-editor";

    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentServiceIdentity { Id = ContributorId, DisplayName = ContributorId }); }

    public ValueTask<bool> CanEditAsync(AgentWorkspaceEditorContext context, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(string.Equals(context.TargetId, TargetId, StringComparison.OrdinalIgnoreCase)); }

    public async ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
        AgentWorkspaceEditorContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = await configService.GetConfigAsync(context.ConfigurationId, cancellationToken);
        var images = await imageCatalogService.RefreshImagesAsync(cancellationToken);
        var imageOptions = images
            .Where(image => image.Status == DockerImageStatus.Ready)
            .Select(image => new AgentEditorOption { Value = image.ImageReference, Label = image.ImageReference, Description = null })
            .ToArray();
        var imageActions = new List<AgentEditorAction>
        {
            new() { ActionId = "refresh-docker-images", Label = "Refresh Images", Kind = AgentEditorActionKind.RefreshField, PackageId = null, Parameters = null },
        };
        if (imageOptions.Length == 0)
        {
            imageActions.Add(new AgentEditorAction { ActionId = "open-docker-execution-settings", Label = "Open Settings",
                Kind = AgentEditorActionKind.OpenPackageSettings, PackageId = PackageId, Parameters = null });
        }

        IReadOnlyList<AgentEditorSection> sections =
        [
            new AgentEditorSection
            {
                SectionId = SectionId, Title = "Docker Execution Settings",
                Description = "Docker creates a resource-bounded container with no network, no added Linux capabilities, and writable workspace bind mounts. It reduces exposure but is not a complete security sandbox. Workspace paths are mounted automatically from the main Workspace section.",
                Fields = [
                    new AgentEditorField
                    {
                        FieldId = ImageFieldId, Label = "Docker image", Kind = AgentEditorFieldKind.Select,
                        Description = imageOptions.Length == 0
                            ? "Pull at least one configured image in Docker Execution settings."
                            : "Choose a ready image with an explicit tag or sha256 digest.",
                        Value = config.ImageReference, Options = imageOptions, Actions = imageActions,
                        Items = null, AddItemLabel = null, UseFolderPicker = false, DefaultNewItemValue = null,
                        ItemValueLabel = null, SecondaryItemValueLabel = null, UseSecondaryFolderPicker = false, DefaultNewSecondaryItemValue = null,
                    },
                    new AgentEditorField
                    {
                        FieldId = ShellPathFieldId, Label = "Shell path inside container", Kind = AgentEditorFieldKind.Text,
                        Description = "POSIX-compatible shell used by shell and file tool commands.",
                        Value = config.ShellPath ?? DockerExecutionWorkspaceConfigService.DefaultShellPath, Options = null, Actions = null,
                        Items = null, AddItemLabel = null, UseFolderPicker = false, DefaultNewItemValue = null,
                        ItemValueLabel = null, SecondaryItemValueLabel = null, UseSecondaryFolderPicker = false, DefaultNewSecondaryItemValue = null,
                    },
                ],
            },
        ];

        return sections;
    }

    public async ValueTask<AgentEditorSaveResult> SaveSectionAsync(
        CYounics_Ai_Agent_Workspace_Editor_WorkspaceEditorSaveSectionRequest invocation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (context, request) = (invocation.Context, invocation.Request);
        if (!string.Equals(request.SectionId, SectionId, StringComparison.OrdinalIgnoreCase))
        {
            return new() { Success = false, Message = "Unknown Docker execution settings section." };
        }

        var image = request.Fields.TryGetValue(ImageFieldId, out var imageValue)
            ? imageValue.Value
            : null;
        if (string.IsNullOrWhiteSpace(image))
        {
            return new() { Success = false, Message = "Pull at least one configured Docker image in Docker Execution settings before saving this workspace." };
        }

        try
        {
            image = DockerImageCatalogService.NormalizeImageReference(image);
        }
        catch (InvalidOperationException ex)
        {
            return new() { Success = false, Message = ex.Message };
        }

        var imageReadiness = await imageCatalogService.GetReadinessAsync(image, cancellationToken);
        if (!imageReadiness.IsReady)
        {
            return new() { Success = false, Message = imageReadiness.Message };
        }

        var shellPath = request.Fields.TryGetValue(ShellPathFieldId, out var shellPathValue)
            ? shellPathValue.Value
            : null;
        try
        {
            var config = await configService.GetConfigAsync(context.ConfigurationId, cancellationToken);
            await configService.SaveConfigAsync(context.ConfigurationId, config with
            {
                ImageReference = image,
                ImageReferenceNeedsAttention = false,
                ShellPath = shellPath,
            }, cancellationToken);
            return new() { Success = true, Message = "Docker execution settings saved." };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new() { Success = false, Message = ex.Message };
        }
    }
}
