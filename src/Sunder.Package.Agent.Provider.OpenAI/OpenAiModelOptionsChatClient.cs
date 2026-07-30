using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Provider.OpenAI;

#pragma warning disable OPENAI001, SCME0001

internal sealed class OpenAiModelOptionsChatClient(IChatClient inner) : IChatClient
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

    private static ChatOptions? PrepareOptions(ChatOptions? options)
    {
        var speedOptionId = GetOption(options, AgentChatModelOptionKeys.SpeedOptionId);
        var modeOptionId = GetOption(options, AgentChatModelOptionKeys.ModeOptionId);
        if (!string.Equals(speedOptionId, "fast", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(modeOptionId, "pro", StringComparison.OrdinalIgnoreCase))
        {
            return options;
        }

        var preparedOptions = options?.Clone() ?? new ChatOptions();
        var rawRepresentationFactory = preparedOptions.RawRepresentationFactory;
        preparedOptions.RawRepresentationFactory = client => CreateRawOptions(
            rawRepresentationFactory?.Invoke(client) as CreateResponseOptions,
            GetOption(preparedOptions, AgentChatModelOptionKeys.SpeedOptionId),
            GetOption(preparedOptions, AgentChatModelOptionKeys.ModeOptionId),
            preparedOptions.Reasoning);
        return preparedOptions;
    }

    private static CreateResponseOptions CreateRawOptions(
        CreateResponseOptions? rawOptions,
        string? speedOptionId,
        string? modeOptionId,
        ReasoningOptions? reasoning)
    {
        rawOptions ??= new CreateResponseOptions();
        if (string.Equals(speedOptionId, "fast", StringComparison.OrdinalIgnoreCase))
        {
            rawOptions.ServiceTier = new ResponseServiceTier("priority");
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

    private static string? GetOption(ChatOptions? options, string key) =>
        options?.AdditionalProperties is { } properties
        && properties.TryGetValue(key, out var value)
            ? value as string
            : null;
}
