using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Builder;

/// <summary>A bounded package-private relay, not a general App RPC provider or desktop-control endpoint.</summary>
internal sealed class BuilderDevelopmentCommandHub(TimeProvider? timeProvider = null) : IDisposable
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Channel<Pending> _queue = Channel.CreateBounded<Pending>(32);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private string? _appId;
    private long _observed;
    private bool _disposed;
    internal bool IsConnected { get { lock (_sync) return !_disposed && _appId is not null && _clock.GetElapsedTime(_observed) < TimeSpan.FromSeconds(15); } }

    internal string Connect(string appId)
    {
        if (!Guid.TryParseExact(appId, "N", out _)) throw Unavailable("The App activation identity is invalid.");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsConnected && _appId != appId) throw Unavailable("Another exact Maker App activation owns the development bridge.");
            _appId = appId; _observed = _clock.GetTimestamp(); return appId;
        }
    }

    internal async Task<BuilderDevelopmentReply> InvokeAsync(string operation, string operationId, string requestJson,
        string? expectedAppId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BuilderContent.ValidateRequest(requestJson);
        if (operation is not ("info" or "logs" or "query" or "view-query" or "screenshot" or "observe" or "apply" or "open-view" or "input-view"))
            throw new BuilderDevelopmentRequestException(new("maker.invalid-request", "Unknown Maker App operation."));
        Pending pending;
        lock (_sync)
        {
            if (!IsConnected || expectedAppId is not null && expectedAppId != _appId)
                throw RequestUnavailable("The original Maker App activation is unavailable. Do not replay the operation against a replacement.");
            if (_pending.Count >= 32) throw RequestUnavailable("The bounded Maker App request queue is full.");
            pending = new(new(Guid.NewGuid().ToString("N"), operation, operationId, requestJson), _appId!);
            _pending.Add(pending.Command.RequestId, pending);
            if (!_queue.Writer.TryWrite(pending)) { _pending.Remove(pending.Command.RequestId); throw RequestUnavailable("The bounded Maker App request queue is full."); }
        }
        try { return await pending.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            lock (_sync)
            {
                pending.CallerStopped = true;
                // This slot owns only the reply, not the operation. Once read by
                // the App, acceptance may have happened; the tool retains its
                // original App/operation binding for observation, never replay.
                _pending.Remove(pending.Command.RequestId);
            }
            throw;
        }
    }

    internal async Task<BuilderDevelopmentCommand?> ReadAsync(string appId, CancellationToken token)
    {
        lock (_sync)
        {
            if (_appId != appId || _disposed) throw Unavailable("The Maker App bridge binding is stale.");
            _observed = _clock.GetTimestamp();
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            while (await _queue.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false))
            {
                lock (_sync)
                {
                    if (_appId != appId || _disposed) throw Unavailable("The Maker App bridge binding was replaced during observation.");
                    if (!_queue.Reader.TryRead(out var pending)) continue;
                    if (pending.CallerStopped || pending.AppId != appId)
                    {
                        _pending.Remove(pending.Command.RequestId);
                        if (pending.CallerStopped) pending.Completion.TrySetCanceled();
                        else pending.Completion.TrySetException(RequestUnavailable("The selected App activation stopped before this request was admitted."));
                        continue;
                    }
                    pending.Dispatched = true;
                    return pending.Command;
                }
            }
            return null;
        }
        catch (OperationCanceledException error) when (error.CancellationToken == wait.Token && wait.IsCancellationRequested)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException(error.Message, error, token);
            return null;
        }
    }

    internal void Complete(string appId, string requestId, string responseJson, string? error)
    {
        using var json = JsonDocument.Parse(responseJson, new JsonDocumentOptions { MaxDepth = 32 });
        var failure = error is null ? null : BuilderDevelopmentFailure.Decode(error);
        lock (_sync)
        {
            // Lost acknowledgements and late replies are idempotent no-ops.
            // An unknown reply cannot create/admit an operation or consume a
            // different pending request, and never refreshes the App lease.
            if (!_pending.TryGetValue(requestId, out var pending)) return;
            if (!pending.Dispatched || pending.AppId != appId)
                throw Unavailable("No request belongs to this exact App activation and response identity.");
            // Images have an independent 2 MiB binary budget (base64 plus envelope
            // on this private relay). Ordinary diagnostic limits are unchanged.
            var budget = pending.Command.Operation == "screenshot" ? 3 * 1024 * 1024 : 256 * 1024;
            if (responseJson.Length > budget || Encoding.UTF8.GetByteCount(responseJson) > budget)
                throw Unavailable("The bounded Maker App response is too large.");
            _pending.Remove(requestId);
            if (failure is null) pending.Completion.TrySetResult(new(appId, json.RootElement.GetRawText()));
            else pending.Completion.TrySetException(new BuilderDevelopmentRequestException(failure));
        }
    }
    private static SunderRpcException Unavailable(string message) => new(new(SunderRpcErrorKind.Unavailable, "maker.development.unavailable", message));
    private static BuilderDevelopmentRequestException RequestUnavailable(string message, bool unknown = false)
        => new(new("maker.development.unavailable", message, unknown));
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true; _queue.Writer.TryComplete();
            foreach (var pending in _pending.Values) pending.Completion.TrySetException(RequestUnavailable("The original Maker Worker activation stopped.",
                pending.Dispatched && pending.Command.Operation is "apply" or "open-view" or "input-view"));
            _pending.Clear();
        }
    }
    private sealed class Pending(BuilderDevelopmentCommand command, string appId)
    {
        internal BuilderDevelopmentCommand Command { get; } = command;
        internal string AppId { get; } = appId;
        internal bool Dispatched { get; set; }
        internal bool CallerStopped { get; set; }
        internal TaskCompletionSource<BuilderDevelopmentReply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

[SunderPackageCodeRole(SunderPackageCodeRole.Shared)]
public sealed record BuilderDevelopmentCommand(string RequestId, string Operation, string OperationId, string RequestJson);
[SunderPackageCodeRole(SunderPackageCodeRole.Shared)]
internal sealed record BuilderDevelopmentReply(string AppId, string PayloadJson);
