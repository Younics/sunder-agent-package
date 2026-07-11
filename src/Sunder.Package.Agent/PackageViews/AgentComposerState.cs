using System.Collections.ObjectModel;

namespace Sunder.Package.Agent.PackageViews;

internal sealed class AgentComposerState
{
    private readonly object _syncRoot = new();
    private readonly HashSet<Guid> _pendingSessionIds = [];

    public string Text { get; set; } = string.Empty;

    public Guid? RollbackTurnId { get; set; }

    public ObservableCollection<AgentPendingAttachmentViewModel> Attachments { get; } = [];

    public bool IsSendPending(Guid sessionId)
    {
        lock (_syncRoot)
        {
            return _pendingSessionIds.Contains(sessionId);
        }
    }

    public AgentComposerSubmission? TryBeginSubmission(
        Guid sessionId,
        IReadOnlySet<Guid>? existingTurnIds = null)
    {
        lock (_syncRoot)
        {
            if (!_pendingSessionIds.Add(sessionId))
            {
                return null;
            }
        }

        return new AgentComposerSubmission(
            sessionId,
            Text,
            Attachments.ToArray(),
            RollbackTurnId,
            existingTurnIds ?? new HashSet<Guid>());
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
            return _pendingSessionIds.Remove(submission.SessionId);
        }
    }
}

internal sealed class AgentComposerSubmission(
    Guid sessionId,
    string text,
    IReadOnlyList<AgentPendingAttachmentViewModel> attachments,
    Guid? rollbackTurnId,
    IReadOnlySet<Guid> existingTurnIds)
{
    public Guid SessionId { get; } = sessionId;

    public string Text { get; } = text;

    public IReadOnlyList<AgentPendingAttachmentViewModel> Attachments { get; } = attachments;

    public Guid? RollbackTurnId { get; } = rollbackTurnId;

    public IReadOnlySet<Guid> ExistingTurnIds { get; } = existingTurnIds;

    public bool IsCommitted { get; private set; }

    public void Commit() => IsCommitted = true;
}
