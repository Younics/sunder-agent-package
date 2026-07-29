using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.HistorySearch;

internal static partial class HistorySearchExtractor
{
    private const int TargetChunkLength = 1_500;
    private const int MaximumChunkLength = 1_800;
    private const int ChunkOverlap = 180;
    private const int MaximumActivityValueLength = 1_024;

    private static readonly IReadOnlyDictionary<string, SafeArgumentKind> EmptyFields =
        new Dictionary<string, SafeArgumentKind>(StringComparer.OrdinalIgnoreCase);

    // Argument metadata is trusted only when the durable execution ledger captured the expected
    // first-party package as the unique owner at preparation time.
    private static readonly IReadOnlyDictionary<string, TrustedToolRule> TrustedTools =
        new Dictionary<string, TrustedToolRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["read"] = Rule("sunder.package.agent.tools.files", HistoryActivityKind.Read, ("path", SafeArgumentKind.Path)),
            ["write"] = Rule("sunder.package.agent.tools.files", HistoryActivityKind.Edit, ("path", SafeArgumentKind.Path)),
            ["edit"] = Rule("sunder.package.agent.tools.files", HistoryActivityKind.Edit, ("path", SafeArgumentKind.Path)),
            ["apply_patch"] = new("sunder.package.agent.tools.files", HistoryActivityKind.Edit, IsPatch: true, Fields: EmptyFields),
            ["grep"] = Rule("sunder.package.agent.tools.files", HistoryActivityKind.Search, ("path", SafeArgumentKind.Path)),
            ["glob"] = Rule("sunder.package.agent.tools.files", HistoryActivityKind.Search, ("path", SafeArgumentKind.Path)),
            ["shell"] = Rule("sunder.package.agent.tools.shell", HistoryActivityKind.Execute, ("workingDirectory", SafeArgumentKind.WorkingDirectory)),
            ["bash"] = Rule("sunder.package.agent.tools.shell", HistoryActivityKind.Execute, ("workingDirectory", SafeArgumentKind.WorkingDirectory)),
            ["web_fetch"] = Rule("sunder.package.agent.tools.web", HistoryActivityKind.Use, ("url", SafeArgumentKind.Url)),
            ["web_search"] = Rule("sunder.package.agent.tools.web", HistoryActivityKind.Search),
            ["task"] = Rule("sunder.package.agent.subagents", HistoryActivityKind.Use),
            ["delegate_tasks"] = Rule("sunder.package.agent.subagents", HistoryActivityKind.Use),
            ["skill"] = Rule("sunder.package.agent.skills", HistoryActivityKind.Use),
            ["skill_resource"] = Rule("sunder.package.agent.skills", HistoryActivityKind.Use),
        };

    internal static IReadOnlyList<HistoryProjectionDocument> Extract(
        HistorySourceSession session,
        AgentTurnRecord turn)
    {
        var documents = new List<HistoryProjectionDocument>();
        if (turn.Role is AgentMessageRole.User or AgentMessageRole.Assistant)
        {
            foreach (var item in turn.Items
                         .Where(static item => item.Kind == AgentTurnItemKind.Text)
                         .OrderBy(static item => item.SequenceNumber))
            {
                var sanitized = HistorySecretRedactor.Redact(item.TextContent);
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    continue;
                }

                var chunkIndex = 0;
                foreach (var chunk in Chunk(sanitized))
                {
                    documents.Add(CreateDocument(
                        session,
                        turn,
                        item,
                        chunkIndex++,
                        HistoryAnchorKind.Text,
                        HistoryActivityKind.None,
                        chunk,
                        CreateSnippet(chunk),
                        []));
                }
            }
        }

        foreach (var item in turn.Items
                     .Where(static item => item.Kind == AgentTurnItemKind.ToolCall)
                     .OrderBy(static item => item.SequenceNumber))
        {
            if (TryExtractActivity(session, turn, item) is { } activity)
            {
                documents.Add(activity);
            }
        }

        return documents;
    }

    internal static string CreateDocumentId(Guid itemId, int chunkIndex, HistoryAnchorKind anchorKind)
    {
        var source = $"{itemId:N}\n{chunkIndex}\n{anchorKind}\n{HistorySearchVersions.Extractor}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    internal static bool IsPotentiallyTrustedToolId(string toolId)
        => TrustedTools.ContainsKey(toolId);

    private static HistoryProjectionDocument? TryExtractActivity(
        HistorySourceSession session,
        AgentTurnRecord turn,
        AgentTurnItemRecord item)
    {
        var toolId = Normalize(item.ToolId, 512);
        if (toolId is null)
        {
            return null;
        }

        var isKnown = TrustedTools.TryGetValue(toolId, out var rule)
                      && string.Equals(
                          item.ToolOwnerPackageId,
                          rule.ExpectedPackageId,
                          StringComparison.Ordinal);
        var activity = isKnown ? rule!.Activity : HistoryActivityKind.Use;
        var facets = new List<HistoryProjectionFacet>
        {
            Facet("tool", toolId),
        };
        if (isKnown)
        {
            facets.Add(Facet("activity", activity.ToString()));
        }
        if (item.ToolExecutionStatus is { } executionStatus)
        {
            facets.Add(Facet("status", executionStatus.ToString()));
        }

        var displayParts = isKnown
            ? new List<string> { activity.ToString(), toolId }
            : new List<string> { toolId };
        if (item.ToolExecutionStatus is { } status)
        {
            displayParts.Add($"status: {status}");
        }
        if (isKnown)
        {
            if (rule!.IsPatch)
            {
                foreach (var (operation, path) in ExtractPatchPathHeaders(item.ArgumentsJson))
                {
                    facets.Add(Facet("operation", operation));
                    facets.Add(Facet("path", path));
                    facets.Add(Facet("basename", Path.GetFileName(path)));
                    displayParts.Add($"{operation} {path}");
                }
            }
            else
            {
                ExtractAllowlistedArguments(item.ArgumentsJson, rule, facets, displayParts);
            }
        }

        var body = HistorySecretRedactor.Redact(string.Join(" | ", displayParts));
        if (string.IsNullOrWhiteSpace(body))
        {
            body = $"{activity} {toolId}";
        }

        return CreateDocument(
            session,
            turn,
            item,
            chunkIndex: 0,
            HistoryAnchorKind.Activity,
            activity,
            body,
            CreateSnippet(body),
            facets
                .Where(static facet => !string.IsNullOrWhiteSpace(facet.Value))
                .DistinctBy(static facet => (facet.Kind, facet.NormalizedValue))
                .Take(64)
                .ToArray());
    }

    private static void ExtractAllowlistedArguments(
        string? argumentsJson,
        TrustedToolRule? rule,
        ICollection<HistoryProjectionFacet> facets,
        ICollection<string> displayParts)
    {
        if (rule is null || rule.Fields.Count == 0 || string.IsNullOrWhiteSpace(argumentsJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject().Take(64))
            {
                if (!rule.Fields.TryGetValue(property.Name, out var kind))
                {
                    continue;
                }
                if (kind == SafeArgumentKind.Path)
                {
                    foreach (var path in ReadStrings(property.Value))
                    {
                        AddFacet("path", path, facets, displayParts);
                        var basename = Path.GetFileName(path);
                        if (!string.IsNullOrWhiteSpace(basename))
                        {
                            facets.Add(Facet("basename", basename));
                        }
                    }
                }
                else if (kind == SafeArgumentKind.Symbol)
                {
                    foreach (var symbol in ReadStrings(property.Value))
                    {
                        AddFacet("symbol", symbol, facets, displayParts);
                    }
                }
                else if (kind == SafeArgumentKind.Url)
                {
                    foreach (var url in ReadStrings(property.Value))
                    {
                        if (SanitizeUrl(url) is { } sanitizedUrl)
                        {
                            AddFacet("url", sanitizedUrl, facets, displayParts);
                        }
                    }
                }
                else if (kind == SafeArgumentKind.Operation)
                {
                    foreach (var operation in ReadStrings(property.Value))
                    {
                        AddFacet("operation", operation, facets, displayParts);
                    }
                }
                else if (kind == SafeArgumentKind.WorkingDirectory)
                {
                    foreach (var workingDirectory in ReadStrings(property.Value))
                    {
                        AddFacet("working-directory", workingDirectory, facets, displayParts);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed tool arguments are deliberately not indexed.
        }
    }

    private static IEnumerable<(string Operation, string Path)> ExtractPatchPathHeaders(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            yield break;
        }

        string? patch = null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("patchText"))
                    {
                        patch = property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString()
                            : null;
                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            yield break;
        }

        if (patch is null)
        {
            yield break;
        }

        foreach (Match match in PatchPathHeaderRegex().Matches(patch))
        {
            var path = Normalize(match.Groups["path"].Value, MaximumActivityValueLength);
            if (path is not null)
            {
                yield return (match.Groups["operation"].Value, path);
            }
        }
    }

    private static HistoryProjectionDocument CreateDocument(
        HistorySourceSession session,
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        int chunkIndex,
        HistoryAnchorKind anchorKind,
        HistoryActivityKind activity,
        string bodyText,
        string displaySnippet,
        IReadOnlyList<HistoryProjectionFacet> facets)
        => new(
            CreateDocumentId(item.ItemId, chunkIndex, anchorKind),
            session.WorkspaceId,
            session.WorkspaceName,
            session.SessionId,
            session.Title,
            session.RootSessionId,
            session.ParentSessionId,
            session.ProfileId,
            anchorKind == HistoryAnchorKind.Text ? turn.Role : null,
            activity,
            turn.TurnId,
            item.ItemId,
            item.CallId,
            anchorKind,
            turn.ContentRevision,
            turn.IsStreaming,
            turn.CreatedAtUtc,
            HistorySearchText.BoundAtRuneBoundary(bodyText, MaximumChunkLength),
            HistorySearchText.BoundAtRuneBoundary(displaySnippet, HistorySearchLimits.StoredSnippetCharacters),
            facets);

    private static IEnumerable<string> Chunk(string text)
    {
        var normalized = NormalizeWhitespace(text);
        if (normalized.Length <= MaximumChunkLength)
        {
            yield return normalized;
            yield break;
        }

        var start = 0;
        while (start < normalized.Length)
        {
            var remaining = normalized.Length - start;
            var take = Math.Min(MaximumChunkLength, remaining);
            if (take < remaining)
            {
                var preferredEnd = start + Math.Min(TargetChunkLength, take);
                var lowerBound = start + Math.Max(1, TargetChunkLength - 300);
                var boundary = FindBoundary(normalized, lowerBound, start + take, preferredEnd);
                take = Math.Max(1, boundary - start);
            }

            var end = MoveBeforeSplitSurrogate(normalized, start + take);
            take = Math.Max(1, end - start);
            var chunk = normalized.Substring(start, take).Trim();
            if (chunk.Length > 0)
            {
                yield return chunk;
            }
            if (start + take >= normalized.Length)
            {
                yield break;
            }

            start = MoveAfterSplitSurrogate(
                normalized,
                Math.Max(start + 1, start + take - ChunkOverlap));
        }
    }

    private static int FindBoundary(string text, int lowerBound, int upperBound, int preferredEnd)
    {
        for (var index = Math.Min(upperBound - 1, text.Length - 1); index >= lowerBound; index--)
        {
            if (text[index] == '\n' && index > 0 && text[index - 1] == '\n')
            {
                return index + 1;
            }
        }
        for (var index = Math.Min(upperBound - 1, text.Length - 1); index >= lowerBound; index--)
        {
            if (text[index] is '.' or '!' or '?' or '\n'
                && index + 1 < text.Length
                && char.IsWhiteSpace(text[index + 1]))
            {
                return index + 1;
            }
        }
        for (var index = Math.Min(upperBound - 1, text.Length - 1); index >= lowerBound; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                return index + 1;
            }
        }
        return Math.Min(upperBound, Math.Max(lowerBound, preferredEnd));
    }

    private static TrustedToolRule Rule(
        string expectedPackageId,
        HistoryActivityKind activity,
        params (string Name, SafeArgumentKind Kind)[] fields)
        => new(
            expectedPackageId,
            activity,
            IsPatch: false,
            fields.ToDictionary(static field => field.Name, static field => field.Kind, StringComparer.OrdinalIgnoreCase));

    private static string? SanitizeUrl(string value)
    {
        var sanitized = HistorySecretRedactor.SanitizeHttpUrl(value);
        if (sanitized is null)
        {
            return null;
        }
        return Normalize(sanitized, MaximumActivityValueLength);
    }

    private static IEnumerable<string> ReadStrings(JsonElement element)
    {
        if (TryReadScalar(element, MaximumActivityValueLength) is { } scalar)
        {
            yield return scalar;
            yield break;
        }
        if (element.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var item in element.EnumerateArray().Take(16))
        {
            if (TryReadScalar(item, MaximumActivityValueLength) is { } value)
            {
                yield return value;
            }
        }
    }

    private static string? TryReadScalar(JsonElement element, int maximumLength)
        => element.ValueKind switch
        {
            JsonValueKind.String => Normalize(element.GetString(), maximumLength),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                Normalize(element.GetRawText(), maximumLength),
            _ => null,
        };

    private static void AddFacet(
        string kind,
        string value,
        ICollection<HistoryProjectionFacet> facets,
        ICollection<string> displayParts)
    {
        var sanitized = Normalize(HistorySecretRedactor.Redact(value), MaximumActivityValueLength);
        if (sanitized is null)
        {
            return;
        }
        facets.Add(Facet(kind, sanitized));
        displayParts.Add($"{kind}: {sanitized}");
    }

    private static HistoryProjectionFacet Facet(string kind, string value)
    {
        var bounded = Bound(HistorySecretRedactor.Redact(value), HistorySearchLimits.MaximumFacetCharacters);
        return new HistoryProjectionFacet(kind, bounded, HistorySearchText.NormalizeFacet(bounded));
    }

    private static string CreateSnippet(string text)
        => Bound(NormalizeWhitespace(HistorySecretRedactor.Redact(text)), HistorySearchLimits.StoredSnippetCharacters);

    private static string NormalizeWhitespace(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaximumChunkLength));
        var previousWasSpace = false;
        var newlineCount = 0;
        foreach (var character in value)
        {
            if (character == '\r')
            {
                continue;
            }
            if (character == '\n')
            {
                if (newlineCount < 2)
                {
                    builder.Append('\n');
                }
                newlineCount++;
                previousWasSpace = false;
                continue;
            }
            newlineCount = 0;
            if (char.IsControl(character))
            {
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }
                continue;
            }
            builder.Append(character);
            previousWasSpace = false;
        }
        return HistorySearchText.NormalizeStoredText(builder.ToString()).Trim();
    }

    private static string? Normalize(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Bound(NormalizeWhitespace(value), maximumLength);
    }

    private static string Bound(string value, int maximumLength)
        => HistorySearchText.BoundAtRuneBoundary(value, maximumLength);

    private static int MoveBeforeSplitSurrogate(string value, int index)
        => index > 0
           && index < value.Length
           && char.IsHighSurrogate(value[index - 1])
           && char.IsLowSurrogate(value[index])
            ? index - 1
            : index;

    private static int MoveAfterSplitSurrogate(string value, int index)
        => index > 0
           && index < value.Length
           && char.IsHighSurrogate(value[index - 1])
           && char.IsLowSurrogate(value[index])
            ? index + 1
            : index;

    [GeneratedRegex(@"^\*\*\* (?<operation>Add|Update|Delete) File: (?<path>[^\r\n]+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PatchPathHeaderRegex();

    private enum SafeArgumentKind
    {
        Path,
        Symbol,
        Url,
        Operation,
        WorkingDirectory,
    }

    private sealed record TrustedToolRule(
        string ExpectedPackageId,
        HistoryActivityKind Activity,
        bool IsPatch,
        IReadOnlyDictionary<string, SafeArgumentKind> Fields);
}
