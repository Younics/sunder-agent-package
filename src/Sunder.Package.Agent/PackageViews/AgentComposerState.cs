using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

internal sealed class AgentComposerState
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, AgentComposerSubmission> _pendingSubmissions = [];

    public string Text { get; set; } = string.Empty;

    public Guid? RollbackTurnId { get; set; }

    public ObservableCollection<AgentPendingAttachmentViewModel> Attachments { get; } = [];

    public bool IsSendPending(Guid sessionId)
    {
        lock (_syncRoot)
        {
            return _pendingSubmissions.ContainsKey(sessionId);
        }
    }

    public IReadOnlyList<AgentComposerSubmission> GetCompletedUncommittedSubmissions()
    {
        lock (_syncRoot)
        {
            return _pendingSubmissions.Values
                .Where(submission => submission.IsCommandCompleted && !submission.IsCommitted)
                .ToArray();
        }
    }

    public AgentComposerSubmission? TryBeginSubmission(
        Guid sessionId,
        IReadOnlySet<Guid>? existingTurnIds = null)
        => TryBeginSubmission(
            sessionId,
            Text,
            Attachments.ToArray(),
            RollbackTurnId,
            existingTurnIds ?? new HashSet<Guid>(),
            useUserTurnCorrelation: true);

    public AgentComposerSubmission? TryBeginSubmission(
        Guid sessionId,
        string text,
        IReadOnlyList<AgentPendingAttachmentViewModel> attachments,
        Guid? rollbackTurnId,
        IReadOnlySet<Guid> existingTurnIds,
        bool useUserTurnCorrelation = true)
    {
        lock (_syncRoot)
        {
            if (_pendingSubmissions.ContainsKey(sessionId))
            {
                return null;
            }

            var submission = new AgentComposerSubmission(
                sessionId,
                Guid.NewGuid(),
                text,
                attachments,
                rollbackTurnId,
                existingTurnIds,
                useUserTurnCorrelation,
                DateTimeOffset.UtcNow);
            _pendingSubmissions.Add(sessionId, submission);
            return submission;
        }
    }

    public AgentComposerSubmission? CommitAuthoritativeUserTurn(AgentTurnRecord turn)
    {
        lock (_syncRoot)
        {
            if (turn.Role != AgentMessageRole.User
                || !_pendingSubmissions.TryGetValue(turn.SessionId, out var submission)
                || submission.IsCommitted
                || !submission.Matches(turn))
            {
                return null;
            }

            submission.Commit();
            return submission;
        }
    }

    public bool IsPendingAuthoritativeUserTurn(AgentTurnRecord turn)
    {
        lock (_syncRoot)
        {
            return _pendingSubmissions.TryGetValue(turn.SessionId, out var submission)
                   && !submission.IsCommitted
                   && submission.Matches(turn);
        }
    }

    public void ClearSubmitted(AgentComposerSubmission submission)
    {
        if (string.Equals(Text, submission.Text, StringComparison.Ordinal))
        {
            Text = string.Empty;
        }

        if (Attachments.Select(attachment => attachment.AttachmentId)
            .SequenceEqual(submission.Attachments.Select(attachment => attachment.AttachmentId)))
        {
            Attachments.Clear();
        }

        if (RollbackTurnId == submission.RollbackTurnId)
        {
            RollbackTurnId = null;
        }
    }

    public bool RestoreUncommitted(AgentComposerSubmission submission, Guid? selectedSessionId)
    {
        if (submission.IsCommitted || selectedSessionId != submission.SessionId)
        {
            return false;
        }

        var restored = false;
        if (string.IsNullOrEmpty(Text))
        {
            Text = submission.Text;
            restored = true;
        }

        if (Attachments.Count == 0)
        {
            foreach (var attachment in submission.Attachments)
            {
                Attachments.Add(attachment);
            }

            restored |= submission.Attachments.Count > 0;
        }

        if (RollbackTurnId is null && submission.RollbackTurnId is not null)
        {
            RollbackTurnId = submission.RollbackTurnId;
            restored = true;
        }

        return restored;
    }

    public bool EndSubmission(AgentComposerSubmission submission)
    {
        lock (_syncRoot)
        {
            return _pendingSubmissions.TryGetValue(submission.SessionId, out var current)
                   && ReferenceEquals(current, submission)
                   && _pendingSubmissions.Remove(submission.SessionId);
        }
    }
}

internal sealed class AgentComposerSubmission(
    Guid sessionId,
    Guid userTurnId,
    string text,
    IReadOnlyList<AgentPendingAttachmentViewModel> attachments,
    Guid? rollbackTurnId,
    IReadOnlySet<Guid> existingTurnIds,
    bool useUserTurnCorrelation,
    DateTimeOffset startedAtUtc)
{
    private const int CommittedFlag = 1;
    private const int CommandCompletedFlag = 2;
    private const int CompleteState = CommittedFlag | CommandCompletedFlag;
    private readonly TaskCompletionSource _authoritativeReconciliation =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;

    public Guid SessionId { get; } = sessionId;

    public Guid UserTurnId { get; } = userTurnId;

    public string Text { get; } = text;

    public IReadOnlyList<AgentPendingAttachmentViewModel> Attachments { get; } = attachments;

    public Guid? RollbackTurnId { get; } = rollbackTurnId;

    public IReadOnlySet<Guid> ExistingTurnIds { get; } = existingTurnIds;

    public bool UseUserTurnCorrelation { get; } = useUserTurnCorrelation;

    public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;

    public bool IsCommitted => (Volatile.Read(ref _state) & CommittedFlag) != 0;

    public bool IsCommandCompleted => (Volatile.Read(ref _state) & CommandCompletedFlag) != 0;

    public bool IsComplete => Volatile.Read(ref _state) == CompleteState;

    public Task AuthoritativeReconciliation => _authoritativeReconciliation.Task;

    public bool Commit() => SetState(CommittedFlag);

    public bool CompleteCommand() => SetState(CommandCompletedFlag);

    public void CompleteAuthoritativeReconciliation()
        => _authoritativeReconciliation.TrySetResult();

    public void FailAuthoritativeReconciliation(Exception exception)
        => _authoritativeReconciliation.TrySetException(exception);

    public bool Matches(AgentTurnRecord turn)
    {
        if (turn.Role != AgentMessageRole.User
            || turn.SessionId != SessionId)
        {
            return false;
        }
        if (UseUserTurnCorrelation)
        {
            return turn.TurnId == UserTurnId;
        }
        if (ExistingTurnIds.Contains(turn.TurnId)
            || turn.CreatedAtUtc < StartedAtUtc
            || !string.Equals(
                TranscriptRowProjector<object>.ExtractTextContent(turn),
                string.IsNullOrWhiteSpace(Text) ? string.Empty : Text,
                StringComparison.Ordinal))
        {
            return false;
        }

        var turnAttachments = turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Attachment)
            .OrderBy(item => item.SequenceNumber)
            .Select(TryReadAttachmentMetadata)
            .ToArray();
        if (turnAttachments.Length != Attachments.Count
            || turnAttachments.Any(metadata => metadata is null))
        {
            return false;
        }

        for (var index = 0; index < turnAttachments.Length; index++)
        {
            var metadata = turnAttachments[index]!;
            var attachment = Attachments[index];
            var upload = attachment.UploadRequest;
            var sha256 = Convert.ToHexString(SHA256.HashData(upload.Content)).ToLowerInvariant();
            if (!string.Equals(metadata.FileName, attachment.FileName, StringComparison.Ordinal)
                || metadata.SizeBytes != upload.Content.LongLength
                || !string.Equals(metadata.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private bool SetState(int flag)
        => (Interlocked.Or(ref _state, flag) | flag) == CompleteState;

    private static AgentAttachmentMetadata? TryReadAttachmentMetadata(AgentTurnItemRecord item)
    {
        if (string.IsNullOrWhiteSpace(item.StructuredPayloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentAttachmentMetadata>(item.StructuredPayloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
