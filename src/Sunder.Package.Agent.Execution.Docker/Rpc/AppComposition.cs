using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed partial class DockerExecutionSettingsViewModel
{
    [ActivatorUtilitiesConstructor]
    internal DockerExecutionSettingsViewModel(App.DockerExecutionSourceClient client, IBackgroundProcessQueue background, IPackageContext context)
        : this(client, background, context.Logging.LoggerFactory.CreateLogger<DockerExecutionSettingsViewModel>()) { }
}
