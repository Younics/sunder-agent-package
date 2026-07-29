using System.Runtime.ExceptionServices;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Storage;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchService(
    HistorySearchStore projection,
    AgentLocalStore authoritative,
    HistoryEmbeddingProviderCatalog embeddingProviders,
    HistorySemanticOperationFence semanticFence,
    HistorySearchIndexingService indexer,
    HistorySearchRuntimeState state)
{
    internal async Task<HistorySearchResponse> SearchAsync(
        HistorySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        request = NormalizeRequest(request);
        try
        {
            return await SearchCoreAsync(
                    request,
                    allowGenerationRestart: true,
                    allowMutationRetry: true,
                    restarted: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new HistorySearchResponse(
                [],
                null,
                IsPartial: true,
                BoundStatus(ReadProjection(() => state.Current)),
                Restarted: request.Continuation is not null);
        }
        catch (HistoryProjectionOperationException exception)
        {
            if (await TryRecoverAsync(exception.InnerException!, cancellationToken).ConfigureAwait(false))
            {
                return new HistorySearchResponse(
                    [],
                    null,
                    IsPartial: true,
                    BoundStatus(ReadProjection(() => state.Refresh())),
                    Restarted: request.Continuation is not null);
            }
            ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
            throw;
        }
    }

    internal async Task<HistorySearchState> GetStateAsync(
        HistorySearchStateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var configuration = ReadProjection(projection.GetConfiguration);
            using var projectionLease = semanticFence.Begin(configuration.Revision, cancellationToken);
            projectionLease.ThrowIfCurrent();
            var filterOptions = LoadFilterOptions(request, cancellationToken);
            projectionLease.ThrowIfCurrent();
            var result = new HistorySearchState(
                BoundStatus(ReadProjection(() => state.Refresh())),
                [],
                [],
                null,
                filterOptions.Workspaces,
                filterOptions.Sessions,
                filterOptions.Profiles,
                filterOptions.Continuation);
            return SanitizeState(FitState(
                result,
                request.Continuation,
                filterOptions.InsertionHighWaterMark));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HistoryProjectionOperationException exception)
        {
            if (await TryRecoverAsync(exception.InnerException!, cancellationToken).ConfigureAwait(false))
            {
                return SanitizeState(new HistorySearchState(
                    BoundStatus(ReadProjection(() => state.Refresh())),
                    [],
                    [],
                    null,
                    [],
                    [],
                    [],
                    null));
            }
            ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
            throw;
        }
    }

    internal async Task<HistorySearchCommandResult> ExecuteCommandAsync(
        HistorySearchCommand command,
        CancellationToken cancellationToken = default)
    {
        var projectionOperationStarted = false;
        try
        {
            switch (command.Kind)
            {
                case HistorySearchCommandKind.ConfigureSemantic:
                    {
                        projectionOperationStarted = true;
                        await indexer.ConfigureSemanticAsync(false, selection: null, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                case HistorySearchCommandKind.Rebuild:
                    projectionOperationStarted = true;
                    indexer.RequestRebuild();
                    break;
                case HistorySearchCommandKind.ClearDerivedIndex:
                    projectionOperationStarted = true;
                    await indexer.ClearDerivedIndexAsync(cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException("Unknown history search command.");
            }
            return new HistorySearchCommandResult(BoundStatus(ReadProjection(() => state.Refresh())));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (projectionOperationStarted
                                          && HistorySearchStore.IsConfirmedProjectionFailure(exception))
        {
            if (await TryRecoverAsync(exception, cancellationToken).ConfigureAwait(false))
            {
                return new HistorySearchCommandResult(BoundStatus(ReadProjection(() => state.Refresh())));
            }
            throw;
        }
    }

    private async Task<HistorySearchResponse> SearchCoreAsync(
        HistorySearchRequest request,
        bool allowGenerationRestart,
        bool allowMutationRetry,
        bool restarted,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!projection.IsAvailable || ReadProjection(projection.TryPinActiveProjection) is not { } pin)
        {
            return new HistorySearchResponse(
                [],
                null,
                IsPartial: false,
                BoundStatus(state.Current),
                Restarted: restarted);
        }
        var configuration = ReadProjection(projection.GetConfiguration);
        if (configuration.Revision != pin.ConfigurationRevision)
        {
            return await RestartForGenerationChangeAsync(request, allowGenerationRestart, cancellationToken)
                .ConfigureAwait(false);
        }
        using var searchLease = semanticFence.Begin(configuration.Revision, cancellationToken);
        searchLease.ThrowIfCurrent();
        var sessionScope = request.SessionId is { } sessionId
            ? authoritative.GetHistorySessionScope(sessionId, request.IncludeChildSessions)
            : null;
        var queryPlan = HistoryFtsQueryPlan.Create(request.Query);

        var lexical = ReadProjection(() => projection.SearchLexical(
            pin.TextGenerationId,
            request,
            queryPlan,
            HistorySearchLimits.CandidateLimit,
            sessionScope));
        var partial = false;
        var fused = Fuse(lexical, []);
        var stored = ReadProjection(() => projection.LoadDocuments(
                pin.TextGenerationId,
                fused.Select(static item => item.DocumentId).ToArray())
            .ToDictionary(static document => document.DocumentId, StringComparer.Ordinal));
        var bodySnippets = ReadProjection(() => projection.LoadBodySnippets(
            pin.TextGenerationId,
            queryPlan,
            fused.Select(static item => item.DocumentId).ToArray()));
        if (!ReadProjection(() => projection.IsPinCurrent(pin)))
        {
            return await RestartForGenerationChangeAsync(request, allowGenerationRestart, cancellationToken)
                .ConfigureAwait(false);
        }

        var validations = authoritative.GetHistorySearchValidations(
            stored.Values.Select(static document => document.TurnId).ToArray());
        var valid = new List<RankedDocument>();
        var candidateChanged = false;
        foreach (var candidate in fused)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!candidate.IsConsistent)
            {
                candidateChanged = true;
                continue;
            }
            if (!stored.TryGetValue(candidate.DocumentId, out var document))
            {
                candidateChanged = true;
                continue;
            }
            if (candidate.SourceContentRevision != document.SourceContentRevision
                || !string.Equals(candidate.ProjectionHash, document.ProjectionHash, StringComparison.Ordinal))
            {
                candidateChanged = true;
                continue;
            }
            if (!IsValid(document, validations.GetValueOrDefault(document.TurnId)))
            {
                indexer.QueueAuthoritativeTurnReindex(document);
                continue;
            }
            var snippet = bodySnippets.GetValueOrDefault(document.DocumentId)
                          ?? HistorySearchText.SanitizeSnippet(document.DisplaySnippet);
            valid.Add(new RankedDocument(document, candidate, snippet));
        }
        if (candidateChanged && allowMutationRetry)
        {
            return await SearchCoreAsync(
                request,
                allowGenerationRestart,
                allowMutationRetry: false,
                restarted,
                cancellationToken).ConfigureAwait(false);
        }
        if (!ReadProjection(() => projection.IsPinCurrent(pin)))
        {
            return await RestartForGenerationChangeAsync(request, allowGenerationRestart, cancellationToken)
                .ConfigureAwait(false);
        }

        var requestFingerprint = CreateRequestFingerprint(request);
        var rankIdentity = CreateRankIdentity(valid);
        var continuation = DecodeContinuation(
            request.Continuation,
            requestFingerprint,
            rankIdentity,
            pin,
            valid,
            out var continuationRestarted);
        partial |= continuationRestarted;
        restarted |= continuationRestarted;
        var status = BoundStatus(ReadProjection(() => state.Refresh()));
        var results = new List<HistorySearchHit>();
        var maximum = Math.Min(request.Limit, Math.Max(0, valid.Count - continuation));
        for (var index = 0; index < maximum; index++)
        {
            var candidate = valid[continuation + index];
            var hit = ToHit(candidate.Document, candidate.Ranking.Reasons, candidate.Snippet);
            var trial = results.Append(hit).ToArray();
            var consumed = continuation + trial.Length;
            var next = consumed < valid.Count
                ? EncodeContinuation(
                    consumed,
                    requestFingerprint,
                    rankIdentity,
                    pin,
                    valid[consumed - 1].Document.DocumentId)
                : null;
            var response = new HistorySearchResponse(trial, next, partial, status, restarted);
            if (Runtime.AgentRuntimePayloadLimits.GetSerializedByteCount(response)
                > Runtime.AgentRuntimePayloadLimits.MaximumOperationResponseBytes)
            {
                partial = true;
                break;
            }
            results.Add(hit);
        }
        var nextOffset = continuation + results.Count;
        var nextContinuation = nextOffset < valid.Count
            ? EncodeContinuation(
                nextOffset,
                requestFingerprint,
                rankIdentity,
                pin,
                nextOffset == 0 ? null : valid[nextOffset - 1].Document.DocumentId)
            : null;
        searchLease.ThrowIfCurrent();
        var final = new HistorySearchResponse(results, nextContinuation, partial, status, restarted);
        if (Runtime.AgentRuntimePayloadLimits.GetSerializedByteCount(final)
            > Runtime.AgentRuntimePayloadLimits.MaximumOperationResponseBytes)
        {
            throw new InvalidOperationException("History response metadata exceeds the Runtime transport limit.");
        }
        return final;
    }

    private async Task<HistorySearchResponse> RestartForGenerationChangeAsync(
        HistorySearchRequest request,
        bool allowGenerationRestart,
        CancellationToken cancellationToken)
    {
        if (!allowGenerationRestart)
        {
            return new HistorySearchResponse(
                [],
                null,
                IsPartial: true,
                BoundStatus(ReadProjection(() => state.Refresh())),
                Restarted: true);
        }
        return await SearchCoreAsync(
            request with { Continuation = null },
            allowGenerationRestart: false,
            allowMutationRetry: true,
            restarted: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(IReadOnlyList<HistorySemanticCandidate> Candidates, bool IsPartial)> SearchSemanticAsync(
        HistorySearchRequest request,
        HistoryProjectionPin pin,
        HistoryProjectionConfiguration configuration,
        HistorySessionScope? sessionScope,
        HistorySemanticOperationFence.Lease lease,
        CancellationToken cancellationToken)
    {
        var generation = ReadProjection(projection.GetActiveEmbeddingGeneration);
        if (generation?.Dimensions is not { } dimensions
            || !GenerationMatches(generation, pin, configuration))
        {
            return ([], true);
        }

        try
        {
            lease.ThrowIfCurrent();
            var resolved = await embeddingProviders.ResolveSelectionAsync(
                configuration.EmbeddingProviderPackageId,
                configuration.EmbeddingProviderId,
                configuration.EmbeddingModelId,
                lease.CancellationToken).ConfigureAwait(false);
            lease.ThrowIfCurrent();
            if (!string.Equals(resolved.SpaceFingerprint, configuration.EmbeddingSpaceFingerprint, StringComparison.Ordinal))
            {
                indexer.RequestProviderRefresh();
                return ([], true);
            }
            var readiness = await embeddingProviders
                .GetReadinessAsync(resolved, lease.CancellationToken).ConfigureAwait(false);
            lease.ThrowIfCurrent();
            if (readiness.Status != AgentProviderReadinessStatus.Ready)
            {
                indexer.ReportSemanticUnavailable(
                    "embedding-provider-not-ready",
                    "The selected embedding provider is not ready. Lexical search remains available.");
                return ([], true);
            }
            var generated = await embeddingProviders.GenerateEmbeddingAsync(
                resolved,
                request.Query,
                lease.CancellationToken).ConfigureAwait(false);
            lease.ThrowIfCurrent();
            var currentFingerprint = await embeddingProviders
                .GetSpaceFingerprintAsync(resolved, lease.CancellationToken)
                .ConfigureAwait(false);
            lease.ThrowIfCurrent();
            if (!string.Equals(currentFingerprint, configuration.EmbeddingSpaceFingerprint, StringComparison.Ordinal))
            {
                indexer.RequestProviderRefresh();
                return ([], true);
            }
            if (!HistoryVectorCodec.TryNormalize(
                    generated,
                    configuration.EmbeddingModelId!,
                    dimensions,
                    out _,
                    out var queryVector))
            {
                return ([], true);
            }
            lease.ThrowIfCurrent();
            var wasTruncated = false;
            var candidates = ReadProjection(() => projection.SearchSemanticTopK(
                pin,
                configuration,
                request,
                queryVector,
                dimensions,
                HistorySearchLimits.MaximumSemanticResults,
                out wasTruncated,
                sessionScope));
            lease.ThrowIfCurrent();
            return (candidates, wasTruncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HistoryProjectionOperationException)
        {
            throw;
        }
        catch
        {
            indexer.ReportSemanticUnavailable(
                "embedding-provider-unavailable",
                "The selected embedding provider is unavailable. Lexical search remains available.");
            return ([], true);
        }
    }

    private (IReadOnlyList<HistorySearchFilterOption> Workspaces,
        IReadOnlyList<HistorySearchFilterOption> Sessions,
        IReadOnlyList<HistorySearchFilterOption> Profiles,
        string? Continuation,
        long? InsertionHighWaterMark) LoadFilterOptions(
            HistorySearchStateRequest request,
            CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(request.Limit, 1, HistorySearchLimits.MaximumStateOptions);
        if (!request.IncludeAdvancedFilters)
        {
            var workspaceId = request.WorkspaceId;
            var workspace = workspaceId is null ? null : authoritative.GetWorkspace(workspaceId);
            return (workspace is null
                    ? []
                    : [new HistorySearchFilterOption(
                        workspace.WorkspaceId,
                        Output(workspace.DisplayName, HistorySearchLimits.MaximumDisplayCharacters))],
                [],
                [],
                null,
                null);
        }
        var workspaces = authoritative.ListWorkspaces()
            .Take(HistorySearchLimits.MaximumStateOptions)
            .Select(static workspace => new HistorySearchFilterOption(
                workspace.WorkspaceId,
                Output(workspace.DisplayName, HistorySearchLimits.MaximumDisplayCharacters)))
            .ToArray();
        var profiles = authoritative.ListProfiles()
            .Where(static profile => !profile.IsInternal)
            .Take(HistorySearchLimits.MaximumStateOptions)
            .Select(static profile => new HistorySearchFilterOption(
                profile.ProfileId,
                Output(profile.DisplayName, HistorySearchLimits.MaximumDisplayCharacters)))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = DecodeStateContinuation(request.Continuation);
        var sessionPage = authoritative.ListHistorySourceSessionsPage(
            cursor.AfterSessionId,
            limit,
            cursor.InsertionHighWaterMark);
        var sessions = sessionPage.Sessions
            .Select(static session => new HistorySearchFilterOption(
                session.SessionId.ToString("D"),
                Output(session.Title, HistorySearchLimits.MaximumDisplayCharacters),
                session.WorkspaceId))
            .ToArray();
        var continuation = sessionPage.Continuation is null
            ? null
            : EncodeStateContinuation(sessionPage.Continuation, sessionPage.InsertionHighWaterMark);
        return (workspaces, sessions, profiles, continuation, sessionPage.InsertionHighWaterMark);
    }

    private static HistorySearchState FitState(
        HistorySearchState value,
        string? previousContinuation,
        long? insertionHighWaterMark)
    {
        if (Runtime.AgentRuntimePayloadLimits.GetSerializedByteCount(value)
            <= Runtime.AgentRuntimePayloadLimits.MaximumOperationResponseBytes)
        {
            return value;
        }
        var sessions = value.Sessions.ToList();
        while (sessions.Count > 0)
        {
            sessions.RemoveAt(sessions.Count - 1);
            var continuation = sessions.Count > 0 && insertionHighWaterMark is { } highWaterMark
                ? EncodeStateContinuation(sessions[^1].Id, highWaterMark)
                : previousContinuation;
            var reduced = value with { Sessions = sessions.ToArray(), Continuation = continuation };
            if (Runtime.AgentRuntimePayloadLimits.GetSerializedByteCount(reduced)
                <= Runtime.AgentRuntimePayloadLimits.MaximumOperationResponseBytes)
            {
                return reduced;
            }
        }
        throw new InvalidOperationException("History state metadata exceeds the Runtime transport limit.");
    }

    private static bool IsValid(HistoryStoredDocument document, HistorySearchValidation? validation)
        => validation is not null
           && validation.TurnId == document.TurnId
           && validation.SessionId == document.SessionId
           && string.Equals(validation.WorkspaceId, document.WorkspaceId, StringComparison.Ordinal)
           && validation.RootSessionId == document.RootSessionId
           && validation.ParentSessionId == document.ParentSessionId
           && string.Equals(validation.ProfileId, document.ProfileId, StringComparison.Ordinal)
           && validation.ContentRevision == document.SourceContentRevision
           && validation.IsStreaming == document.SourceIsStreaming
           && validation.ItemIds.Contains(document.ItemId)
           && (document.CallId is null || validation.CallIds.Contains(document.CallId))
           && (document.Role is null || validation.Role == document.Role);

    private static HistorySearchHit ToHit(
        HistoryStoredDocument document,
        IReadOnlyList<string> reasons,
        string snippet)
        => new(
            document.DocumentId,
            document.WorkspaceId,
            Output(document.WorkspaceName, HistorySearchLimits.MaximumDisplayCharacters),
            document.SessionId,
            Output(document.SessionTitle, HistorySearchLimits.MaximumDisplayCharacters),
            document.ParentSessionId is not null,
            document.RootSessionId,
            document.ProfileId,
            document.CreatedAtUtc,
            document.Role,
            document.Activity,
            HistorySearchText.SanitizeSnippet(snippet),
            SelectFacets(document.Facets, "path"),
            SelectFacets(document.Facets, "symbol"),
            reasons.Select(reason => Output(reason, 128))
                .Take(HistorySearchLimits.MaximumMatchReasons)
                .ToArray(),
            document.TurnId,
            document.ItemId,
            document.CallId,
            document.AnchorKind);

    private static IReadOnlyList<string> SelectFacets(
        IReadOnlyList<HistoryProjectionFacet> facets,
        string kind)
        => facets
            .Where(facet => string.Equals(facet.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .Select(static facet => Output(facet.Value, HistorySearchLimits.MaximumFacetCharacters))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(HistorySearchLimits.MaximumFacetsPerResult)
            .ToArray();

    private static HistorySearchRequest NormalizeRequest(HistorySearchRequest request)
    {
        var fromUtc = request.FromUtc?.ToUniversalTime();
        var toUtc = request.ToUtc?.ToUniversalTime();
        if (fromUtc > toUtc) (fromUtc, toUtc) = (toUtc, fromUtc);
        return request with
        {
            Query = HistorySearchText.SanitizeQueryInput(
                request.Query,
                HistorySearchLimits.MaximumQueryCharacters),
            WorkspaceId = request.WorkspaceId,
            ProfileId = request.ProfileId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Limit = Math.Clamp(request.Limit, 1, HistorySearchLimits.MaximumResults),
            Continuation = request.Continuation is { Length: <= HistorySearchLimits.MaximumContinuationCharacters }
                ? request.Continuation
                : null,
        };
    }

    private async Task<bool> TryRecoverAsync(Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            return await indexer.RecoverProjectionAsync(exception, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static bool GenerationMatches(
        HistoryProjectionGeneration generation,
        HistoryProjectionPin pin,
        HistoryProjectionConfiguration configuration)
        => generation.GenerationId == pin.EmbeddingGenerationId
           && generation.ParentTextGenerationId == pin.TextGenerationId
           && generation.ConfigurationRevision == pin.ConfigurationRevision
           && string.Equals(generation.ProviderPackageId, configuration.EmbeddingProviderPackageId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(generation.ProviderId, configuration.EmbeddingProviderId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(generation.ModelId, configuration.EmbeddingModelId, StringComparison.Ordinal)
           && string.Equals(generation.EmbeddingSpaceFingerprint, configuration.EmbeddingSpaceFingerprint, StringComparison.Ordinal);

    private static HistorySearchStatus BoundStatus(HistorySearchStatus status)
        => status with
        {
            EmbeddingProviderPackageId = status.EmbeddingProviderPackageId,
            EmbeddingProviderId = status.EmbeddingProviderId,
            EmbeddingModelId = status.EmbeddingModelId,
            FailureCode = status.FailureCode,
            FailureMessage = OptionalOutput(status.FailureMessage, HistorySearchLimits.MaximumStatusCharacters),
            RuntimeInstanceId = status.RuntimeInstanceId,
        };

    private static HistorySearchState SanitizeState(HistorySearchState value)
        => value with
        {
            Status = BoundStatus(value.Status),
            EmbeddingProviders = value.EmbeddingProviders.Select(static option => option with
            {
                PackageId = option.PackageId,
                ProviderId = option.ProviderId,
                DisplayName = Output(option.DisplayName, HistorySearchLimits.MaximumDisplayCharacters),
            }).ToArray(),
            EmbeddingModels = value.EmbeddingModels.Select(static option => option with
            {
                PackageId = option.PackageId,
                ProviderId = option.ProviderId,
                ModelId = option.ModelId,
                DisplayName = Output(option.DisplayName, HistorySearchLimits.MaximumDisplayCharacters),
            }).ToArray(),
            EmbeddingReadiness = value.EmbeddingReadiness is null
                ? null
                : value.EmbeddingReadiness with
                {
                    ProviderId = value.EmbeddingReadiness.ProviderId,
                    Message = Output(value.EmbeddingReadiness.Message, HistorySearchLimits.MaximumStatusCharacters),
                },
            Workspaces = SanitizeOptions(value.Workspaces),
            Sessions = SanitizeOptions(value.Sessions),
            Profiles = SanitizeOptions(value.Profiles),
            Continuation = value.Continuation,
        };

    private static IReadOnlyList<HistorySearchFilterOption> SanitizeOptions(
        IReadOnlyList<HistorySearchFilterOption> values)
        => values.Select(static option => option with
        {
            Id = option.Id,
            DisplayName = Output(option.DisplayName, HistorySearchLimits.MaximumDisplayCharacters),
            ParentId = option.ParentId,
        }).ToArray();

    private static (string? AfterSessionId, long? InsertionHighWaterMark) DecodeStateContinuation(
        string? continuation)
    {
        if (!string.IsNullOrWhiteSpace(continuation)
            && continuation.Length <= HistorySearchLimits.MaximumContinuationCharacters)
        {
            try
            {
                var data = JsonSerializer.Deserialize<StateContinuationData>(
                    Convert.FromBase64String(continuation));
                if (data is { Version: 2, InsertionHighWaterMark: >= 0 }
                    && Guid.TryParse(data.AfterSessionId, out _))
                {
                    return (data.AfterSessionId, data.InsertionHighWaterMark);
                }
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
            }
        }
        return (null, null);
    }

    private static string EncodeStateContinuation(string afterSessionId, long insertionHighWaterMark)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new StateContinuationData(
            Version: 2,
            afterSessionId,
            insertionHighWaterMark)));

    private static T ReadProjection<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (HistorySearchStore.IsConfirmedProjectionFailure(exception))
        {
            throw new HistoryProjectionOperationException(exception);
        }
    }

    private static string Output(string? value, int maximumLength)
        => HistorySearchText.BoundAtRuneBoundary(
            HistorySearchText.NormalizeStoredText(value).Trim(),
            maximumLength);

    private static string? OptionalOutput(string? value, int maximumLength)
    {
        var output = Output(value, maximumLength);
        return output.Length == 0 ? null : output;
    }

    private sealed record StateContinuationData(
        int Version,
        string AfterSessionId,
        long InsertionHighWaterMark);

    private sealed class HistoryProjectionOperationException(Exception innerException)
        : Exception("The history projection operation failed.", innerException);

}
