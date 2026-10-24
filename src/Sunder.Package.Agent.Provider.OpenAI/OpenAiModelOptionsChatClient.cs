using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.OpenAI;

#pragma warning disable OPENAI001, SCME0001

internal sealed class OpenAiModelOptionsChatClient(IChatClient inner, AgentChatClientContext? context = null) : IChatClient
{
    private readonly IChatClient _inner = inner;

    public ChatClientMetadata Metadata { get; } =
        inner.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata ?? new ChatClientMetadata("OpenAI");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _inner.GetResponseAsync(messages, PrepareOptions(options), cancellationToken)
                .ConfigureAwait(false);
            ThrowIfTerminalFailure(response);
            if (context is not null && response.RawRepresentation is ResponseResult raw)
                await new ProviderStreamTelemetry(context).ProcessingTierAsync(raw.ServiceTier?.ToString(), cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            if (OpenAiExceptionMapper.TryMapContextWindowExceeded(ex, out var providerException))
            {
                throw providerException;
            }

            throw;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<ChatResponseUpdate> enumerator;
        try
        {
            enumerator = _inner.GetStreamingResponseAsync(
                    messages,
                    PrepareOptions(options),
                    cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex)
        {
            if (OpenAiExceptionMapper.TryMapContextWindowExceeded(ex, out var providerException))
            {
                throw providerException;
            }

            throw;
        }

        var telemetry = context is null ? null : new ProviderStreamTelemetry(context);
        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }

                    update = enumerator.Current;
                }
                catch (Exception ex)
                {
                    if (OpenAiExceptionMapper.TryMapContextWindowExceeded(ex, out var providerException))
                    {
                        throw providerException;
                    }

                    throw;
                }

                ThrowIfTerminalFailure(update);
                if (telemetry is not null && update.RawRepresentation is StreamingResponseCompletedUpdate completed)
                    await telemetry.ProcessingTierAsync(completed.Response.ServiceTier?.ToString(), cancellationToken);
                yield return update;
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();

    private static void ThrowIfTerminalFailure(ChatResponse response)
    {
        foreach (var contentError in response.Messages
                     .SelectMany(message => message.Contents)
                     .OfType<ErrorContent>())
        {
            ThrowTerminalFailure(contentError.ErrorCode, contentError.Message, contentError.Details);
        }

        switch (response.RawRepresentation)
        {
            case ResponseResult { Error: { } error }:
                ThrowTerminalFailure(error.Code.ToString(), error.Message);
                break;
            case ResponseResult { IncompleteStatusDetails: { } incomplete }:
                ThrowTerminalFailure("response.incomplete", incomplete.Reason.ToString());
                break;
            default:
                ThrowIfTerminalFailure(response.AdditionalProperties);
                break;
        }
    }

    private static void ThrowIfTerminalFailure(ChatResponseUpdate update)
    {
        foreach (var contentError in update.Contents.OfType<ErrorContent>())
        {
            ThrowTerminalFailure(contentError.ErrorCode, contentError.Message, contentError.Details);
        }

        if (update.RawRepresentation is StreamingResponseFailedUpdate failedUpdate)
        {
            if (failedUpdate.Response.Error is { } error)
            {
                ThrowTerminalFailure(error.Code.ToString(), error.Message);
            }

            ThrowTerminalFailure("response.failed", "OpenAI returned a failed response without error details.");
        }
        else if (update.RawRepresentation is StreamingResponseIncompleteUpdate incompleteUpdate)
        {
            ThrowTerminalFailure(
                "response.incomplete",
                incompleteUpdate.Response.IncompleteStatusDetails?.Reason.ToString());
        }
        else
        {
            ThrowIfTerminalFailure(update.AdditionalProperties);
        }
    }

    private static void ThrowIfTerminalFailure(AdditionalPropertiesDictionary? properties)
    {
        if (properties is null)
        {
            return;
        }

        foreach (var key in new[] { "Error", "error", "response.failed", "response.incomplete" })
        {
            if (properties.TryGetValue(key, out var value))
            {
                ThrowTerminalFailure(key, value?.ToString());
            }
        }
    }

    private static void ThrowTerminalFailure(params string?[] details)
    {
        var diagnostic = string.Join(": ", details.Where(detail => !string.IsNullOrWhiteSpace(detail)));
        if (OpenAiExceptionMapper.TryMapContextWindowExceeded(
                new InvalidOperationException(diagnostic),
                out var providerException))
        {
            throw providerException;
        }

        throw new AgentChatProviderException(
            string.IsNullOrWhiteSpace(diagnostic)
                ? "OpenAI returned a terminal error response."
                : $"OpenAI returned a terminal error response: {diagnostic}",
            "### OpenAI request failed\n\nThe provider returned an error instead of a completed response.",
            "openai-response-failed");
    }

    private ChatOptions? PrepareOptions(ChatOptions? options)
    {
        var modelId = options?.ModelId ?? context?.ModelId;
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            var model = OpenAiModelCatalog.Find(modelId);
            AgentChatModelOptions.Validate(options, modelId, model?.Variants?.Select(v => v.VariantId),
                model?.SpeedOptions?.Select(v => v.SpeedOptionId), model?.ModeOptions?.Select(v => v.ModeOptionId));
        }
        var speedOptionId = AgentChatModelOptions.SpeedId(options);
        var modeOptionId = AgentChatModelOptions.ModeId(options);
        var reasoningId = AgentChatModelOptions.ReasoningId(options);
        if (!string.Equals(speedOptionId, "fast", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(speedOptionId, "normal", StringComparison.OrdinalIgnoreCase)
            && reasoningId is null
            && !string.Equals(modeOptionId, "pro", StringComparison.OrdinalIgnoreCase))
        {
            return options;
        }

        var preparedOptions = options?.Clone() ?? new ChatOptions();
        var rawRepresentationFactory = preparedOptions.RawRepresentationFactory;
        preparedOptions.RawRepresentationFactory = client => CreateRawOptions(
            rawRepresentationFactory?.Invoke(client) as CreateResponseOptions,
            speedOptionId,
            modeOptionId,
            preparedOptions.Reasoning,
            reasoningId);
        return preparedOptions;
    }

    private static CreateResponseOptions CreateRawOptions(
        CreateResponseOptions? rawOptions,
        string? speedOptionId,
        string? modeOptionId,
        ReasoningOptions? reasoning,
        string? reasoningId)
    {
        rawOptions ??= new CreateResponseOptions();
        if (string.Equals(speedOptionId, "fast", StringComparison.OrdinalIgnoreCase))
        {
            rawOptions.ServiceTier = new ResponseServiceTier("priority");
        }
        else if (string.Equals(speedOptionId, "normal", StringComparison.OrdinalIgnoreCase))
        {
            rawOptions.ServiceTier = new ResponseServiceTier("default");
        }

        if (reasoningId is not null)
        {
            var responseReasoning = rawOptions.ReasoningOptions ?? new ResponseReasoningOptions();
            responseReasoning.ReasoningEffortLevel = new ResponseReasoningEffortLevel(reasoningId);
            responseReasoning.ReasoningSummaryVerbosity = reasoningId == "none"
                ? null : ToOpenAiReasoningSummary(reasoning?.Output ?? ReasoningOutput.Summary);
            rawOptions.ReasoningOptions = responseReasoning;
        }

        if (string.Equals(modeOptionId, "pro", StringComparison.OrdinalIgnoreCase))
        {
            var responseReasoning = rawOptions.ReasoningOptions ?? new ResponseReasoningOptions();
            responseReasoning.ReasoningEffortLevel ??= ToOpenAiReasoningEffort(reasoning?.Effort);
            responseReasoning.ReasoningSummaryVerbosity ??= ToOpenAiReasoningSummary(reasoning?.Output);
            responseReasoning.Patch.Set("$.mode"u8, "pro");
            rawOptions.ReasoningOptions = responseReasoning;
        }

        return rawOptions;
    }

    private static ResponseReasoningEffortLevel? ToOpenAiReasoningEffort(ReasoningEffort? effort)
        => effort switch
        {
            ReasoningEffort.None => ResponseReasoningEffortLevel.None,
            ReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
            ReasoningEffort.Medium => ResponseReasoningEffortLevel.Medium,
            ReasoningEffort.High => ResponseReasoningEffortLevel.High,
            ReasoningEffort.ExtraHigh => new ResponseReasoningEffortLevel("xhigh"),
            _ => null,
        };

    private static ResponseReasoningSummaryVerbosity? ToOpenAiReasoningSummary(ReasoningOutput? output)
        => output switch
        {
            ReasoningOutput.Summary => ResponseReasoningSummaryVerbosity.Concise,
            ReasoningOutput.Full => ResponseReasoningSummaryVerbosity.Detailed,
            _ => null,
        };

}
