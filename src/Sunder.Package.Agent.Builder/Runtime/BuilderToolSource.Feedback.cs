using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderToolSource
{
    private void RewriteSelections(JsonObject reply, Captured capture, string? viewId = null, string? ownerId = null)
    {
        ownerId ??= Text(reply, "ownerId");
        reply.Remove("ownerId");
        if (reply["views"] is JsonArray views)
            foreach (var view in views.OfType<JsonObject>())
            {
                var id = Text(view, "viewId")!;
                view["ref"] = Retain("v", new(capture, "view", id, OwnerId: ownerId));
                view.Remove("viewId");
            }
        if (reply["outputs"] is JsonArray outputs)
            foreach (var output in outputs.OfType<JsonObject>())
            {
                var path = output["folder"]!.DeepClone();
                output["ref"] = Retain("b", new(capture, "output", Data: path));
                output.Remove("folder");
            }
        if (reply["snapshot"] is JsonObject snapshot)
        {
            var snapshotId = Text(snapshot, "snapshotId")!;
            var items = new JsonArray();
            foreach (var element in snapshot["elements"]!.AsArray().OfType<JsonObject>())
            {
                var item = new JsonObject { ["ref"] = Retain("e", new(capture, "element", viewId, snapshotId, Text(element, "reference"), element.DeepClone(), ownerId)),
                    ["role"] = Text(element, "role")!.ToLowerInvariant(), ["name"] = Text(element, "name") };
                if (element["isEnabled"]?.GetValue<bool>() == false) item["enabled"] = false;
                if (element["actions"] is JsonArray { Count: > 0 } actions) item["actions"] = actions.DeepClone();
                if (element["state"] is JsonObject { Count: > 0 } state) item["state"] = state.DeepClone();
                items.Add(item);
            }
            reply["elements"] = items;
            reply.Remove("snapshot");
            if (reply["partial"]?.GetValue<bool>() == false) reply.Remove("partial");
        }
    }

    private void AddViewSelection(AgentToolExecutionContext context, ToolPlan plan, JsonObject payload)
    {
        var selection = plan.Selected!;
        if (selection.Project.AppId != plan.Capture.AppId) throw new ArgumentException("The original view owner is unavailable; inspect the current package.");
        payload["viewId"] = selection.ViewId;
        payload["ownerId"] = selection.OwnerId;
    }

    private JsonObject ViewQuery(AgentToolExecutionContext context, ToolPlan plan)
    {
        var query = new JsonObject { ["role"] = Text(plan.Args, "role"), ["name"] = Text(plan.Args, "name"), ["text"] = Text(plan.Args, "text"),
            ["limit"] = plan.Args["limit"]?.DeepClone() ?? JsonValue.Create(20), ["includeValue"] = plan.Args["value"]?.DeepClone() ?? JsonValue.Create(false),
            ["cursor"] = Text(plan.Args, "cursor") };
        var selection = plan.Selected!;
        if (Text(plan.Args, "within") is { } within)
        {
            var parent = Reference(within, context, "element");
            if (parent.Project.AppId != plan.Capture.AppId || parent.ViewId != selection.ViewId || parent.Project.EntryPoint != selection.Project.EntryPoint || parent.OwnerId != selection.OwnerId)
                throw new ArgumentException("within must belong to this exact view.");
            query["snapshotId"] = parent.SnapshotId; query["within"] = parent.ElementId;
        }
        if (selection.Kind == "element")
        { query["snapshotId"] = selection.SnapshotId; query["elementReference"] = selection.ElementId; }
        return query;
    }

    private async Task<AgentToolResult> QueryLogsToolsetAsync(AgentToolExecutionContext context, string tool, ToolPlan plan, JsonObject payload, CancellationToken token)
    {
        var args = plan.Args;
        static string? Time(JsonObject args, string name)
        {
            var text = Text(args, name);
            if (text is null) return null;
            if (!(text.EndsWith('Z') || text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-')
                || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ArgumentException(name + " needs an ISO timestamp with an explicit offset.");
            return text;
        }
        var query = new JsonObject { ["since"] = Time(args, "since"), ["until"] = Time(args, "until"),
            ["minimumLevel"] = Text(args, "level") switch { "trace" => 0, "debug" => 1, "information" => 2, "error" => 4, "critical" => 5, _ => 3 },
            ["source"] = Text(args, "source") ?? "all", ["contains"] = Text(args, "contains"),
            ["maximumEntries"] = args["limit"]?.GetValue<int>() ?? 10, ["maximumBytes"] = 4096 };
        if (Text(args, "after") is { } after)
        {
            if (args["since"] is not null) throw new ArgumentException("Use after or since, not both.");
            var operation = Reference(after, context, "operation");
            if (operation.Project.EntryPoint != plan.Capture.EntryPoint || operation.Project.AppId != plan.Capture.AppId) throw new ArgumentException("Operation belongs to another project/activation.");
            query["since"] = operation.Data!["started"]!.DeepClone();
        }
        if (Text(args, "cursor") is { } cursor)
        {
            var page = Reference(cursor, context, "log-page");
            if (page.Project.EntryPoint != plan.Capture.EntryPoint || page.Project.AppId != plan.Capture.AppId) throw new ArgumentException("Cursor belongs to another project/activation.");
            var allowed = new[] { "package", "cursor", "limit" };
            if (args.Any(pair => !allowed.Contains(pair.Key))) throw new ArgumentException("Continue logs using package, cursor and optional limit only; filters are retained.");
            query = page.Data!.DeepClone().AsObject();
            if (args["limit"] is { } limit) query["maximumEntries"] = limit.DeepClone();
        }
        else query["since"] ??= DateTimeOffset.UtcNow.AddMinutes(-15).ToString("O");
        payload["query"] = query;
        var response = await runtime.InvokeDevelopmentAsync("logs", "", payload.ToJsonString(CompactJson), plan.Capture.AppId, token).ConfigureAwait(false);
        var raw = JsonNode.Parse(response.PayloadJson)!.AsObject();
        var entries = new JsonArray();
        JsonObject? previous = null;
        JsonObject? previousLog = null;
        foreach (var log in raw["entries"]!.AsArray().OfType<JsonObject>())
        {
            var message = Text(log, "message")!;
            if (previous is not null && previousLog is not null && Text(previousLog, "message") == message
                && Text(previousLog, "source") == Text(log, "source") && Text(previousLog, "level") == Text(log, "level")
                && Text(previousLog, "category") == Text(log, "category") && JsonNode.DeepEquals(previousLog["properties"], log["properties"])
                && JsonNode.DeepEquals(previousLog["wasTruncated"], log["wasTruncated"]))
            { previous["count"] = (previous["count"]?.GetValue<int>() ?? 1) + 1; previous["until"] = log["timestamp"]!.DeepClone(); continue; }
            var item = new JsonObject { ["ref"] = Retain("l", new(plan.Capture, "log", Data: log.DeepClone())), ["time"] = log["timestamp"]!.DeepClone(),
                ["source"] = Text(log, "source"), ["level"] = Text(log, "level")!.ToLowerInvariant(), ["message"] = message };
            entries.Add(item); previous = item; previousLog = log;
        }
        var result = new JsonObject { ["entries"] = entries };
        if (raw["nextCursor"] is { } next)
        {
            var continuation = query.DeepClone().AsObject(); continuation["cursor"] = next.DeepClone();
            result["next"] = Retain("c", new(plan.Capture, "log-page", Data: continuation));
        }
        if (raw["historyGap"]?.GetValue<bool>() == true) result["historyGap"] = true;
        if (raw["wasTruncated"]?.GetValue<bool>() == true) result["truncated"] = true;
        return CompactResult(tool, result, "Requested package diagnostics");
    }
}
