using System.Runtime.CompilerServices;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Provider.Shared;
using GenAIContent = Google.GenAI.Types.Content;

namespace Sunder.Package.Agent.Provider.Gemini;

internal sealed class GeminiCompletionTransport(
    GeminiResponseTranslator responseTranslator,
    ProviderStreamTelemetry telemetry)
{
    public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        Client client,
        List<GenAIContent> contents,
        GenerateContentConfig config,
        string modelId,
        bool allowMultipleToolCalls,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (translation, responseId) = await ExecuteAsync(
            client,
            contents,
            config,
            modelId,
            allowMultipleToolCalls,
            cancellationToken);

        if (translation.UnsupportedPartKinds.Count > 0)
        {
            await telemetry.UnsupportedResponseAsync(
                "Gemini",
                translation.UnsupportedPartKinds,
                cancellationToken);
            throw GeminiExceptionMapper.UnsupportedResponse(translation.UnsupportedPartKinds);
        }

        if (translation.Contents.Count > 0)
        {
            await telemetry.RecordFirstEventAsync(translation.FirstEventKind, cancellationToken);
            yield return ProviderResponseUpdates.Create(modelId, responseId, responseId, translation.Contents);
        }

        if (ProviderResponseUpdates.CreateUsage(modelId, responseId, responseId, translation.Usage) is { } usageUpdate)
        {
            yield return usageUpdate;
        }

        await telemetry.CompletedAsync("Provider completed without content.", cancellationToken);
    }

    private async Task<(GeminiResponseTranslation Translation, string ResponseId)> ExecuteAsync(
        Client client,
        List<GenAIContent> contents,
        GenerateContentConfig config,
        string modelId,
        bool allowMultipleToolCalls,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.Models.GenerateContentAsync(
                model: ProviderModelId.RemovePrefix(modelId, "gemini"),
                contents: contents,
                config: config,
                cancellationToken: cancellationToken);
            var terminalStatus = GeminiResponseTranslator.GetTerminalStatus(response);
            if (!terminalStatus.IsTerminal || !terminalStatus.IsSuccess)
            {
                throw GeminiExceptionMapper.IncompleteResponse(
                    terminalStatus.Detail ?? "Gemini completion omitted a successful terminal finish reason.");
            }

            var translation = responseTranslator.Translate(response, allowMultipleToolCalls);
            return (
                translation,
                string.IsNullOrWhiteSpace(response.ResponseId) ? Guid.NewGuid().ToString("N") : response.ResponseId);
        }
        catch (Exception ex)
        {
            switch (ProviderStreamFailureClassifier.Classify(ex, cancellationToken))
            {
                case ProviderStreamFailureKind.CallerCancellation:
                    await telemetry.CanceledAsync();
                    throw;
                case ProviderStreamFailureKind.ProviderCancellation:
                    await telemetry.FailedAsync(ex);
                    throw GeminiExceptionMapper.ProviderTimeout((OperationCanceledException)ex);
                default:
                    await telemetry.FailedAsync(ex);
                    throw GeminiExceptionMapper.Request(ex);
            }
        }
    }
}
