using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileToolDescriptorRegistry
{
    public const string SourceId = "workspace-files";
    public const string DisplayName = "Workspace Files";

    public static IReadOnlyList<AgentToolDescriptor> Descriptors { get; } =
    [
        Create("read", "Read File", "Read a file or directory from the selected workspace.", true, ReadSchema, ReadInstructions),
        Create("write", "Write File", "Create or overwrite a file in the selected workspace.", false, WriteSchema, WriteInstructions),
        Create("edit", "Edit File", "Modify an existing file in the selected workspace using exact string replacement.", false, EditSchema, EditInstructions),
        Create("apply_patch", "Apply Patch", "Apply a structured patch to files in the selected workspace.", false, ApplyPatchSchema, ApplyPatchInstructions),
        Create("grep", "Grep", "Search file contents in the selected workspace using regular expressions.", true, GrepSchema, GrepInstructions),
        Create("glob", "Glob", "Find files in the selected workspace by glob pattern.", true, GlobSchema, GlobInstructions),
    ];

    private static AgentToolDescriptor Create(string id, string name, string description, bool readOnly, string schema, string instructions) => new()
    {
        ToolId = id, DisplayName = name, Description = description, IsReadOnly = readOnly, RequiresNetwork = false,
        ArgumentsJsonSchema = schema, SourceKind = "workspace", SourceId = "files", SourceDisplayName = DisplayName,
        RuntimeInstructions = instructions, Priority = AgentToolPriority.Medium,
        ConcurrencyMode = readOnly ? AgentToolConcurrencyMode.ParallelSafe : AgentToolConcurrencyMode.Sequential,
        Aliases = null, SelectionScope = AgentToolSelectionScope.Tool, SelectionGroupId = null, SelectionGroupDisplayName = null,
        SelectionGroupDescription = null, SelectionGroupAllowsBulkSelection = true, ActivationRequirement = null,
    };

    public static bool Contains(string toolId)
        => Descriptors.Any(descriptor => string.Equals(descriptor.ToolId, toolId, StringComparison.OrdinalIgnoreCase));

    private const string ReadInstructions = """
        Use this tool to read file contents or list directory entries in the selected workspace. It supports reading specific line ranges for large files.

        Usage:
        - Use the path parameter for the workspace-relative or executor-resolved file or directory path.
        - By default, this tool returns up to 2000 lines from the start of a file.
        - The offset parameter is the line number to start from, using 1-based indexing.
        - To read later sections, call this tool again with a larger offset.
        - Use grep to find specific content in large files or files with long lines.
        - If you are unsure of the correct path, use glob to look up filenames by glob pattern.
        - File contents are returned with each line prefixed by its line number.
        - Directory entries are returned one per line, with a trailing slash for subdirectories.
        - Avoid tiny repeated slices. If you need more context, read a larger window.
        - Pages are bounded to about 50 KiB and individual long lines are shortened explicitly. Use grep for omitted long-line content.
        """;

    private const string GlobInstructions = """
        Use this tool to find files by name or path pattern before using lower-priority executor commands.

        Usage:
        - Supports glob patterns like **/*.cs or src/**/*.ts.
        - Patterns containing a slash are relative to the search path; unrelated directory trees are not traversed.
        - End a pattern with / to find directories: */ lists immediate child directories, **/ finds nested directories.
        - Symbolic links are not followed. Use read on a directory to inspect its entries.
        - Hidden files/directories are excluded from wildcard matches unless hidden is true; explicit dot segments still match.
        - Returns up to 100 matching paths by default; set limit between 1 and 1000.
        - Incomplete results include the limiting bound and narrowing guidance. A search deadline is 30 seconds.
        - Use this when you need to locate files by name pattern.
        - Prefer this over shell commands for file discovery.
        """;

    private const string GrepInstructions = """
        Use this tool to search file contents before using lower-priority executor commands.

        Usage:
        - Searches file contents using regular expressions.
        - Uses bounded .NET non-backtracking regular expressions; backreferences and lookarounds are not supported.
        - Set literal to true to search exact text without regex escaping.
        - Matching is case-sensitive by default; set caseSensitive to false for invariant case-insensitive matching.
        - Use include to filter by file pattern when supported.
        - Returns up to 100 matching lines by default; set limit between 1 and 1000.
        - Incomplete results include the limiting bound and narrowing guidance. A search deadline is 30 seconds.
        - Use this when you need to find references, symbols, text, or code patterns.
        - Prefer this over shell commands for content search.
        """;

    private const string WriteInstructions = """
        Use this tool to create a new file or overwrite a file when the complete intended content is known.

        Usage:
        - This tool overwrites the existing file if one exists at the provided path.
        - Prefer edit or apply_patch for modifying existing code.
        - Read an existing file first before replacing it so you do not accidentally discard content.
        - Existing files require a complete text snapshot and a content check at publication. A concurrent change fails without overwriting it.
        - Do not create documentation files unless explicitly requested.
        - Avoid emojis unless the user explicitly asks for them.
        """;

    private const string EditInstructions = """
        Use this tool for precise edits to existing files by replacing exact text matches.

        Usage:
        - Read the target file before editing.
        - Preserve exact indentation and whitespace from the current file content.
        - The edit fails if oldString is not found or matches more than once, unless replaceAll is true.
        - Do not copy line-number prefixes from read into oldString/newString. Use LF line breaks; the file's original line endings are preserved.
        - Include enough surrounding context in oldString to identify the intended location.
        - Use replaceAll for file-wide renames or repeated exact replacements.
        - Prefer this over write for focused changes to existing files.
        """;

    private const string ApplyPatchInstructions = """
        Use this tool for structured multi-file edits. Every operation is preflighted before any file is changed.

        Patch format:
        *** Begin Patch
        [one or more file sections]
        *** End Patch

        Each operation starts with one of:
        *** Add File: <path>
        *** Delete File: <path>
        *** Update File: <path>

        Rules:
        - You must include a header with the intended action.
        - A patch may contain at most 64 file operations targeting at most 64 distinct paths, counting both paths of a move.
        - Add File content lines must be prefixed with +.
        - Use Update File for in-place changes. Immediately follow it with *** Move to: <new path> to move the file, with optional edit hunks.
        - Move destinations must not exist. Moves create the destination and then remove the source; they are not atomic renames and do not guarantee filesystem identity or metadata preservation.
        - Failed moves use guarded best-effort rollback. If source restoration or destination ownership cannot be confirmed, retain recovery files and inspect the reported state instead of blindly retrying.
        - Use Delete File only when removing an existing file.
        - Use each path at most once in a patch.
        - Prefer apply_patch for coordinated edits across multiple files.
        """;

    private const string ReadSchema = """
        {"type":"object","properties":{"path":{"type":"string","minLength":1},"offset":{"type":"integer","minimum":1},"limit":{"type":"integer","minimum":1,"maximum":2000}},"required":["path"],"additionalProperties":false}
        """;

    private const string WriteSchema = """
        {"type":"object","properties":{"path":{"type":"string","minLength":1},"content":{"type":"string"}},"required":["path","content"],"additionalProperties":false}
        """;

    private const string EditSchema = """
        {"type":"object","properties":{"path":{"type":"string","minLength":1},"oldString":{"type":"string","minLength":1},"newString":{"type":"string"},"replaceAll":{"type":"boolean"}},"required":["path","oldString","newString"],"additionalProperties":false}
        """;

    private const string ApplyPatchSchema = """
        {"type":"object","properties":{"patchText":{"type":"string","minLength":1}},"required":["patchText"],"additionalProperties":false}
        """;

    private const string GrepSchema = """
        {"type":"object","properties":{"pattern":{"type":"string","minLength":1},"path":{"type":"string"},"include":{"type":"string"},"literal":{"type":"boolean"},"caseSensitive":{"type":"boolean"},"limit":{"type":"integer","minimum":1,"maximum":1000}},"required":["pattern"],"additionalProperties":false}
        """;

    private const string GlobSchema = """
        {"type":"object","properties":{"pattern":{"type":"string","minLength":1},"path":{"type":"string"},"hidden":{"type":"boolean"},"limit":{"type":"integer","minimum":1,"maximum":1000}},"required":["pattern"],"additionalProperties":false}
        """;
}
