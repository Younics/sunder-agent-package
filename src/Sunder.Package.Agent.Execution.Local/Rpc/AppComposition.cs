using Microsoft.Extensions.DependencyInjection;

namespace Sunder.Package.Agent.Execution.Local;

public sealed partial class LocalExecutionSettingsViewModel
{
    [ActivatorUtilitiesConstructor]
    internal LocalExecutionSettingsViewModel(App.LocalExecutionSourceClient client) : this((ILocalExecutionPresentationClient)client) { }
}
