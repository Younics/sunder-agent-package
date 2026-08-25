using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Protocol;

#pragma warning disable MEAI001

public static class AgentChatProviderRpc
{
    public const string ServiceId = "chat-provider";
    internal const string StreamQueueLimitErrorCode = "rpc.worker.stream-queue-limit";
    internal const string UnsafeRpcFailureErrorCode = "rpc.worker.unsafe-dispatch";
    private const int MaxCoalescedSourceEvents = 16;
    private const int MaxCoalescedContentBytes = 64 * 1024;

    public static ISunderRpcServiceHandler CreateHandler(IAgentChatProvider provider)
    {
        return AgentRpcServiceHandler.Create(AgentRpcContractDescriptors.Get(AgentRpcContractIds.ChatProvider))
            .AddUnary<AgentRpcEmpty, AgentProviderDescriptor>(ServiceId, "describe", (_, _, _) => ValueTask.FromResult(provider.Descriptor))
            .AddUnary<AgentRpcEmpty, IReadOnlyList<AgentModelDescriptor>>(ServiceId, "list-models", (_, _, token) => provider.GetAvailableModelsAsync(token))
            .AddUnary<AgentRpcEmpty, AgentProviderReadiness>(ServiceId, "get-readiness", (_, _, token) => provider.GetReadinessAsync(token))
            .AddUnary<ChatCapabilitiesRequest, AgentProviderRunCapabilities>(ServiceId, "get-capabilities", (_, request, token) => provider.GetRunCapabilitiesAsync(request.ModelId, token))
            .AddNullableUnary<AgentRpcEmpty, string>(ServiceId, "resolve-utility-model", async (_, _, token) =>
                provider is IAgentUtilityModelProvider utility
                    ? await utility.ResolveUtilityModelIdAsync(token).ConfigureAwait(false)
                    : null)
            .AddStream<AgentChatStreamRequest, AgentChatStreamEvent>(ServiceId, "stream-chat", (context, request, token) =>
                StreamAsync(provider, request, context, token))
            .Build();
    }

    private static async IAsyncEnumerable<AgentChatStreamEvent> StreamAsync(
        IAgentChatProvider provider,
        AgentChatStreamRequest request,
        SunderRpcInvocationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<AgentChatStreamEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(
            provider,
            request,
            context,
            channel.Writer,
            producerCancellation.Token);
        var streamConsumed = false;
        try
        {
            AgentChatStreamEvent? pending = null;
            while (pending is not null
                   || await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AgentChatStreamEvent item;
                if (pending is not null)
                {
                    item = pending;
                    pending = null;
                }
                else if (!channel.Reader.TryRead(out item!))
                {
                    continue;
                }

                if (item is AgentChatUpdateEvent update
                    && TryGetCoalescibleContentSize(update, out var contentBytes))
                {
                    List<AgentChatContent>? mergedContents = null;
                    var sourceEventCount = 1;
                    while (sourceEventCount < MaxCoalescedSourceEvents
                           && channel.Reader.TryRead(out var next))
                    {
                        if (next is not AgentChatUpdateEvent nextUpdate
                            || !HasMatchingEnvelope(update, nextUpdate)
                            || !TryGetCoalescibleContentSize(nextUpdate, out var nextContentBytes)
                            || contentBytes + nextContentBytes > MaxCoalescedContentBytes)
                        {
                            pending = next;
                            break;
                        }

                        mergedContents ??= new List<AgentChatContent>(update.Contents);
                        mergedContents.AddRange(nextUpdate.Contents);
                        contentBytes += nextContentBytes;
                        sourceEventCount++;
                    }

                    if (mergedContents is not null)
                    {
                        item = update with { ContentItems = mergedContents };
                    }
                }

                yield return item;
            }

            streamConsumed = true;
            await producer.ConfigureAwait(false);
        }
        finally
        {
            if (!streamConsumed)
            {
                try
                {
                    producerCancellation.Cancel();
                }
                catch
                {
                    // Producer completion remains authoritative during stream teardown.
                }
                try
                {
                    await producer.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (producerCancellation.IsCancellationRequested)
                {
                }
            }
        }
    }

    private static bool HasMatchingEnvelope(
        AgentChatUpdateEvent first,
        AgentChatUpdateEvent second)
        => string.Equals(first.Role, second.Role, StringComparison.Ordinal)
           && string.Equals(first.ResponseId, second.ResponseId, StringComparison.Ordinal)
           && string.Equals(first.MessageId, second.MessageId, StringComparison.Ordinal)
           && string.Equals(first.ConversationId, second.ConversationId, StringComparison.Ordinal)
           && string.Equals(first.ModelId, second.ModelId, StringComparison.Ordinal)
           && first.CreatedAt == second.CreatedAt;

    private static bool TryGetCoalescibleContentSize(
        AgentChatUpdateEvent update,
        out int contentBytes)
    {
        contentBytes = 0;
        if (update.FinishReason is not null
            || update.ContinuationHandle is not null
            || update.Contents.Count == 0)
        {
            return false;
        }

        long estimate = 0;
        foreach (var content in update.Contents)
        {
            if (content.Kind is not ("text" or "reasoning"))
            {
                return false;
            }

            estimate += Encoding.UTF8.GetByteCount(content.Text ?? string.Empty);
            estimate += Encoding.UTF8.GetByteCount(content.ProtectedData ?? string.Empty);
            if (estimate > MaxCoalescedContentBytes)
            {
                contentBytes = MaxCoalescedContentBytes + 1;
                return true;
            }
        }

        contentBytes = (int)estimate;
        return true;
    }

    private static async Task ProduceAsync(
        IAgentChatProvider provider,
        AgentChatStreamRequest request,
        SunderRpcInvocationContext context,
        ChannelWriter<AgentChatStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        IChatClient? chatClient = null;
        Exception? primaryException = null;
        var providerFailurePublished = false;
        try
        {
            chatClient = await provider.CreateChatClientAsync(
                new AgentChatClientContext(request.ProviderId, request.ModelId),
                cancellationToken).ConfigureAwait(false);
            var messages = await AgentChatWireMapper.ToChatMessagesAsync(
                request.Messages,
                context,
                cancellationToken).ConfigureAwait(false);
            await foreach (var update in chatClient.GetStreamingResponseAsync(
                               messages,
                               AgentChatWireMapper.ToChatOptions(request.Options),
                               cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteAsync(AgentChatWireMapper.FromUpdate(update), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (AgentChatProviderException exception)
        {
            primaryException = exception;
            try
            {
                await writer.WriteAsync(AgentChatStreamEvent.Error(
                    ProjectRpcFailureErrorCode(exception)
                    ?? exception.ErrorCode
                    ?? "provider-error",
                    exception.Message,
                    exception.Content,
                    exception.FailureKind), cancellationToken).ConfigureAwait(false);
                providerFailurePublished = true;
            }
            catch (Exception publishException)
            {
                primaryException = publishException;
            }
        }
        catch (Exception exception)
        {
            primaryException = exception;
        }
        finally
        {
            try
            {
                chatClient?.Dispose();
            }
            catch (Exception cleanupException)
            {
                primaryException ??= cleanupException;
            }
            finally
            {
                writer.TryComplete();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!providerFailurePublished && primaryException is not null)
        {
            ExceptionDispatchInfo.Capture(primaryException).Throw();
        }
    }

    private static string? ProjectRpcFailureErrorCode(Exception exception)
    {
        var pending = new Stack<Exception>();
        var containsUnsafeRpcFailure = false;
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is SunderRpcException rpcException)
            {
                if (string.Equals(
                        rpcException.Error.Code,
                        StreamQueueLimitErrorCode,
                        StringComparison.Ordinal))
                {
                    return StreamQueueLimitErrorCode;
                }
                containsUnsafeRpcFailure |= rpcException.Error.RetrySafety == SunderRpcRetrySafety.Unsafe;
            }

            if (current is AggregateException aggregate)
            {
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                {
                    pending.Push(aggregate.InnerExceptions[index]);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return containsUnsafeRpcFailure ? UnsafeRpcFailureErrorCode : null;
    }

    internal static SunderRpcException? CreateProjectedRpcException(AgentChatErrorEvent error)
    {
        var kind = error.ErrorCode switch
        {
            StreamQueueLimitErrorCode => SunderRpcErrorKind.ResourceExhausted,
            UnsafeRpcFailureErrorCode => SunderRpcErrorKind.Unavailable,
            _ => (SunderRpcErrorKind?)null,
        };
        return kind is null
            ? null
            : new SunderRpcException(new SunderRpcError(
                kind.Value,
                error.ErrorCode,
                error.ErrorMessage,
                SunderRpcDispatchState.PossiblyDispatched,
                SunderRpcRetrySafety.Unsafe));
    }

    internal static AgentChatProviderException CreateProviderException(AgentChatErrorEvent error)
        => new(
            error.ErrorMessage,
            error.ErrorContent,
            error.ErrorCode,
            CreateProjectedRpcException(error))
        {
            FailureKind = error.ErrorFailureKind,
        };
}

public sealed class AgentChatProviderRpcClient(ISunderRpcClient client, SunderRpcEndpointReference endpoint) : IAgentChatProvider, IAgentUtilityModelProvider
{
    private AgentProviderDescriptor? _descriptor;

    public AgentProviderDescriptor Descriptor
        => _descriptor ??= AgentRpcServiceHandler.InvokeAsync<AgentRpcEmpty, AgentProviderDescriptor>(client, endpoint, AgentChatProviderRpc.ServiceId, "describe", new AgentRpcEmpty()).AsTask().GetAwaiter().GetResult();

    public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        => AgentRpcServiceHandler.InvokeAsync<AgentRpcEmpty, IReadOnlyList<AgentModelDescriptor>>(client, endpoint, AgentChatProviderRpc.ServiceId, "list-models", new AgentRpcEmpty(), cancellationToken);

    public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
        => AgentRpcServiceHandler.InvokeAsync<AgentRpcEmpty, AgentProviderReadiness>(client, endpoint, AgentChatProviderRpc.ServiceId, "get-readiness", new AgentRpcEmpty(), cancellationToken);

    public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
        => AgentRpcServiceHandler.InvokeAsync<ChatCapabilitiesRequest, AgentProviderRunCapabilities>(client, endpoint, AgentChatProviderRpc.ServiceId, "get-capabilities", new(modelId), cancellationToken);

    public async ValueTask<string?> ResolveUtilityModelIdAsync(CancellationToken cancellationToken = default)
        => await AgentRpcServiceHandler.InvokeNullableAsync<AgentRpcEmpty, string>(
            client,
            endpoint,
            AgentChatProviderRpc.ServiceId,
            "resolve-utility-model",
            new AgentRpcEmpty(),
            cancellationToken).ConfigureAwait(false);

    public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IChatClient>(new AgentRpcChatClient(client, endpoint, context.ProviderId, context.ModelId));
    }
}

internal sealed class AgentRpcChatClient(
    ISunderRpcClient client,
    SunderRpcEndpointReference endpoint,
    string providerId,
    string modelId) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => await GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ISunderRpcCallScope? scope = null;
        try
        {
            var mappedMessages = await AgentChatWireMapper.FromChatMessagesAsync(
                messages,
                RegisterContentAsync,
                cancellationToken).ConfigureAwait(false);
            var request = new AgentChatStreamRequest(
                providerId,
                modelId,
                mappedMessages,
                AgentChatWireMapper.FromChatOptions(options));
            ISunderRpcClient invocationClient = scope is null ? client : scope;
            await foreach (var item in AgentRpcServiceHandler.SubscribeAsync<AgentChatStreamRequest, AgentChatStreamEvent>(
                               invocationClient,
                               endpoint,
                               AgentChatProviderRpc.ServiceId,
                               "stream-chat",
                               request,
                               cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (item is AgentChatErrorEvent error)
                {
                    throw AgentChatProviderRpc.CreateProviderException(error);
                }
                yield return AgentChatWireMapper.ToUpdate((AgentChatUpdateEvent)item);
            }
        }
        finally
        {
            if (scope is not null)
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
        }

        async ValueTask<SunderRpcContentReference> RegisterContentAsync(
            DataContent content,
            CancellationToken token)
        {
            scope ??= await client.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
            return await AgentChatWireMapper.RegisterContentAsync(
                scope,
                endpoint,
                content,
                token).ConfigureAwait(false);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal static class AgentChatWireMapper
{
    internal const int MaxContentBytes = 32 * 1024 * 1024;

    public static async ValueTask<IReadOnlyList<AgentChatMessage>> FromChatMessagesAsync(
        IEnumerable<ChatMessage> messages,
        Func<DataContent, CancellationToken, ValueTask<SunderRpcContentReference>> registerContentAsync,
        CancellationToken cancellationToken)
    {
        var mappedMessages = new List<AgentChatMessage>();
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mappedContents = new List<AgentChatContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                if (content is DataContent data && !data.Data.IsEmpty)
                {
                    var reference = await registerContentAsync(data, cancellationToken).ConfigureAwait(false);
                    mappedContents.Add(FromContentReference(data, reference));
                }
                else
                {
                    mappedContents.Add(FromContent(content));
                }
            }
            mappedMessages.Add(FromChatMessage(message, mappedContents));
        }
        return mappedMessages;
    }

    public static async ValueTask<SunderRpcContentReference> RegisterContentAsync(
        ISunderRpcCallScope scope,
        SunderRpcEndpointReference endpoint,
        DataContent content,
        CancellationToken cancellationToken)
    {
        if (content.Data.Length > MaxContentBytes)
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.ResourceExhausted,
                "rpc.content.length-limit",
                "Chat binary content exceeds the transfer limit."));
        }

        await using var source = new MemoryStream(content.Data.ToArray(), writable: false);
        return await scope.RegisterContentAsync(
            endpoint,
            source,
            new SunderRpcContentRegistrationOptions(
                content.MediaType,
                string.IsNullOrWhiteSpace(content.Name) ? "content.bin" : content.Name,
                content.Data.Length,
                Repeatability: SunderRpcContentRepeatability.SingleUse,
                MaximumUses: 1),
            cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<IReadOnlyList<ChatMessage>> ToChatMessagesAsync(
        IReadOnlyList<AgentChatMessage> messages,
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken)
    {
        var contentByReference = new Dictionary<SunderRpcContentReference, ReadOnlyMemory<byte>>();
        foreach (var reference in messages
                     .SelectMany(static message => message.Contents)
                     .Select(static content => content.ContentReference)
                     .Where(static reference => reference is not null)
                     .Cast<SunderRpcContentReference>()
                     .Distinct())
        {
            if (reference.Length is < 0 or > MaxContentBytes)
            {
                throw new InvalidDataException("Chat binary content exceeds the transfer limit.");
            }

            await using var source = await context.OpenContentAsync(reference, cancellationToken).ConfigureAwait(false);
            var bytes = new byte[checked((int)reference.Length)];
            await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            contentByReference.Add(reference, bytes);
        }

        return messages
            .Select(message => ToChatMessage(message, reference => contentByReference[reference]))
            .ToArray();
    }

    public static AgentChatMessage FromChatMessage(ChatMessage message)
        => FromChatMessage(message, message.Contents.Select(FromContent).ToArray());

    public static AgentChatMessage FromChatMessage(
        ChatMessage message,
        IReadOnlyList<AgentChatContent> contents)
        => new(
            message.Role.Value,
            message.MessageId,
            message.AuthorName,
            contents);

    public static ChatMessage ToChatMessage(
        AgentChatMessage message,
        Func<SunderRpcContentReference, ReadOnlyMemory<byte>>? resolveContent = null)
        => new(new ChatRole(message.Role), message.Contents.Select(content => ToContent(content, resolveContent)).ToList())
        {
            MessageId = message.MessageId,
            AuthorName = message.AuthorName,
        };

    public static AgentChatOptions FromChatOptions(ChatOptions? options)
    {
        options ??= new ChatOptions();
        return new AgentChatOptions(
            options.Instructions,
            options.ConversationId,
            options.ModelId,
            options.Temperature,
            options.MaxOutputTokens,
            options.TopP,
            options.TopK,
            options.FrequencyPenalty,
            options.PresencePenalty,
            options.Seed,
            options.AllowMultipleToolCalls,
            options.Reasoning?.Effort.ToString() is { } effort
                ? JsonNamingPolicy.KebabCaseLower.ConvertName(effort)
                : null,
            options.Tools?.OfType<AIFunctionDeclaration>().Select(static tool => new AgentChatTool(
                tool.Name,
                tool.Description,
                tool.JsonSchema.Clone())).ToArray() ?? [],
            options.AdditionalProperties?.ToDictionary(
                static pair => pair.Key,
                static pair => JsonSerializer.SerializeToElement(pair.Value),
                StringComparer.Ordinal),
            options.ContinuationToken is null
                ? null
                : options.ContinuationToken.ToBytes().ToArray());
    }

    public static ChatOptions ToChatOptions(AgentChatOptions options)
    {
        var mapped = new ChatOptions
        {
            Instructions = options.Instructions,
            ConversationId = options.ConversationId,
            ModelId = options.ModelId,
            Temperature = options.Temperature,
            MaxOutputTokens = checked((int?)options.MaxOutputTokens),
            TopP = options.TopP,
            TopK = checked((int?)options.TopK),
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            Seed = options.Seed,
            AllowMultipleToolCalls = options.AllowMultipleToolCalls,
            Tools = options.Tools.Select(static tool => (AITool)AIFunctionFactory.CreateDeclaration(
                tool.Name,
                tool.Description,
                tool.JsonSchema)).ToList(),
        };
        if (Enum.TryParse<ReasoningEffort>(options.ReasoningEffort?.Replace("-", string.Empty), ignoreCase: true, out var effort))
        {
            mapped.Reasoning = new ReasoningOptions { Effort = effort, Output = effort == ReasoningEffort.None ? ReasoningOutput.None : ReasoningOutput.Summary };
        }
        if (options.AdditionalProperties is { Count: > 0 })
        {
            mapped.AdditionalProperties = new AdditionalPropertiesDictionary();
            foreach (var pair in options.AdditionalProperties)
            {
                mapped.AdditionalProperties[pair.Key] = DeserializeAdditionalProperty(pair.Value);
            }
        }
        if (options.ContinuationHandle is { Length: > 0 })
        {
            mapped.ContinuationToken = ResponseContinuationToken.FromBytes(options.ContinuationHandle);
        }
        return mapped;
    }

    public static AgentChatUpdateEvent FromUpdate(ChatResponseUpdate update)
        => new(
            update.Role?.Value,
            update.ResponseId,
            update.MessageId,
            update.ConversationId,
            update.ModelId,
            update.FinishReason?.Value,
            update.CreatedAt is { } createdAt ? AgentRpcServiceHandler.ToWireTimestamp(createdAt) : null,
            update.Contents.Select(FromContent).ToArray(),
            update.ContinuationToken?.ToBytes().ToArray());

    public static ChatResponseUpdate ToUpdate(AgentChatUpdateEvent item)
        => new(
            item.Role is null ? null : new ChatRole(item.Role),
            item.Contents.Select(static content => ToContent(content, resolveContent: null)).ToList())
        {
            ResponseId = item.ResponseId,
            MessageId = item.MessageId,
            ConversationId = item.ConversationId,
            ModelId = item.ModelId,
            FinishReason = item.FinishReason is null ? null : new ChatFinishReason(item.FinishReason),
            CreatedAt = item.CreatedAt is { } createdAt
                ? AgentRpcServiceHandler.FromWireTimestamp(createdAt)
                : null,
            ContinuationToken = item.ContinuationHandle is null
                ? null
                : ResponseContinuationToken.FromBytes(item.ContinuationHandle),
        };

    public static AgentChatContent FromContent(AIContent content)
        => content switch
        {
            TextReasoningContent reasoning => new("reasoning", Text: reasoning.Text, ProtectedData: reasoning.ProtectedData),
            TextContent text => new("text", Text: text.Text),
            FunctionCallContent call => new("tool-call", CallId: call.CallId, Name: call.Name, Json: new(JsonSerializer.SerializeToElement(call.Arguments)), InformationalOnly: call.InformationalOnly),
            FunctionResultContent result => new("tool-result", CallId: result.CallId, Json: new(JsonSerializer.SerializeToElement(result.Result))),
            UsageContent usage => new("usage", Usage: new AgentChatUsage(
                usage.Details.InputTokenCount,
                usage.Details.OutputTokenCount,
                usage.Details.TotalTokenCount,
                usage.Details.CachedInputTokenCount,
                usage.Details.ReasoningTokenCount)),
            ErrorContent error => new("error", ErrorCode: error.ErrorCode, ErrorMessage: error.Message, Json: new(JsonSerializer.SerializeToElement(error.Details))),
            DataContent data => new("content-reference", Name: data.Name, MediaType: data.MediaType, ContentUri: data.Uri is null ? null : new SunderRpcUri(data.Uri)),
            _ => new("error", ErrorCode: "unsupported-content", ErrorMessage: $"Unsupported AI content type '{content.GetType().Name}'."),
        };

    public static AgentChatContent FromContentReference(
        DataContent content,
        SunderRpcContentReference reference)
        => new(
            "content-reference",
            Name: content.Name,
            MediaType: content.MediaType,
            ContentReference: reference);

    private static AIContent ToContent(
        AgentChatContent content,
        Func<SunderRpcContentReference, ReadOnlyMemory<byte>>? resolveContent)
        => content.Kind switch
        {
            "reasoning" => new TextReasoningContent(content.Text ?? string.Empty) { ProtectedData = content.ProtectedData },
            "text" => new TextContent(content.Text ?? string.Empty),
            "tool-call" => new FunctionCallContent(
                content.CallId ?? string.Empty,
                content.Name ?? string.Empty,
                DeserializeDictionary(content.Json)) { InformationalOnly = content.InformationalOnly },
            "tool-result" => new FunctionResultContent(content.CallId ?? string.Empty, DeserializeObject(content.Json)),
            "usage" => new UsageContent(new UsageDetails
            {
                InputTokenCount = content.Usage?.InputTokens,
                OutputTokenCount = content.Usage?.OutputTokens,
                TotalTokenCount = content.Usage?.TotalTokens,
                CachedInputTokenCount = content.Usage?.CachedInputTokens,
                ReasoningTokenCount = content.Usage?.ReasoningTokens,
            }),
            "content-reference" when content.ContentReference is { } reference && resolveContent is not null
                => new DataContent(resolveContent(reference), reference.MediaType) { Name = reference.FileName },
            "content-reference" when content.ContentUri is { } contentUri
                && Uri.TryCreate(contentUri.Value, UriKind.Absolute, out var uri)
                => new DataContent(uri, content.MediaType ?? "application/octet-stream") { Name = content.Name },
            "error" => new ErrorContent(content.ErrorMessage ?? "Provider error.") { ErrorCode = content.ErrorCode, Details = DeserializeText(content.Json) },
            _ => new ErrorContent("Unsupported Agent RPC chat content."),
        };

    private static IDictionary<string, object?> DeserializeDictionary(SunderRpcOptional<JsonElement> json)
        => !json.TryGetValue(out var value) || value.ValueKind == JsonValueKind.Null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : value.Deserialize<Dictionary<string, object?>>()
              ?? new Dictionary<string, object?>(StringComparer.Ordinal);

    private static object? DeserializeObject(SunderRpcOptional<JsonElement> json)
        => json.TryGetValue(out var value) && value.ValueKind != JsonValueKind.Null
            ? value.Clone()
            : null;

    private static string? DeserializeText(SunderRpcOptional<JsonElement> json)
        => json.TryGetValue(out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    private static object? DeserializeAdditionalProperty(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Number when value.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => value.GetDouble(),
            _ => value.Clone(),
        };
    }
}

public sealed record ChatCapabilitiesRequest(string? ModelId);
public sealed record AgentChatStreamRequest(string ProviderId, string ModelId, IReadOnlyList<AgentChatMessage> Messages, AgentChatOptions Options);
public sealed record AgentChatMessage(string Role, string? MessageId, string? AuthorName, IReadOnlyList<AgentChatContent> Contents);
public sealed record AgentChatTool(string Name, string? Description, JsonElement JsonSchema);
public sealed record AgentChatOptions(
    string? Instructions,
    string? ConversationId,
    string? ModelId,
    float? Temperature,
    long? MaxOutputTokens,
    float? TopP,
    long? TopK,
    float? FrequencyPenalty,
    float? PresencePenalty,
    long? Seed,
    bool? AllowMultipleToolCalls,
    string? ReasoningEffort,
    IReadOnlyList<AgentChatTool> Tools,
    IReadOnlyDictionary<string, JsonElement>? AdditionalProperties,
    byte[]? ContinuationHandle);
public sealed record AgentChatContent(
    string Kind,
    string? Text = null,
    string? ProtectedData = null,
    string? CallId = null,
    string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    SunderRpcOptional<JsonElement> Json = default,
    bool InformationalOnly = false,
    string? MediaType = null,
    SunderRpcUri? ContentUri = null,
    SunderRpcContentReference? ContentReference = null,
    AgentChatUsage? Usage = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
public sealed record AgentChatUsage(long? InputTokens, long? OutputTokens, long? TotalTokens, long? CachedInputTokens, long? ReasoningTokens);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AgentChatUpdateEvent), UpdateKind)]
[JsonDerivedType(typeof(AgentChatErrorEvent), ErrorKind)]
public abstract record AgentChatStreamEvent
{
    public const string UpdateKind = "update";
    public const string ErrorKind = "error";

    private protected AgentChatStreamEvent()
    {
    }

    public static AgentChatErrorEvent Error(
        string code,
        string message,
        string content,
        AgentChatProviderFailureKind failureKind)
        => new(code, message, content, failureKind);
}

public sealed record AgentChatUpdateEvent(
    string? Role = null,
    string? ResponseId = null,
    string? MessageId = null,
    string? ConversationId = null,
    string? ModelId = null,
    string? FinishReason = null,
    SunderRpcOffsetDateTime? CreatedAt = null,
    IReadOnlyList<AgentChatContent>? ContentItems = null,
    byte[]? ContinuationHandle = null) : AgentChatStreamEvent
{
    [JsonIgnore]
    public IReadOnlyList<AgentChatContent> Contents => ContentItems ?? [];
}

public sealed record AgentChatErrorEvent(
    string ErrorCode,
    string ErrorMessage,
    string ErrorContent,
    AgentChatProviderFailureKind ErrorFailureKind) : AgentChatStreamEvent;
