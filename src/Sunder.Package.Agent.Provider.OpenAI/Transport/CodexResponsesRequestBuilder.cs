using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

internal static class CodexResponsesRequestBuilder
{
    public static CodexResponsesRequest Build(
        AgentChatClientContext context,
        IReadOnlyList<AIChatMessage> messages,
        ChatOptions? options,
        bool toolAware,
        CodexResponseContinuationState? continuationState = null,
        bool disableContinuation = false)
        => CodexResponsesRequestSerializer.Serialize(
            CodexResponsesRequestNormalizer.Normalize(context, messages, options, toolAware),
            continuationState,
            disableContinuation);

    public static IReadOnlyList<string> BuildAssistantOutputFingerprints(
        string? text,
        IReadOnlyList<FunctionCallContent> functionCalls)
        => CodexResponsesRequestSerializer.BuildItemFingerprints(
            CodexResponsesInputNormalizer.BuildAssistantOutput(text, functionCalls));
}

internal sealed record CodexResponsesRequest(
    string Model,
    int InputItemCount,
    int ConversationInputItemCount,
    int ToolCount,
    string Body,
    bool UsesDeveloperInstructionInput,
    bool HasTopLevelInstructions,
    string? ServiceTier,
    int? MaxOutputTokens,
    bool HasPromptCacheKey,
    bool HasIncludeOptions,
    bool HasReasoningOptions,
    bool HasTextOptions,
    string? ToolChoice,
    bool? ParallelToolCalls,
    bool HasPreviousResponseId,
    string ShapeFingerprint,
    IReadOnlyList<string> ConversationItemFingerprints);
