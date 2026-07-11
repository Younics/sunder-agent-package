using System.Diagnostics;
using System.Runtime.CompilerServices;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Provider.Shared;
using GenAIContent = Google.GenAI.Types.Content;

namespace Sunder.Package.Agent.Provider.Gemini;

internal sealed class GeminiStreamingTransport(
    GeminiResponseTranslator responseTranslator,
    GeminiTelemetry telemetry)
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
        var stopwatch = Stopwatch.StartNew();
        var firstEventRecorded = false;
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
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                await telemetry.FailedAsync(ex, stopwatch.ElapsedMilliseconds);
                throw GeminiExceptionMapper.ProviderTimeout(ex);
            }
            catch (OperationCanceledException)
            {
                await telemetry.CanceledAsync(stopwatch.ElapsedMilliseconds);
                throw;
            }
            catch (Exception ex)
            {
                await telemetry.FailedAsync(ex, stopwatch.ElapsedMilliseconds);
                throw GeminiExceptionMapper.Request(ex);
            }

            if (translation.UnsupportedPartKinds.Count > 0)
            {
                await telemetry.UnsupportedResponseAsync(
                    translation.UnsupportedPartKinds,
                    stopwatch.ElapsedMilliseconds,
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
                    await telemetry.CompletedAsync(firstEventRecorded, stopwatch.ElapsedMilliseconds, cancellationToken);
                    yield break;
                }

                continue;
            }

            if (!firstEventRecorded)
            {
                firstEventRecorded = true;
                await telemetry.FirstEventAsync(
                    translation.FirstEventKind,
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken);
            }

            var responseId = string.IsNullOrWhiteSpace(chunk.ResponseId)
                ? fallbackResponseId
                : chunk.ResponseId;
            yield return new ChatResponseUpdate(ChatRole.Assistant, translation.Contents.ToList())
            {
                ResponseId = responseId,
                MessageId = responseId,
                ModelId = modelId,
            };

            if (terminalStatus.IsSuccess)
            {
                await telemetry.CompletedAsync(firstEventRecorded, stopwatch.ElapsedMilliseconds, cancellationToken);
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
