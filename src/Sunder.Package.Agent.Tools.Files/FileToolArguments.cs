using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileToolArguments
{
    public const int DefaultReadLimit = 2000;

    public static bool TryParseRead(string json, out FileReadArgs args, out string? error)
    {
        args = new FileReadArgs(string.Empty);
        if (!TryParse(json, out var values, out error)
            || !values!.TryReadRequiredString("path", out var path, out error)
            || !values.TryReadOptionalInt32("offset", out var offset, out error)
            || !values.TryReadOptionalInt32("limit", out var limit, out error)
            || !ValidatePath(path, out error)
            || !ValidateRange(offset, limit, out error))
        {
            error = Prefix("read", error);
            return false;
        }

        args = new FileReadArgs(path!, offset, limit);
        return true;
    }

    public static bool TryParseWrite(string json, out FileWriteArgs args, out string? error)
    {
        args = new FileWriteArgs(string.Empty, string.Empty);
        if (!TryParse(json, out var values, out error)
            || !values!.TryReadRequiredString("path", out var path, out error)
            || !values.TryReadRequiredString("content", out var content, out error)
            || !ValidatePath(path, out error))
        {
            error = Prefix("write", error);
            return false;
        }

        args = new FileWriteArgs(path!, content!);
        return true;
    }

    public static bool TryParseEdit(string json, out FileEditArgs args, out string? error)
    {
        args = new FileEditArgs(string.Empty, string.Empty, string.Empty);
        if (!TryParse(json, out var values, out error)
            || !values!.TryReadRequiredString("path", out var path, out error)
            || !values.TryReadRequiredString("oldString", out var oldString, out error)
            || !values.TryReadRequiredString("newString", out var newString, out error)
            || !values.TryReadOptionalBoolean("replaceAll", out var replaceAll, out error)
            || !ValidatePath(path, out error))
        {
            error = Prefix("edit", error);
            return false;
        }

        if (oldString!.Length == 0)
        {
            error = Prefix("edit", "The `oldString` parameter must not be empty.");
            return false;
        }

        args = new FileEditArgs(path!, oldString, newString!, replaceAll ?? false);
        return true;
    }

    public static bool TryParsePatch(string json, out FilePatchArgs args, out string? error)
    {
        args = new FilePatchArgs(string.Empty);
        if (!TryParse(json, out var values, out error)
            || !values!.TryReadRequiredString("patchText", out var patchText, out error))
        {
            error = Prefix("apply_patch", error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(patchText))
        {
            error = Prefix("apply_patch", "The `patchText` parameter must not be empty.");
            return false;
        }

        args = new FilePatchArgs(patchText);
        return true;
    }

    public static bool TryParseGrep(string json, out FileGrepArgs args, out string? error)
    {
        args = new FileGrepArgs(string.Empty);
        if (!TryParse(json, out var values, out error)
            || !values!.TryReadRequiredString("pattern", out var pattern, out error)
            || !values.TryReadOptionalString("path", out var path, out error)
            || !values.TryReadOptionalString("include", out var include, out error))
        {
            error = Prefix("grep", error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = Prefix("grep", "The `pattern` parameter must not be empty.");
            return false;
        }

        args = new FileGrepArgs(pattern, path, include);
        return true;
    }

    public static bool TryParseGlob(string json, out FileGlobArgs args, out string? error)
    {
        args = new FileGlobArgs(string.Empty);
        if (!TryParse(json, out var values, out error)
            || !values!.TryReadRequiredString("pattern", out var pattern, out error)
            || !values.TryReadOptionalString("path", out var path, out error))
        {
            error = Prefix("glob", error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = Prefix("glob", "The `pattern` parameter must not be empty.");
            return false;
        }

        args = new FileGlobArgs(pattern, path);
        return true;
    }

    public static string? ResolvePermissionPath(string toolId, string json)
    {
        var path = TryParse(json, out var values, out _)
                   && values!.TryReadOptionalString("path", out var parsedPath, out _)
            ? parsedPath
            : null;
        return toolId.ToLowerInvariant() switch
        {
            "grep" or "glob" when string.IsNullOrWhiteSpace(path) => ".",
            _ => path,
        };
    }

    private static bool TryParse(string json, out AgentToolArgumentObject? values, out string? error)
        => AgentToolArgumentObject.TryParse(json, out values, out error);

    private static bool ValidatePath(string? path, out string? error)
    {
        error = string.IsNullOrWhiteSpace(path) ? "The `path` parameter must not be empty." : null;
        return error is null;
    }

    private static bool ValidateRange(int? offset, int? limit, out string? error)
    {
        if (offset is <= 0)
        {
            error = "The `offset` parameter must be at least 1.";
            return false;
        }

        if (limit is <= 0 or > DefaultReadLimit)
        {
            error = $"The `limit` parameter must be between 1 and {DefaultReadLimit}.";
            return false;
        }

        error = null;
        return true;
    }

    private static string Prefix(string toolName, string? error)
        => $"Invalid {toolName} arguments: {error ?? "arguments were empty or invalid."}";
}

internal sealed record FileReadArgs(string Path, int? Offset = null, int? Limit = null)
{
    public int EffectiveOffset => Offset ?? 1;

    public int EffectiveLimit => Limit ?? FileToolArguments.DefaultReadLimit;
}

internal sealed record FileWriteArgs(string Path, string Content);

internal sealed record FileEditArgs(string Path, string OldString, string NewString, bool ReplaceAll = false);

internal sealed record FilePatchArgs(string PatchText);

internal sealed record FileGrepArgs(string Pattern, string? Path = null, string? Include = null);

internal sealed record FileGlobArgs(string Pattern, string? Path = null);
