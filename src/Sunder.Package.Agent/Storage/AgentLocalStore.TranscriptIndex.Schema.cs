namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private const string TranscriptIndexSql = """
        CREATE TABLE AgentTranscriptEntries (
            EntryId TEXT PRIMARY KEY,
            SessionId TEXT NOT NULL,
            TurnId TEXT NOT NULL,
            TurnSequence INTEGER NOT NULL CHECK (TurnSequence >= 1),
            ItemSequence INTEGER NOT NULL,
            ExecutionId TEXT NULL UNIQUE,
            State INTEGER NULL CHECK (State IN (0, 1, 2)),
            GroupId TEXT NULL,
            CompactVisible INTEGER NOT NULL CHECK (CompactVisible IN (0, 1)),
            UNIQUE (SessionId, TurnSequence, ItemSequence)
        );
        CREATE INDEX IX_TranscriptEntries_Position ON AgentTranscriptEntries(SessionId, TurnSequence, ItemSequence);
        CREATE INDEX IX_TranscriptEntries_Compact ON AgentTranscriptEntries(SessionId, TurnSequence, ItemSequence) WHERE CompactVisible = 1;
        CREATE INDEX IX_TranscriptEntries_Group ON AgentTranscriptEntries(SessionId, GroupId, TurnSequence, ItemSequence);
        CREATE INDEX IX_TranscriptEntries_Turn ON AgentTranscriptEntries(TurnId, ItemSequence);

        CREATE TABLE AgentTranscriptGroups (
            GroupId TEXT PRIMARY KEY,
            SessionId TEXT NOT NULL,
            RunId TEXT NULL,
            RunRevision INTEGER NULL,
            FirstEntryId TEXT NOT NULL,
            LastEntryId TEXT NOT NULL,
            Revision INTEGER NOT NULL CHECK (Revision >= 1),
            RunningCount INTEGER NOT NULL CHECK (RunningCount >= 0),
            CompletedCount INTEGER NOT NULL CHECK (CompletedCount >= 0),
            FailedCount INTEGER NOT NULL CHECK (FailedCount >= 0)
        );
        CREATE INDEX IX_TranscriptGroups_Session ON AgentTranscriptGroups(SessionId);

        CREATE TABLE AgentTranscriptGroupTools (
            GroupId TEXT NOT NULL,
            ToolId TEXT NOT NULL,
            FirstTurnSequence INTEGER NOT NULL,
            FirstItemSequence INTEGER NOT NULL,
            Count INTEGER NOT NULL CHECK (Count >= 1),
            PRIMARY KEY (GroupId, ToolId)
        );
        CREATE UNIQUE INDEX IX_ToolExecutions_CallItem ON AgentToolExecutions(CallItemId) WHERE CallItemId IS NOT NULL;
        CREATE UNIQUE INDEX IX_ToolExecutions_ResultItem ON AgentToolExecutions(ResultItemId) WHERE ResultItemId IS NOT NULL;
        CREATE INDEX IX_ToolExecutions_Recording ON AgentToolExecutions(SessionId, CallId)
            WHERE RunRevision = 0 AND CallItemId IS NOT NULL AND ResultItemId IS NULL;
        CREATE UNIQUE INDEX IX_TurnItems_ExecutionKind ON AgentTurnItems(ToolExecutionId, Kind)
            WHERE Kind IN ('ToolCall', 'ToolResult');
        """;
}
