using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Generated.Packages.Sunder_Package_Agent_Execution_Docker_Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed partial class DockerExecutionSettingsViewModel
{
    [ActivatorUtilitiesConstructor]
    internal DockerExecutionSettingsViewModel(IPresentationService client, IBackgroundProcessQueue background, IPackageContext context)
        : this(client, background, context.Logging.LoggerFactory.CreateLogger<DockerExecutionSettingsViewModel>()) { }
}
