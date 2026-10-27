using Microsoft.Extensions.AI;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed partial class DefaultAgentBehaviorLoop
{
    private const string InterruptedStreamContinuationInstruction =
        "The previous provider response was interrupted. Its visible partial text is saved. "
        + "Continue from the recorded work without repeating completed content or completed tool calls. "
        + "No local tool calls from that incomplete response were dispatched. Reissue any required undispatched calls with complete arguments. "
        + "An ambiguous or unconfirmed earlier tool execution may have had effects: verify current state before any further mutation.";
    private const string OutputLimitContinuationInstruction =
        "The previous response reached the provider's output-token limit. Its visible partial text is saved. "
        + "Continue from the recorded work without repeating completed content. Break remaining work into smaller pieces. "
        + "No tool calls from that incomplete response were executed. Reissue any required calls with complete arguments.";

    private static AgentProviderMessages WithContinuation(AgentProviderMessages messages, string? instruction)
        => instruction is null ? messages : messages with
        {
            Messages = messages.Messages.Append(new ChatMessage(ChatRole.User, instruction)).ToArray(),
        };
}
