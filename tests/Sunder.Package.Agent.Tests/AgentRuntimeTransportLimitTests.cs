using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRuntimeTransportLimitTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AgentTransportLimits_MirrorCoreDefaults()
    {
        Assert.Equal(1024 * 1024, AgentRuntimePayloadLimits.RuntimeMaximumRequestBytes);
        Assert.Equal(4 * 1024 * 1024, AgentRuntimePayloadLimits.RuntimeMaximumResponseBytes);
        Assert.Equal(1024 * 1024, AgentRuntimePayloadLimits.RuntimeMaximumEventBytes);
    }

    [Fact]
    public async Task AttachmentGateway_ChunksUploadsAndDownloadsBelowCoreTransportLimits()
    {
        var upload = CreateBytes((5 * 1024 * 1024) + 137);
        var download = CreateBytes((7 * 1024 * 1024) + 251);
        var client = new BoundedAttachmentRuntimeClient(download);
        using var gateway = new AgentAppRuntimeGateway(client);
        var sessionId = Guid.NewGuid();

        _ = await gateway.QueueUserMessageAsync(
            sessionId,
            "profile",
            "Inspect the attachment.",
            "workspace",
            [new AgentAttachmentUploadRequest("large.bin", "application/octet-stream", upload)]);
        var read = await gateway.ReadAttachmentBytesAsync(new AgentAttachmentMetadata(
            Guid.NewGuid(),
            "download.bin",
            "application/octet-stream",
            AgentAttachmentKind.Binary,
            download.Length,
            string.Empty,
            "unused",
            IsText: false,
            WasTruncated: false));

        Assert.Equal(upload, client.UploadedContent);
        Assert.Equal(download, read);
        Assert.NotNull(client.RunCommand);
        Assert.Single(client.RunCommand!.AttachmentHandles!);
        Assert.True(client.MaximumRequestBytes < AgentRuntimePayloadLimits.RuntimeMaximumRequestBytes);
        Assert.True(client.MaximumResponseBytes < AgentRuntimePayloadLimits.RuntimeMaximumResponseBytes);
        Assert.True(client.UploadChunkCount > 1);
        Assert.True(client.DownloadChunkCount > 1);
    }

    [Fact]
    public async Task AttachmentGateway_RejectsEscapedRunMessageAboveSerializedRequestLimit()
    {
        var client = new BoundedAttachmentRuntimeClient([]);
        using var gateway = new AgentAppRuntimeGateway(client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateway.QueueUserMessageAsync(
                Guid.NewGuid(),
                "profile",
                new string('\u0001', 180 * 1024),
                "workspace",
                []));

        Assert.Contains("transport limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(client.RunCommand);
    }

    [Fact]
    public void TranscriptAndChangeProjections_FitCoreResponseAndEventLimits()
    {
        var turn = CreateTurn(new string('\\', 6 * 1024 * 1024));
        var page = AgentRuntimePayloadLimits.FitTranscriptPage(
            new AgentTranscriptPage(7, [turn], HasMore: false),
            AgentTranscriptPageDirection.Turn);

        Assert.True(page.HasMore);
        Assert.Single(page.Turns);
        Assert.True(Assert.Single(page.Turns[0].Items).WasTruncated);
        Assert.True(
            AgentRuntimePayloadLimits.GetSerializedByteCount(page)
            <= AgentRuntimePayloadLimits.MaximumOperationResponseBytes);

        var oversized = new AgentRuntimeChange(
            8,
            AgentRuntimeChangeKind.Turn,
            SessionId: turn.SessionId,
            Turn: turn,
            RuntimeInstanceId: "runtime");
        var projected = AgentRuntimePayloadLimits.ReplaceWithResnapshotIfOversized(
            oversized,
            oversized);

        Assert.Equal(AgentRuntimeChangeKind.ResnapshotRequired, projected.Kind);
        Assert.True(
            AgentRuntimePayloadLimits.GetSerializedByteCount(projected)
            <= AgentRuntimePayloadLimits.MaximumStreamEventBytes);
    }

    private static AgentTurnRecord CreateTurn(string content)
    {
        var turnId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                0,
                AgentTurnItemKind.Text,
                content,
                null,
                null,
                null,
                null,
                null,
                null,
                WasTruncated: false,
                IsError: false,
                ErrorCode: null,
                BackendId: null)],
            now,
            now);
    }

    private static byte[] CreateBytes(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }
        return bytes;
    }

    private sealed class BoundedAttachmentRuntimeClient(byte[] downloadContent)
        : IPackageRuntimeClient
    {
        private readonly Dictionary<string, MemoryStream> _uploads = new(StringComparer.Ordinal);
        private readonly byte[] _downloadContent = downloadContent;

        public bool IsAvailable => true;
        public int MaximumRequestBytes { get; private set; }
        public int MaximumResponseBytes { get; private set; }
        public int UploadChunkCount { get; private set; }
        public int DownloadChunkCount { get; private set; }
        public byte[]? UploadedContent { get; private set; }
        public AgentRunCommand? RunCommand { get; private set; }

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions).Length;
            MaximumRequestBytes = Math.Max(MaximumRequestBytes, requestBytes);
            if (requestBytes > AgentRuntimePayloadLimits.RuntimeMaximumRequestBytes)
            {
                throw new InvalidDataException("Request exceeded the simulated Core transport limit.");
            }

            object response;
            if (ReferenceEquals(operation, AgentRuntimeOperations.AttachmentTransfers))
            {
                response = HandleTransfer((AgentAttachmentTransferRequest)(object)request);
            }
            else if (ReferenceEquals(operation, AgentRuntimeOperations.Runs))
            {
                RunCommand = (AgentRunCommand)(object)request;
                var handle = Assert.Single(RunCommand.AttachmentHandles!);
                UploadedContent = _uploads[handle.TransferId].ToArray();
                response = new AgentRunCommandResult(
                    1,
                    new AgentRunCheckpointRecord(
                        Guid.NewGuid(),
                        RunCommand.SessionId,
                        1,
                        AgentRunStatus.Running,
                        "Running.",
                        DateTimeOffset.UtcNow));
            }
            else
            {
                throw new NotSupportedException(operation.OperationId);
            }

            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions).Length;
            MaximumResponseBytes = Math.Max(MaximumResponseBytes, responseBytes);
            if (responseBytes > AgentRuntimePayloadLimits.RuntimeMaximumResponseBytes)
            {
                throw new InvalidDataException("Response exceeded the simulated Core transport limit.");
            }
            return ValueTask.FromResult((TResponse)response);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.CompletedTask;
            yield break;
        }

        private AgentAttachmentTransferResult HandleTransfer(AgentAttachmentTransferRequest request)
        {
            switch (request.Kind)
            {
                case AgentAttachmentTransferKind.BeginUpload:
                    var transferId = Guid.NewGuid().ToString("N");
                    _uploads.Add(transferId, new MemoryStream(request.Upload!.SizeBytes));
                    return new AgentAttachmentTransferResult(
                        transferId,
                        TotalBytes: request.Upload.SizeBytes);
                case AgentAttachmentTransferKind.WriteUploadChunk:
                    var upload = _uploads[request.TransferId!];
                    Assert.Equal(request.Offset, upload.Length);
                    upload.Write(request.Content!);
                    UploadChunkCount++;
                    return new AgentAttachmentTransferResult(
                        request.TransferId,
                        checked((int)upload.Length),
                        checked((int)upload.Capacity));
                case AgentAttachmentTransferKind.CompleteUpload:
                    var completed = _uploads[request.TransferId!];
                    return new AgentAttachmentTransferResult(
                        request.TransferId,
                        checked((int)completed.Length),
                        checked((int)completed.Length),
                        IsComplete: true);
                case AgentAttachmentTransferKind.AbortUpload:
                    return new AgentAttachmentTransferResult(IsComplete: true);
                case AgentAttachmentTransferKind.ReadDownloadChunk:
                    var count = Math.Min(
                        AgentRuntimePayloadLimits.AttachmentDownloadChunkBytes,
                        _downloadContent.Length - request.Offset);
                    var content = _downloadContent.AsSpan(request.Offset, count).ToArray();
                    DownloadChunkCount++;
                    return new AgentAttachmentTransferResult(
                        NextOffset: request.Offset + count,
                        TotalBytes: _downloadContent.Length,
                        Content: content,
                        IsComplete: request.Offset + count == _downloadContent.Length);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
