namespace Sunder.Package.Agent.Provider.OpenAI;

internal sealed partial class OpenAiAuthPresentationService(IOpenAiOwnerManagementClient management)
{
    internal ValueTask<OpenAiAuthOperationResult> GetStatusAsync(
        CancellationToken cancellationToken = default)
        => management.GetAuthStatusAsync(cancellationToken);

    internal ValueTask<OpenAiAuthOperationResult> DisconnectAsync(
        CancellationToken cancellationToken = default)
        => management.DisconnectAuthAsync(cancellationToken);
}
