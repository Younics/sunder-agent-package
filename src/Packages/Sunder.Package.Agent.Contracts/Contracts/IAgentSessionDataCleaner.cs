namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentSessionDataCleaner
{
    string CleanerId { get; }

    void DeleteSessionData(Guid sessionId);
}
