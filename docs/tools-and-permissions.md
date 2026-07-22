# Static And Dynamic Tools

Agent tools are Runtime contributions. Choose `IAgentTool` for a fixed tool and `IAgentToolSource` for tools discovered from session, profile, workspace, or external state.

## Static Tools

Register each fixed `IAgentTool` through `PackageExtensionPoints.Tools`. The base Agent wraps all fixed tools in the `installed-packages` source.

An implementation provides:

- One stable `AgentToolDescriptor`.
- Cheap, cancellation-aware readiness through `GetReadinessAsync`.
- Validated execution through `ExecuteAsync`.
- Optional `IAgentPermissionAwareTool` for a specific approval request.
- Optional `IAgentToolPresentationResolver` for deterministic transcript presentation.

The [minimal sample](../samples/Sunder.Agent.Extension.Minimal/CurrentUtcTimeTool.cs) is a complete read-only static tool.

## Dynamic Tool Sources

Register an `IAgentToolSource` through `PackageExtensionPoints.ToolSources` when the catalog can change or needs `AgentToolSourceContext`:

```csharp
public interface IAgentToolSource
{
    string SourceId { get; }
    string DisplayName { get; }
    string SourceKind { get; }
    ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(...);
    ValueTask<AgentToolReadiness?> GetReadinessAsync(...);
    ValueTask<AgentToolResult> ExecuteAsync(...);
}
```

`AgentToolSourceContext` supplies the current session, profile, workspace, and selected execution binding. `AgentToolExecutionContext` additionally supplies durable run/revision/user-turn/tool-call correlation and whether an approved operation may cross configured scope.

Implement `IAgentNativeToolSource` when a framework-native `AIFunctionDeclaration` is needed. Otherwise Agent creates declarations from each descriptor's JSON Schema. Native declarations must use the same identity and schema semantics as their descriptors.

Agent re-lists and re-resolves a tool immediately before permission evaluation and execution. The current descriptor must match the advertised `ToolId`, `SourceKind`, `SourceId`, and `IsReadOnly` values, be ready, and remain assigned to the profile. This prevents stale or replaced tools from executing under an earlier advertisement.

## Descriptor Contract

| Property | Requirement |
| --- | --- |
| `ToolId` | Stable, non-empty, globally collision-resistant, and case-insensitively unique. Do not change it for display-name edits. |
| `DisplayName` / `Description` | User/model-facing text. Keep descriptions factual and bounded. |
| `IsReadOnly` | Security-sensitive. Set `false` if any successful path can mutate local, remote, account, process, or durable state. |
| `RequiresNetwork` | Declare network dependence. It is descriptive and does not itself authorize traffic. |
| `ArgumentsJsonSchema` | JSON object schema. Set `additionalProperties: false` unless unknown fields are intentionally supported. |
| `SourceKind`, `SourceId`, `SourceDisplayName` | Stable source identity. Agent fills missing fields from the source but explicit values are clearer. |
| `Aliases` | Compatibility lookup names only. Canonical results must use `ToolId`. Avoid ambiguous aliases. |
| `SelectionScope` | `Tool` for individual selection or `Group` for all tools selected through `SelectionGroupId`. |
| `ActivationRequirement` | Optional required profile capability kind/source/id. Use it when tool availability depends on another selected capability. |
| `RuntimeInstructions` | Trusted package-authored instructions added to the system prompt. Never place discovered server text, user content, tool output, or workspace data here. |
| `Priority` | Higher-priority tools are advertised first when several tools fit. It does not bypass profile selection or permissions. |
| `ConcurrencyMode` | Default `Sequential`. Use `ParallelSafe` only for independent, side-effect-free or concurrency-safe calls. |

Profiles explicitly select tools, tool groups, skills, and subagents. A tool being installed does not guarantee it is advertised to every profile. A profile with no matching assignments receives no unconditionally selectable tools.

## Arguments And Results

Use `AgentToolArgumentObject.TryParse` or equivalent strict parsing. It enforces the shared byte, depth, property-count, and case-collision limits. Validate required fields, ranges, enum values, paths, URLs, and cross-field constraints before any side effect.

Expected failures should return an `AgentToolResult` with:

- The canonical `ToolId`.
- A short `Summary` safe for transcript and model consumption.
- Bounded `Content` and optional structured JSON.
- `IsError = true` and a stable package-scoped `ErrorCode`.
- `WasTruncated = true` when output was bounded.
- `BackendId` when it helps identify the selected executor/server without exposing secrets.

Do not return secrets, raw authorization responses, unrestricted local paths, or unbounded command/network output. Unexpected source/tool exceptions are converted by Agent to `tool-execution-exception`; caller cancellation is preserved.

## Permission Pipeline

Permissions authorize a described Agent action; they are not a sandbox.

1. Agent confirms the tool was advertised, assigned, and ready for the current run.
2. `IAgentPermissionAwareToolSource` or `IAgentPermissionAwareTool` may build an `AgentPermissionRequest`.
3. Agent resolves the request's `ActionId` and `BoundaryId` against registered `IAgentPermissionSurface` actions.
4. A configured override, session-tree approval, or inherited Unrestricted Mode may change an `Ask` decision.
5. `Deny` records a denied outcome. `Ask` durably suspends the run. `Allow` executes with the approved context.
6. Approval resumes only the correlated session/run revision/tool call. Stale requests do not execute under a newer run.

If a read-only tool returns no permission request, it runs after advertisement checks. If a mutating tool returns no request, Agent uses the generic `agent.tool.mutate:provider-requested-mutation` boundary, whose default is `Ask`. If durable correlation or source/workspace identity is insufficient, that mutating tool is denied instead of approved ephemerally.

### Define A Surface

```csharp
public IReadOnlyList<AgentPermissionActionDescriptor> ListActions() =>
[
    new(
        "acme.issue.create",
        "Create issues",
        "Creates an issue in the selected Acme project.",
        [
            new(
                "selected-project",
                "Selected Acme project",
                "The project selected on the current profile.",
                AgentPermissionDecision.Ask),
        ]),
];
```

Register that object separately through `PackageExtensionPoints.PermissionSurfaces`; merely implementing the interface does not publish it. Action and boundary ids become persisted user preference keys, so changing them resets effective policy.

Build the corresponding request from parsed, canonicalized arguments:

```csharp
return new AgentPermissionRequest(
    "acme.issue.create",
    "selected-project",
    $"Create issue in {projectDisplayName}",
    ToolId: request.ToolId,
    WorkspaceId: context.Workspace?.WorkspaceId,
    BindingId: context.ExecutionBinding?.BindingId,
    ResourceDisplayName: projectDisplayName,
    ResourceReference: canonicalProjectId,
    IsMutation: true);
```

Unknown action/boundary pairs default to `Ask`; an empty action id is denied. Never choose an `Allow` default for an unclassified or outside-scope resource.

## Path And Command Permissions

File sources should ask the selected execution target to resolve a canonical resource and use one of the shared boundary ids:

- `configured-scope`
- `outside-configured-scope`
- `selected-execution-target`
- `unknown`

Classify first with scope widening allowed only for inspection, then execute outside scope only when the Runtime passes `AllowOutsideConfiguredScope = true` after approval. Re-resolve immediately before the operation to resist symlink, mount, or configuration changes.

Shell permission should include a bounded command summary and the selected binding. Structured process invocation is safer than caller-authored shell quoting but still requires execution permission.

## Discovery And Presentation

- Return `null` readiness when a source does not own the requested id; return `Failed` for an owned but unavailable tool.
- Cache external discovery only with explicit invalidation and bounded lifetime. A stale cache is never authority to execute.
- Dynamic sources that alter selectable groups should also implement `IAgentProfileSelectableCapabilityProvider` and `IAgentProfileSelectableCapabilityChangeNotifier`.
- `IAgentToolPresentationResolver` must be pure and tolerate malformed historical arguments/results. Presentation failure must not be needed to understand execution security.

## Security Checklist

- Tool identity remains stable and unique.
- Mutating behavior is never labeled read-only.
- Input and output are bounded.
- URLs reject unsupported schemes and apply SSRF/DNS policies where relevant.
- Paths are canonicalized physically and checked again at mutation time.
- Permission requests describe the actual canonical resource.
- Cancellation is honored, but code does not claim cancellation rolled back a completed side effect.
- Parallel-safe tools have no hidden shared mutation.
- Logs and results contain no secrets.

Next: [Execution targets, paths, and security](execution-targets.md) and [Testing extensions](testing-extensions.md).
