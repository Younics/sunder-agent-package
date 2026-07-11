namespace Sunder.Package.Agent.Provider.Gemini;

internal sealed class GeminiToolCorrelation
{
    private readonly Dictionary<string, string> _functionNamesByCallId = new(StringComparer.Ordinal);

    public void Register(string callId, string functionName)
    {
        _functionNamesByCallId[callId] = functionName;
    }

    public string Resolve(string callId)
        => _functionNamesByCallId.TryGetValue(callId, out var functionName)
            ? functionName
            : throw new InvalidOperationException(
                $"No preceding Gemini function call was found for call ID '{callId}'.");
}
