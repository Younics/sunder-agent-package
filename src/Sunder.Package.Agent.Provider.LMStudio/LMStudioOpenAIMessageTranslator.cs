using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Sunder.Package.Agent.Provider.Shared;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;
using OpenAIChatMessage = OpenAI.Chat.ChatMessage;

namespace Sunder.Package.Agent.Provider.LMStudio;

internal static class LMStudioOpenAIMessageTranslator
{
    public static IReadOnlyList<OpenAIChatMessage> Translate(
        IEnumerable<AIChatMessage> messages,
        string? instructions)
    {
        var translated = new List<OpenAIChatMessage>();
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            translated.Add(OpenAIChatMessage.CreateSystemMessage(instructions));
        }

        foreach (var message in messages)
        {
            AddMessages(translated, message);
        }

        return translated;
    }

    private static void AddMessages(ICollection<OpenAIChatMessage> messages, AIChatMessage message)
    {
        if (TryAddFunctionCallMessage(messages, message) || TryAddFunctionResultMessages(messages, message))
        {
            return;
        }

        var textBuilder = new StringBuilder();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                    AppendText(textBuilder, textContent.Text);
                    break;

                case FunctionCallContent functionCall:
                    FlushTextMessage(messages, message.Role, textBuilder);
                    messages.Add(CreateFunctionCallMessage(functionCall));
                    break;

                case FunctionResultContent functionResult:
                    FlushTextMessage(messages, message.Role, textBuilder);
                    messages.Add(OpenAIChatMessage.CreateToolMessage(
                        functionResult.CallId,
                        ProviderToolResult.RenderText(functionResult.Result)));
                    break;
            }
        }

        if (textBuilder.Length == 0 && !string.IsNullOrWhiteSpace(message.Text))
        {
            textBuilder.Append(message.Text);
        }

        FlushTextMessage(messages, message.Role, textBuilder);
    }

    private static bool TryAddFunctionCallMessage(ICollection<OpenAIChatMessage> messages, AIChatMessage message)
    {
        var calls = message.Contents.OfType<FunctionCallContent>().ToArray();
        if (calls.Length == 0 || calls.Length != message.Contents.Count)
        {
            return false;
        }

        messages.Add(OpenAIChatMessage.CreateAssistantMessage(calls.Select(CreateToolCall).ToArray()));
        return true;
    }

    private static bool TryAddFunctionResultMessages(ICollection<OpenAIChatMessage> messages, AIChatMessage message)
    {
        var results = message.Contents.OfType<FunctionResultContent>().ToArray();
        if (results.Length == 0 || results.Length != message.Contents.Count)
        {
            return false;
        }

        foreach (var result in results)
        {
            messages.Add(OpenAIChatMessage.CreateToolMessage(
                result.CallId,
                ProviderToolResult.RenderText(result.Result)));
        }

        return true;
    }

    private static OpenAIChatMessage CreateFunctionCallMessage(FunctionCallContent functionCall)
        => OpenAIChatMessage.CreateAssistantMessage([CreateToolCall(functionCall)]);

    private static ChatToolCall CreateToolCall(FunctionCallContent functionCall)
        => ChatToolCall.CreateFunctionToolCall(
            string.IsNullOrWhiteSpace(functionCall.CallId) ? Guid.NewGuid().ToString("N") : functionCall.CallId,
            functionCall.Name,
            BinaryData.FromString(SerializeArguments(
                functionCall.Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal))));

    private static string SerializeArguments(IDictionary<string, object?> arguments)
        => arguments.Count == 0 ? "{}" : JsonSerializer.Serialize(arguments);

    private static void AppendText(StringBuilder builder, string text)
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.Append(text);
    }

    private static void FlushTextMessage(
        ICollection<OpenAIChatMessage> messages,
        AIChatRole role,
        StringBuilder textBuilder)
    {
        if (textBuilder.Length == 0)
        {
            return;
        }

        var text = textBuilder.ToString();
        messages.Add(role == AIChatRole.System
            ? OpenAIChatMessage.CreateSystemMessage(text)
            : role == AIChatRole.Assistant
                ? OpenAIChatMessage.CreateAssistantMessage(text)
                : OpenAIChatMessage.CreateUserMessage(text));
        textBuilder.Clear();
    }
}
