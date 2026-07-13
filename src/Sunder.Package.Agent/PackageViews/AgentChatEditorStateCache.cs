namespace Sunder.Package.Agent.PackageViews;

internal readonly record struct AgentChatEditorState(
    int CaretIndex,
    int SelectionStart,
    int SelectionEnd,
    bool HadFocus);
