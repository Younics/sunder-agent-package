using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews
{
    public sealed partial class MemoryInspectorViewModel
    {
        [ActivatorUtilitiesConstructor]
        internal MemoryInspectorViewModel(MemoryAppRuntimeGateway gateway, IPackageContext context, IPackageNotificationService notifications)
            : this((IMemoryInspectorGateway)gateway, PresentationDispatcher.Capture(),
                new PresentationOperationObserver(context, notifications, "Semantic Memory")) { }
    }
}

namespace Sunder.Package.Agent.Memory.Semantic
{
    internal sealed partial class MemoryAppRuntimeGateway
    {
        [ActivatorUtilitiesConstructor]
        internal MemoryAppRuntimeGateway(ISunderRpcClient rpc, IPackageContext context) : this(rpc, context.Logging.LoggerFactory) { }
    }
}
