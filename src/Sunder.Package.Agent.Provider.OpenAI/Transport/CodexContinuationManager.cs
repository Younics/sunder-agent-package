using Microsoft.Extensions.AI;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

internal sealed class CodexContinuationManager(
    CodexResponseContinuationStore store,
    string? conversationId)
{
    private readonly CodexResponseContinuationStore _store = store;
    private readonly string? _conversationId = conversationId;

    public CodexResponseContinuationState? State => _store.Get(_conversationId);

    public void Reject() => _store.Clear(_conversationId);

    public void RecordCompleted(
        CodexResponsesRequest request,
        string? backendResponseId,
        string? text,
        IReadOnlyList<FunctionCallContent> functionCalls)
    {
        if (string.IsNullOrWhiteSpace(_conversationId) || string.IsNullOrWhiteSpace(backendResponseId))
        {
            return;
        }

        var outputFingerprints = CodexResponsesRequestBuilder.BuildAssistantOutputFingerprints(text, functionCalls);
        _store.Save(new CodexResponseContinuationState(
            _conversationId,
            request.ShapeFingerprint,
            backendResponseId,
            request.ConversationItemFingerprints.Concat(outputFingerprints).ToArray()));
    }
}
