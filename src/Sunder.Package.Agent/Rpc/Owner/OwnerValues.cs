using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Owner;

[SunderRpcTransparent]
internal readonly record struct Uuid { public required Guid Value { get; init; } }
[SunderRpcTransparent]
internal readonly record struct Timestamp { public required SunderRpcOffsetDateTime Value { get; init; } }
[SunderRpcTransparent]
internal readonly record struct ModelReleaseDate { public required DateOnly Value { get; init; } }
[SunderRpcTransparent]
internal readonly record struct JsonText { public required string Value { get; init; } }
[SunderRpcTransparent]
internal sealed class ContentReference { public required SunderRpcContentReference Value { get; init; } }
[SunderRpcTransparent, SunderRpcDefinition("JSONObject"), SunderRpcFacet(JsonObjectOnly = true)]
internal readonly record struct JSONObject { public required JsonElement Value { get; init; } }

[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentTurnKind { Message = 0, ToolCall = 1, ToolResult = 2 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentMessageRole { System = 0, User = 1, Assistant = 2, Tool = 3 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentRunStatus { Idle = 0, Running = 1, Interrupted = 2, Stopped = 3, Completed = 4, Failed = 5, WaitingForApproval = 6 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentSessionState { Active = 0, Interrupted = 1, Stopped = 2, Completed = 3, Failed = 5 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentTurnItemKind { Text = 0, ToolCall = 1, ToolResult = 2, Attachment = 3 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentToolExecutionStatus { Prepared = 0, Started = 1, Completed = 2, Failed = 3, Ambiguous = 4 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentPendingPermissionStatus { Pending = 0, Claimed = 1, Executed = 2, Denied = 3, Failed = 4, Expired = 5 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentTranscriptPageDirection { Recent = 0, Before = 1, After = 2, Turn = 3 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentAuthMode { ApiKey = 0, CodexConnected = 1 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentProviderReadinessStatus { Ready = 0, NeedsConfiguration = 1, Failed = 2 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentToolConcurrencyMode { Sequential = 0, ParallelSafe = 1 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentToolPriority { Low = 0, Medium = 1, High = 2 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentToolSelectionScope { Tool = 0, Group = 1 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentToolReadinessStatus { Ready = 0, Failed = 3 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentReasoningEffort { None = 0, Low = 1, Medium = 2, High = 3, ExtraHigh = 4 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentProfileCommandKind { Create = 0, Save = 1, Delete = 2 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentWorkspaceCommandKind { Create = 0, Save = 1, Delete = 2, Warmup = 3 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentSessionCommandKind { Create = 0, Update = 1, Delete = 2 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentRunCommandKind { Start = 0, RollbackAndStart = 1, Stop = 2, ApprovePermission = 3, DenyPermission = 4 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentRunCommandStatus { Pending = 0, Committed = 1, Absent = 2 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentPermissionCommandKind { Read = 0, SetUnrestricted = 1, SaveOverride = 2, DeleteOverride = 3 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentPermissionDecision { Allow = 0, Ask = 1, Deny = 2 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentExecutionTargetWarmupStatus { Ready = 0, Skipped = 1, Failed = 2 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentAttachmentKind { Text = 0, Image = 1, Pdf = 2, Audio = 3, Video = 4, Binary = 5 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum HistoryActivityKind { None = 0, Read = 1, Search = 2, Use = 3, Edit = 4, Delete = 5, Execute = 6 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum HistorySearchRoleFilter { Any = 0, User = 1, Assistant = 2 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum HistoryAnchorKind { Text = 0, Activity = 1 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum HistorySearchAvailability { Starting = 0, Ready = 1, Rebuilding = 2, Clearing = 3, Unavailable = 4 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum HistorySearchCommandKind { ConfigureSemantic = 0, Rebuild = 1, ClearDerivedIndex = 2 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentRuntimeChangeKind { Connected = 0, ResnapshotRequired = 1, Profile = 2, Catalog = 3, Workspace = 4, Session = 5, Turn = 6, TranscriptReset = 7, RunActivity = 8, Permission = 9, TurnMutation = 10 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentRunActivityKind { Thinking = 0, Reasoning = 1, Tool = 2, Processing = 3 }
[SunderRpcNumericEnum(IncludeRangeBounds = false)] internal enum AgentTurnMutationKind { Add = 0, Append = 1, Replace = 2, Complete = 3 }
