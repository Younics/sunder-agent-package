using System.Diagnostics;
using System.Runtime.CompilerServices;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Provider.Shared;
using GenAIContent = Google.GenAI.Types.Content;

namespace Sunder.Package.Agent.Provider.Gemini;

internal sealed class GeminiCompletionTransport(
    GeminiResponseTranslator responseTranslator,
    GeminiTelemetry telemetry)
{
    public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        Client client,
        List<GenAIContent> contents,
        GenerateContentConfig config,
        string modelId,
        bool allowMultipleToolCalls,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (translation, responseId, elapsedMilliseconds) = await ExecuteAsync(
            client,
            contents,
            config,
            modelId,
            allowMultipleToolCalls,
            cancellationToken);

        if (translation.UnsupportedPartKinds.Count > 0)
        {
            await telemetry.UnsupportedResponseAsync(
                translation.UnsupportedPartKinds,
                elapsedMilliseconds,
                cancellationToken);
            throw GeminiExceptionMapper.UnsupportedResponse(translation.UnsupportedPartKinds);
        }

        if (translation.Contents.Count > 0)
        {
            await telemetry.FirstEventAsync(translation.FirstEventKind, elapsedMilliseconds, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, translation.Contents.ToList())
            {
                ResponseId = responseId,
                MessageId = responseId,
                ModelId = modelId,
            };
        }

        await telemetry.CompletedAsync(
            translation.Contents.Count > 0,
            elapsedMilliseconds,
            cancellationToken);
    }

    private async Task<(GeminiResponseTranslation Translation, string ResponseId, long ElapsedMilliseconds)> ExecuteAsync(
        Client client,
        List<GenAIContent> contents,
        GenerateContentConfig config,
        string modelId,
        bool allowMultipleToolCalls,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
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
                string.IsNullOrWhiteSpace(response.ResponseId) ? Guid.NewGuid().ToString("N") : response.ResponseId,
                stopwatch.ElapsedMilliseconds);
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
    }
}
