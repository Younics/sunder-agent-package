using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentSessionTitleService(
    AgentSessionService sessionService,
    AgentRunProviderResolver providerResolver,
    AgentRunEventLogger runEventLogger,
    AgentBackgroundWorkService? backgroundWork = null)
{
    private const int MaxPromptMessageChars = 2_000;
    private const int MaxTitleChars = 64;
    private const string TitleInstructions = "Generate a concise chat session title from the user's first message. Return only the title, no quotes, no markdown, no explanation. Use 2 to 6 words when possible. Do not include the word Session unless it is part of the user's task.";

    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentRunProviderResolver _providerResolver = providerResolver;
    private readonly AgentRunEventLogger _runEventLogger = runEventLogger;
    private readonly AgentBackgroundWorkService? _backgroundWork = backgroundWork;

    public bool ShouldGenerateTitleForFirstUserMessage(AgentSessionRecord session) =>
        IsAutoTitleCandidate(session) && _sessionService.ListTurns(session.SessionId).Count == 0;

    public void ScheduleTitleFromFirstUserMessage(
        AgentSessionRecord session,
        AgentProfileRecord profile,
        string userMessage,
        Guid runId,
        long runRevision)
    {
        if (!IsAutoTitleCandidate(session) || string.IsNullOrWhiteSpace(userMessage))
        {
            return;
        }

        _backgroundWork?.TryQueue(cancellationToken => GenerateAndApplyTitleAsync(
            session.SessionId,
            profile,
            userMessage,
            runId,
            runRevision,
            cancellationToken));
    }

    private async Task GenerateAndApplyTitleAsync(
        Guid sessionId,
        AgentProfileRecord profile,
        string userMessage,
        Guid runId,
        long runRevision,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var providerSelection = _providerResolver.ResolveChatProvider(profile);
            var provider = providerSelection.Provider;
            if (provider is null)
            {
                return;
            }

            var modelId = await ResolveUtilityModelIdAsync(provider, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return;
            }

            var readiness = await provider.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
            if (readiness.Status != AgentProviderReadinessStatus.Ready)
            {
                Log(
                    PackageLogLevel.Debug,
                    sessionId,
                    runId,
                    runRevision,
                    "session.title.skipped",
                    readiness.Message,
                    stopwatch.ElapsedMilliseconds,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["provider.id"] = provider.Descriptor.ProviderId,
                        ["model.id"] = modelId,
                        ["provider.readiness_status"] = readiness.Status,
                    });
                return;
            }

            using var chatClient = await provider.CreateChatClientAsync(
                new AgentChatClientContext(
                    provider.Descriptor.ProviderId,
                    modelId,
                    CorrelationAttributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["session.id"] = sessionId,
                        ["run.id"] = runId,
                        ["run.revision"] = runRevision,
                        ["utility.task"] = "session-title",
                    }),
                cancellationToken).ConfigureAwait(false);

            var response = await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, $"First user message:\n{TruncatePromptMessage(userMessage)}")],
                new ChatOptions
                {
                    Instructions = TitleInstructions,
                    MaxOutputTokens = 32,
                    ModelId = modelId,
                    ToolMode = ChatToolMode.None,
                },
                cancellationToken).ConfigureAwait(false);
            var title = NormalizeGeneratedTitle(response.Text);
            if (title is null || AgentSessionTitleDefaults.IsGeneratedDefaultTitle(title))
            {
                return;
            }

            var current = _sessionService.GetSession(sessionId);
            if (current is null || !IsAutoTitleCandidate(current))
            {
                return;
            }

            _sessionService.UpdateSession(current with
            {
                Title = title,
                UpdatedAtUtc = current.UpdatedAtUtc,
            });

            Log(
                PackageLogLevel.Information,
                sessionId,
                runId,
                runRevision,
                "session.title.generated",
                "Session title generated.",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["provider.id"] = provider.Descriptor.ProviderId,
                    ["model.id"] = modelId,
                    ["session.title.length"] = title.Length,
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(
                PackageLogLevel.Warning,
                sessionId,
                runId,
                runRevision,
                "session.title.failed",
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                exception: ex);
        }
    }

    private async ValueTask<string?> ResolveUtilityModelIdAsync(
        IAgentChatProvider provider,
        CancellationToken cancellationToken)
    {
        if (provider is IAgentUtilityModelProvider utilityModelProvider)
        {
            var modelId = await utilityModelProvider.ResolveUtilityModelIdAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                return modelId.Trim();
            }
        }

        var models = await provider.GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false);
        return models.FirstOrDefault(model => model.IsRecommended)?.ModelId
            ?? models.FirstOrDefault()?.ModelId;
    }

    private static bool IsAutoTitleCandidate(AgentSessionRecord session) =>
        session.ParentSessionId is null && AgentSessionTitleDefaults.IsGeneratedDefaultTitle(session.Title);

    private static string TruncatePromptMessage(string value) =>
        value.Length <= MaxPromptMessageChars ? value : value[..MaxPromptMessageChars];

    private static string? NormalizeGeneratedTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var title = WhitespacePattern().Replace(value, " ").Trim();
        title = LeadingListMarkerPattern().Replace(title, string.Empty).Trim();
        if (title.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
        {
            title = title["Title:".Length..].Trim();
        }

        title = title.Trim(' ', '"', '\'', '`', '*', '_');
        title = title.TrimEnd(' ', '.', ':', ';', '!', '?');
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return title.Length <= MaxTitleChars
            ? title
            : TrimToWordBoundary(title, MaxTitleChars);
    }

    private static string TrimToWordBoundary(string value, int maxChars)
    {
        var truncated = value[..maxChars].TrimEnd();
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace >= 24)
        {
            truncated = truncated[..lastSpace].TrimEnd();
        }

        return truncated.TrimEnd(' ', '.', ':', ';', '!', '?');
    }

    private void Log(
        PackageLogLevel level,
        Guid sessionId,
        Guid runId,
        long runRevision,
        string eventName,
        string message,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null) =>
        _runEventLogger.LogRunEvent(
            level,
            sessionId,
            runId,
            runRevision,
            eventName,
            message,
            elapsedMilliseconds,
            attributes,
            exception);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"^(?:[-*]\s+|\d+[.)]\s+)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingListMarkerPattern();
}
