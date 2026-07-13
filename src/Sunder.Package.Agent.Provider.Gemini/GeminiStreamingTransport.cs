using System.Runtime.CompilerServices;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Provider.Shared;
using GenAIContent = Google.GenAI.Types.Content;

namespace Sunder.Package.Agent.Provider.Gemini;

internal sealed class GeminiStreamingTransport(
    GeminiResponseTranslator responseTranslator,
    ProviderStreamTelemetry telemetry)
{
    public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        Client client,
        List<GenAIContent> contents,
        GenerateContentConfig config,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = CreateStream(client, contents, config, modelId, cancellationToken);
        var fallbackResponseId = Guid.NewGuid().ToString("N");
        var responseId = fallbackResponseId;
        var usage = new ProviderUsageAccumulator();
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            GeminiResponseTranslation translation;
            GenerateContentResponse chunk;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                chunk = enumerator.Current;
                translation = responseTranslator.Translate(chunk, allowMultipleToolCalls: true);
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

            usage.SetLatest(translation.Usage);
            responseId = string.IsNullOrWhiteSpace(chunk.ResponseId)
                ? responseId
                : chunk.ResponseId;

            if (translation.UnsupportedPartKinds.Count > 0)
            {
                await telemetry.UnsupportedResponseAsync(
                    "Gemini",
                    translation.UnsupportedPartKinds,
                    cancellationToken);
                throw GeminiExceptionMapper.UnsupportedResponse(translation.UnsupportedPartKinds);
            }

            var terminalStatus = GeminiResponseTranslator.GetTerminalStatus(chunk);
            if (terminalStatus.IsTerminal && !terminalStatus.IsSuccess)
            {
                throw GeminiExceptionMapper.IncompleteResponse(terminalStatus.Detail!);
            }

            if (translation.Contents.Count == 0)
            {
                if (terminalStatus.IsSuccess)
                {
                    if (usage.CreateContent() is { } usageContent)
                    {
                        yield return ProviderResponseUpdates.Create(
                            modelId,
                            responseId,
                            responseId,
                            usageContent);
                    }

                    await telemetry.CompletedAsync("Provider stream ended without events.", cancellationToken);
                    yield break;
                }

                continue;
            }

            await telemetry.RecordFirstEventAsync(translation.FirstEventKind, cancellationToken);

            yield return ProviderResponseUpdates.Create(
                modelId,
                responseId,
                responseId,
                translation.Contents);

            if (terminalStatus.IsSuccess)
            {
                if (usage.CreateContent() is { } usageContent)
                {
                    yield return ProviderResponseUpdates.Create(
                        modelId,
                        responseId,
                        responseId,
                        usageContent);
                }

                await telemetry.CompletedAsync("Provider stream ended without events.", cancellationToken);
                yield break;
            }
        }

        throw GeminiExceptionMapper.IncompleteResponse(
            "The Gemini event stream ended before a successful terminal finish reason.");
    }

    private static IAsyncEnumerable<GenerateContentResponse> CreateStream(
        Client client,
        List<GenAIContent> contents,
        GenerateContentConfig config,
        string modelId,
        CancellationToken cancellationToken)
    {
        try
        {
            return client.Models.GenerateContentStreamAsync(
                model: ProviderModelId.RemovePrefix(modelId, "gemini"),
                contents: contents,
                config: config,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw GeminiExceptionMapper.Request(ex);
        }
    }
}
