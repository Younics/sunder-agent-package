using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.LMStudio.Rpc;

// Package-owned streaming semantics; generated bindings own dispatch and wire validation.
internal static class ChatStream
{
    private const int MaxCoalescedSourceEvents = 16;
    private const int MaxCoalescedContentBytes = 64 * 1024;
    private const string StreamQueueLimitErrorCode = "rpc.worker.stream-queue-limit";
    private const string UnsafeRpcFailureErrorCode = "rpc.worker.unsafe-dispatch";

    internal static async IAsyncEnumerable<AgentChatStreamEvent> ReadAsync(IAgentChatProvider provider, AgentChatStreamRequest request,
        SunderRpcInvocationContext context, [EnumeratorCancellation] CancellationToken token)
    {
        var channel = Channel.CreateBounded<AgentChatStreamEvent>(new BoundedChannelOptions(64)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var producer = ProduceAsync(provider, request, context, channel.Writer, lifetime.Token);
        var consumed = false;
        try
        {
            AgentChatStreamEvent? pending = null;
            while (pending is not null || await channel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                AgentChatStreamEvent item;
                if (pending is not null) { item = pending; pending = null; }
                else if (!channel.Reader.TryRead(out item!)) continue;
                if (item is AgentChatUpdateEvent update && TryGetContentSize(update, out var bytes))
                {
                    List<AgentChatContent>? merged = null;
                    var count = 1;
                    while (count < MaxCoalescedSourceEvents && channel.Reader.TryRead(out var next))
                    {
                        if (next is not AgentChatUpdateEvent nextUpdate || !SameEnvelope(update, nextUpdate)
                            || !TryGetContentSize(nextUpdate, out var nextBytes) || bytes + nextBytes > MaxCoalescedContentBytes)
                        { pending = next; break; }
                        merged ??= new(update.Contents);
                        merged.AddRange(nextUpdate.Contents); bytes += nextBytes; count++;
                    }
                    if (merged is not null) item = update with { ContentItems = merged };
                }
                yield return item;
            }
            consumed = true;
            await producer.ConfigureAwait(false);
        }
        finally
        {
            if (!consumed)
            {
                try { lifetime.Cancel(); }
                catch { /* Producer completion remains authoritative during teardown. */ }
                try { await producer.ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }
    }

    private static bool SameEnvelope(AgentChatUpdateEvent first, AgentChatUpdateEvent second)
        => first.Role == second.Role && first.ResponseId == second.ResponseId && first.MessageId == second.MessageId
           && first.ConversationId == second.ConversationId && first.ModelId == second.ModelId && first.CreatedAt == second.CreatedAt;

    private static bool TryGetContentSize(AgentChatUpdateEvent update, out int bytes)
    {
        bytes = 0;
        if (update.FinishReason is not null || update.ContinuationHandle is not null || update.Contents.Count == 0) return false;
        long estimate = 0;
        foreach (var content in update.Contents)
        {
            if (content.Kind is not ("text" or "reasoning")) return false;
            estimate += Encoding.UTF8.GetByteCount(content.Text ?? string.Empty);
            estimate += Encoding.UTF8.GetByteCount(content.ProtectedData ?? string.Empty);
            if (estimate > MaxCoalescedContentBytes) { bytes = MaxCoalescedContentBytes + 1; return true; }
        }
        bytes = (int)estimate; return true;
    }

    private static async Task ProduceAsync(IAgentChatProvider provider, AgentChatStreamRequest request,
        SunderRpcInvocationContext context, ChannelWriter<AgentChatStreamEvent> writer, CancellationToken token)
    {
        IChatClient? client = null;
        Exception? failure = null;
        var published = false;
        try
        {
            client = await provider.CreateChatClientAsync(new(request.ProviderId, request.ModelId), token).ConfigureAwait(false);
            var messages = await ChatValues.MessagesAsync(request.Messages, context, token).ConfigureAwait(false);
            await foreach (var update in client.GetStreamingResponseAsync(messages, ChatValues.Options(request.Options), token).WithCancellation(token).ConfigureAwait(false))
                await writer.WriteAsync(ChatValues.Update(update), token).ConfigureAwait(false);
        }
        catch (AgentChatProviderException error)
        {
            failure = error;
            try
            {
                await writer.WriteAsync(AgentChatStreamEvent.Error(ProjectRpcFailure(error) ?? error.ErrorCode ?? "provider-error",
                    error.Message, error.Content, error.FailureKind), token).ConfigureAwait(false);
                published = true;
            }
            catch (Exception publishError) { failure = publishError; }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { client?.Dispose(); }
            catch (Exception cleanupError) { failure ??= cleanupError; }
            finally { writer.TryComplete(); }
        }
        token.ThrowIfCancellationRequested();
        if (!published && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string? ProjectRpcFailure(Exception exception)
    {
        var pending = new Stack<Exception>();
        var unsafeFailure = false;
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is SunderRpcException rpc)
            {
                if (rpc.Error.Code == StreamQueueLimitErrorCode) return StreamQueueLimitErrorCode;
                unsafeFailure |= rpc.Error.RetrySafety == SunderRpcRetrySafety.Unsafe;
            }
            if (current is AggregateException aggregate)
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--) pending.Push(aggregate.InnerExceptions[index]);
            else if (current.InnerException is not null) pending.Push(current.InnerException);
        }
        return unsafeFailure ? UnsafeRpcFailureErrorCode : null;
    }
}
