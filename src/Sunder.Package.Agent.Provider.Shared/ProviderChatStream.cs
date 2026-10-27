using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Sdk.Rpc;
using AgentChatClientContext = Sunder.Package.Agent.Contracts.Models.AgentChatClientContext;
using AgentChatProviderException = Sunder.Package.Agent.Contracts.Models.AgentChatProviderException;
using AgentProviderFailureClassifier = Sunder.Package.Agent.Contracts.Models.AgentProviderFailureClassifier;
using ProviderFailureKind = Sunder.Package.Agent.Contracts.Models.AgentChatProviderFailureKind;

namespace Sunder.Package.Agent.Provider.Shared;

// Package-owned streaming semantics; generated bindings own dispatch and wire validation.
internal static class ProviderChatStream
{
    private const int MaxCoalescedSourceEvents = 16;
    private const int MaxCoalescedContentBytes = 64 * 1024;
    private const string StreamQueueLimitErrorCode = "rpc.worker.stream-queue-limit";
    private const string UnsafeRpcFailureErrorCode = "rpc.worker.unsafe-dispatch";

    internal static async IAsyncEnumerable<AgentChatStreamEvent> ReadAsync(
        Func<AgentChatClientContext, CancellationToken, ValueTask<IChatClient>> createClient,
        AgentChatStreamRequest request, [EnumeratorCancellation] CancellationToken token)
    {
        var channel = Channel.CreateBounded<AgentChatStreamEvent>(new BoundedChannelOptions(64)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var producer = ProduceAsync(createClient, request, channel.Writer, lifetime.Token);
        var consumed = false;
        AgentChatStreamEvent? pending = null;
        try
        {
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
                        merged ??= new(update.ContentItems ?? []);
                        merged.AddRange(nextUpdate.ContentItems ?? []); bytes += nextBytes; count++;
                    }
                    if (merged is not null) item = update with { ContentItems = merged };
                }
                try { yield return item; }
                finally { await ProviderChatValues.DisposeContentAsync(item).ConfigureAwait(false); }
            }
            consumed = true;
            await producer.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (!consumed)
                {
                    try { lifetime.Cancel(); }
                    catch { /* Producer completion remains authoritative during teardown. */ }
                    try { await producer.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                }
            }
            finally
            {
                await ProviderChatValues.DisposeContentAsync(pending).ConfigureAwait(false);
                while (channel.Reader.TryRead(out var remaining))
                    await ProviderChatValues.DisposeContentAsync(remaining).ConfigureAwait(false);
            }
        }
    }

    private static bool SameEnvelope(AgentChatUpdateEvent first, AgentChatUpdateEvent second)
        => first.Role == second.Role && first.ResponseId == second.ResponseId && first.MessageId == second.MessageId
           && first.ConversationId == second.ConversationId && first.ModelId == second.ModelId && first.CreatedAt == second.CreatedAt;

    private static bool TryGetContentSize(AgentChatUpdateEvent update, out int bytes)
    {
        bytes = 0;
        if (update.FinishReason is not null || update.ContinuationHandle is not null || update.ContentItems is not { Count: > 0 } contents) return false;
        long estimate = 0;
        foreach (var content in contents)
        {
            if (content.Kind is not ("text" or "reasoning")) return false;
            estimate += Encoding.UTF8.GetByteCount(content.Text ?? string.Empty);
            estimate += Encoding.UTF8.GetByteCount(content.ProtectedData ?? string.Empty);
            if (estimate > MaxCoalescedContentBytes) { bytes = MaxCoalescedContentBytes + 1; return true; }
        }
        bytes = (int)estimate; return true;
    }

    private static async Task ProduceAsync(
        Func<AgentChatClientContext, CancellationToken, ValueTask<IChatClient>> createClient,
        AgentChatStreamRequest request, ChannelWriter<AgentChatStreamEvent> writer, CancellationToken token)
    {
        IChatClient? client = null;
        Exception? failure = null;
        var published = false;
        try
        {
            client = await createClient(new(request.ProviderId, request.ModelId), token).ConfigureAwait(false);
            var messages = await ProviderChatValues.MessagesAsync(request.Messages, token).ConfigureAwait(false);
            await foreach (var update in client.GetStreamingResponseAsync(messages, ProviderChatValues.Options(request.Options), token).WithCancellation(token).ConfigureAwait(false))
            {
                var item = await ProviderChatValues.UpdateAsync(update).ConfigureAwait(false);
                try { await writer.WriteAsync(item, token).ConfigureAwait(false); }
                catch
                {
                    await ProviderChatValues.DisposeContentAsync(item).ConfigureAwait(false);
                    throw;
                }
            }
        }
        catch (AgentChatProviderException error)
        {
            failure = error;
            var normalized = AgentProviderFailureClassifier.Normalize(error);
            try
            {
                await writer.WriteAsync(new AgentChatErrorEvent
                {
                    ErrorCode = ProjectRpcFailure(error) ?? normalized.ErrorCode ?? "provider-error",
                    ErrorMessage = normalized.Message,
                    ErrorContent = normalized.Content,
                    ErrorFailureKind = Enum.Parse<AgentChatProviderFailureKind>(normalized.FailureKind.ToString()),
                    StatusCode = normalized.StatusCode,
                    RetryAfterMilliseconds = normalized.RetryAfterMilliseconds,
                }, token).ConfigureAwait(false);
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
                unsafeFailure |= rpc.Error.DispatchState != SunderRpcDispatchState.NotDispatched
                    || rpc.Error.RetrySafety != SunderRpcRetrySafety.Safe
                    || rpc.Error.Kind is not (SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.DeadlineExceeded);
            }
            if (current is AggregateException aggregate)
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--) pending.Push(aggregate.InnerExceptions[index]);
            else if (current.InnerException is not null) pending.Push(current.InnerException);
        }
        return unsafeFailure ? UnsafeRpcFailureErrorCode : null;
    }
}
