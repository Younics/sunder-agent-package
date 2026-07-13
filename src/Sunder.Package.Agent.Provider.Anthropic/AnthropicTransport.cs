using Anthropic;
using Microsoft.Extensions.AI;

namespace Sunder.Package.Agent.Provider.Anthropic;

internal interface IAnthropicTransport
{
    IAsyncEnumerable<ChatResponseUpdate> SendAsync(
        AnthropicRequest request,
        string modelId,
        CancellationToken cancellationToken);
}

internal sealed class AnthropicStandardTransport(
    IAnthropicClient client,
    AnthropicResponseTranslator responseTranslator) : IAnthropicTransport
{
    public IAsyncEnumerable<ChatResponseUpdate> SendAsync(
        AnthropicRequest request,
        string modelId,
        CancellationToken cancellationToken)
        => request.IncludeTools
            ? responseTranslator.TranslateToolResponseAsync(
                token => client.Messages.Create(request.Parameters, token),
                static response => response.Content,
                static response => response.StopReason?.Value().ToString(),
                static response => AnthropicResponseTranslator.GetUsage(response.Usage),
                AnthropicResponseTranslator.GetReasoningContent,
                AnthropicResponseTranslator.GetToolCall,
                AnthropicResponseTranslator.GetText,
                modelId,
                request.AllowMultipleToolCalls,
                cancellationToken)
            : responseTranslator.TranslateStreamingResponseAsync(
                () => client.Messages.CreateStreaming(request.Parameters, cancellationToken),
                AnthropicResponseTranslator.GetStreamingContent,
                modelId,
                cancellationToken);
}

internal sealed class AnthropicFastTransport(
    IAnthropicClient client,
    AnthropicResponseTranslator responseTranslator) : IAnthropicTransport
{
    public IAsyncEnumerable<ChatResponseUpdate> SendAsync(
        AnthropicRequest request,
        string modelId,
        CancellationToken cancellationToken)
    {
        var parameters = AnthropicOptionsTranslator.TranslateFast(request.Parameters);
        return request.IncludeTools
            ? responseTranslator.TranslateToolResponseAsync(
                token => client.Beta.Messages.Create(parameters, token),
                static response => response.Content,
                static response => response.StopReason?.Value().ToString(),
                static response => AnthropicResponseTranslator.GetUsage(response.Usage),
                AnthropicResponseTranslator.GetReasoningContent,
                AnthropicResponseTranslator.GetToolCall,
                AnthropicResponseTranslator.GetText,
                modelId,
                request.AllowMultipleToolCalls,
                cancellationToken)
            : responseTranslator.TranslateStreamingResponseAsync(
                () => client.Beta.Messages.CreateStreaming(parameters, cancellationToken),
                AnthropicResponseTranslator.GetStreamingContent,
                modelId,
                cancellationToken);
    }
}
