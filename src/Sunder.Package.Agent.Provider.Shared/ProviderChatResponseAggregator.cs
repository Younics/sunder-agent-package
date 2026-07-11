using Microsoft.Extensions.AI;

namespace Sunder.Package.Agent.Provider.Shared;

internal static class ProviderChatResponseAggregator
{
    public static async Task<ChatResponse> AggregateAsync(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        string fallbackModelId,
        CancellationToken cancellationToken)
    {
        var response = await updates.ToChatResponseAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(response.ModelId))
        {
            response.ModelId = fallbackModelId;
        }

        return response;
    }
}
