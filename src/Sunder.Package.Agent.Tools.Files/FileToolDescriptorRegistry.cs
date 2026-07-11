using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FileToolDescriptorRegistry
{
    public const string SourceId = "workspace-files";
    public const string DisplayName = "Workspace Files";

    public static IReadOnlyList<AgentToolDescriptor> Descriptors { get; } =
    [
        new("read", "Read File", "Read a file or directory from the selected workspace.", IsReadOnly: true, ArgumentsJsonSchema: ReadSchema, SourceKind: "workspace", SourceId: "files", SourceDisplayName: DisplayName, RuntimeInstructions: ReadInstructions, Priority: AgentToolPriority.Medium) { ConcurrencyMode = AgentToolConcurrencyMode.ParallelSafe },
        new("write", "Write File", "Create or overwrite a file in the selected workspace.", IsReadOnly: false, ArgumentsJsonSchema: WriteSchema, SourceKind: "workspace", SourceId: "files", SourceDisplayName: DisplayName, RuntimeInstructions: WriteInstructions, Priority: AgentToolPriority.Medium),
        new("edit", "Edit File", "Modify an existing file in the selected workspace using exact string replacement.", IsReadOnly: false, ArgumentsJsonSchema: EditSchema, SourceKind: "workspace", SourceId: "files", SourceDisplayName: DisplayName, RuntimeInstructions: EditInstructions, Priority: AgentToolPriority.Medium),
        new("apply_patch", "Apply Patch", "Apply a structured patch to files in the selected workspace.", IsReadOnly: false, ArgumentsJsonSchema: ApplyPatchSchema, SourceKind: "workspace", SourceId: "files", SourceDisplayName: DisplayName, RuntimeInstructions: ApplyPatchInstructions, Priority: AgentToolPriority.Medium),
        new("grep", "Grep", "Search file contents in the selected workspace using regular expressions.", IsReadOnly: true, ArgumentsJsonSchema: GrepSchema, SourceKind: "workspace", SourceId: "files", SourceDisplayName: DisplayName, RuntimeInstructions: GrepInstructions, Priority: AgentToolPriority.Medium) { ConcurrencyMode = AgentToolConcurrencyMode.ParallelSafe },
        new("glob", "Glob", "Find files in the selected workspace by glob pattern.", IsReadOnly: true, ArgumentsJsonSchema: GlobSchema, SourceKind: "workspace", SourceId: "files", SourceDisplayName: DisplayName, RuntimeInstructions: GlobInstructions, Priority: AgentToolPriority.Medium) { ConcurrencyMode = AgentToolConcurrencyMode.ParallelSafe },
    ];

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
        """;

    private const string GlobInstructions = """
        Use this tool to find files by name or path pattern before using lower-priority executor commands.

        Usage:
        - Supports glob patterns like **/*.cs or src/**/*.ts.
        - Returns at most 1000 matching paths.
        - Use this when you need to locate files by name pattern.
        - Prefer this over shell commands for file discovery.
        """;

    private const string GrepInstructions = """
        Use this tool to search file contents before using lower-priority executor commands.

        Usage:
        - Searches file contents using regular expressions.
        - Supports full regex syntax.
        - Use include to filter by file pattern when supported.
        - Returns at most 1000 matches with file paths and line numbers.
        - Use this when you need to find references, symbols, text, or code patterns.
        - Prefer this over shell commands for content search.
        """;

    private const string WriteInstructions = """
        Use this tool to create a new file or overwrite a file when the complete intended content is known.

        Usage:
        - This tool overwrites the existing file if one exists at the provided path.
        - Prefer edit or apply_patch for modifying existing code.
        - Read an existing file first before replacing it so you do not accidentally discard content.
        - Do not create documentation files unless explicitly requested.
        - Avoid emojis unless the user explicitly asks for them.
        """;

    private const string EditInstructions = """
        Use this tool for precise edits to existing files by replacing exact text matches.

        Usage:
        - Read the target file before editing.
        - Preserve exact indentation and whitespace from the current file content.
        - The edit fails if oldString is not found.
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
        - Add File content lines must be prefixed with +.
        - Use Update File for in-place changes.
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
        {"type":"object","properties":{"pattern":{"type":"string","minLength":1},"path":{"type":"string"},"include":{"type":"string"}},"required":["pattern"],"additionalProperties":false}
        """;

    private const string GlobSchema = """
        {"type":"object","properties":{"pattern":{"type":"string","minLength":1},"path":{"type":"string"}},"required":["pattern"],"additionalProperties":false}
        """;
}
