using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileToolPresentationAdapter
{
    public static AgentToolPresentation? Resolve(AgentToolPresentationRequest request)
        => request.ToolId.ToLowerInvariant() switch
        {
            "read" => ResolveRead(request),
            "write" => ResolveWrite(request),
            "edit" => ResolveEdit(request),
            "glob" => ResolveGlob(request),
            "grep" => ResolveGrep(request),
            "apply_patch" => ResolvePatch(request),
            _ => null,
        };

    private static AgentToolPresentation ResolveRead(AgentToolPresentationRequest request)
    {
        var parsed = FileToolArguments.TryParseRead(request.ArgumentsJson, out var args, out var error);
        return Create(
            request,
            parsed ? args.Path : null,
            parsed
                ? AgentToolPresentationMarkdown.BuildRequestMarkdown(
                    ("Path", args.Path),
                    ("Offset", args.Offset?.ToString()),
                    ("Limit", args.Limit?.ToString()))
                : AgentToolPresentationMarkdown.BuildRawRequestMarkdown(request.ArgumentsJson, error));
    }

    private static AgentToolPresentation ResolveWrite(AgentToolPresentationRequest request)
    {
        var parsed = FileToolArguments.TryParseWrite(request.ArgumentsJson, out var args, out var error);
        return Create(
            request,
            parsed ? args.Path : null,
            parsed
                ? AgentToolPresentationMarkdown.BuildRequestMarkdown(
                    ("Path", args.Path),
                    ("Content", FormatTextStats(args.Content)))
                : AgentToolPresentationMarkdown.BuildRawRequestMarkdown(request.ArgumentsJson, error));
    }

    private static AgentToolPresentation ResolveEdit(AgentToolPresentationRequest request)
    {
        var parsed = FileToolArguments.TryParseEdit(request.ArgumentsJson, out var args, out var error);
        return Create(
            request,
            parsed ? args.Path : null,
            parsed
                ? AgentToolPresentationMarkdown.BuildRequestMarkdown(
                    ("Path", args.Path),
                    ("Old text", FormatTextStats(args.OldString)),
                    ("New text", FormatTextStats(args.NewString)),
                    ("Replace all", args.ReplaceAll ? "yes" : "no"))
                : AgentToolPresentationMarkdown.BuildRawRequestMarkdown(request.ArgumentsJson, error));
    }

    private static AgentToolPresentation ResolveGlob(AgentToolPresentationRequest request)
    {
        var parsed = FileToolArguments.TryParseGlob(request.ArgumentsJson, out var args, out var error);
        var header = parsed
            ? string.IsNullOrWhiteSpace(args.Path) ? args.Pattern : $"{args.Pattern} in {args.Path}"
            : null;
        return Create(
            request,
            header,
            parsed
                ? AgentToolPresentationMarkdown.BuildRequestMarkdown(
                    ("Pattern", args.Pattern),
                    ("Path", string.IsNullOrWhiteSpace(args.Path) ? "." : args.Path))
                : AgentToolPresentationMarkdown.BuildRawRequestMarkdown(request.ArgumentsJson, error));
    }

    private static AgentToolPresentation ResolveGrep(AgentToolPresentationRequest request)
    {
        var parsed = FileToolArguments.TryParseGrep(request.ArgumentsJson, out var args, out var error);
        var qualifiers = parsed
            ? new[] { args.Include, args.Path }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
            : [];
        var header = !parsed || qualifiers.Length == 0
            ? parsed ? args.Pattern : null
            : $"{args.Pattern} in {string.Join(" / ", qualifiers)}";
        return Create(
            request,
            header,
            parsed
                ? AgentToolPresentationMarkdown.BuildRequestMarkdown(
                    ("Pattern", args.Pattern),
                    ("Path", string.IsNullOrWhiteSpace(args.Path) ? "." : args.Path),
                    ("Include", args.Include))
                : AgentToolPresentationMarkdown.BuildRawRequestMarkdown(request.ArgumentsJson, error));
    }

    private static AgentToolPresentation ResolvePatch(AgentToolPresentationRequest request)
    {
        var parsed = FileToolArguments.TryParsePatch(request.ArgumentsJson, out var args, out var error);
        string? header = request.ResultSummary;
        if (string.IsNullOrWhiteSpace(header) && parsed)
        {
            try
            {
                header = FilePatchParser.BuildSummary(FilePatchParser.Parse(args.PatchText));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                header = "Patch workspace files";
            }
        }

        var detail = parsed
            ? AgentToolPresentationMarkdown.BuildFencedMarkdown("Patch", "diff", args.PatchText)
            : AgentToolPresentationMarkdown.BuildRawRequestMarkdown(request.ArgumentsJson, error);
        return new AgentToolPresentation(header, detail, request.TextContent);
    }

    private static AgentToolPresentation Create(AgentToolPresentationRequest request, string? fallbackHeader, string detail)
        => new(request.ResultSummary ?? fallbackHeader, detail, request.TextContent);

    private static string FormatTextStats(string text)
    {
        var lineCount = string.IsNullOrEmpty(text) ? 0 : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;
        return $"{FileToolResult.FormatCount(text.Length, "char")}, {FileToolResult.FormatCount(lineCount, "line")}";
    }
}
