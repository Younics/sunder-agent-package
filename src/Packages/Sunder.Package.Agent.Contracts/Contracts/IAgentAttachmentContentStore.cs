using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentAttachmentContentStore
{
    Task<byte[]> ReadAttachmentBytesAsync(AgentAttachmentMetadata metadata, CancellationToken cancellationToken = default);
}
