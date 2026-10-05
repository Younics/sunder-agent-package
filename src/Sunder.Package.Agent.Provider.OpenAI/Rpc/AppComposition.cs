using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.Provider.OpenAI
{
    public sealed partial class OpenAiSettingsViewModel
    {
        [ActivatorUtilitiesConstructor]
        internal OpenAiSettingsViewModel(IPackageContext context, OpenAiAuthPresentationService auth,
            ProviderCredentialOwnerClient credentials, IPackageNotificationService notifications)
            : this(context, auth, new PackageCallbackFlowRunner(context.Callbacks), credentials,
                new PresentationOperationObserver(context, notifications, "OpenAI settings")) { }
    }

    internal sealed partial class OpenAiAuthPresentationService
    {
        [ActivatorUtilitiesConstructor]
        internal OpenAiAuthPresentationService(OpenAiOwnerManagementRpcClient management) : this((IOpenAiOwnerManagementClient)management) { }
    }
}

namespace Sunder.Package.Agent.Provider.Shared
{
    internal sealed partial class ProviderCredentialOwnerClient
    {
        [ActivatorUtilitiesConstructor]
        internal ProviderCredentialOwnerClient(OpenAI.OpenAiOwnerManagementRpcClient management) : this((OpenAI.IOpenAiOwnerManagementClient)management) { }
    }
}
