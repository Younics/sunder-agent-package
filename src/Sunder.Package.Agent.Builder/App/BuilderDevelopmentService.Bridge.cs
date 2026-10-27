using System.Text.Json;
using Sunder.Generated.Packages.Younics_Sunder_Package_Maker_Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderDevelopmentService
{
    private readonly string _appId = Guid.NewGuid().ToString("N");
    private Task _consumer = Task.CompletedTask;
    private CancellationTokenRegistration _activationStop;
    private bool _started;

    public void Start(CancellationToken activationCancellationToken)
    {
        activationCancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_started) return;
            ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
            _started = true;
            _activationStop = activationCancellationToken.Register(_lifetime.Cancel);
            if (ExecutionContext.IsFlowSuppressed()) _consumer = Task.Run(ConsumeAsync);
            else using (ExecutionContext.SuppressFlow()) _consumer = Task.Run(ConsumeAsync);
        }
    }

    private async Task ConsumeAsync()
    {
        var token = _lifetime.Token;
        ServiceProvider<IBuilderRuntimeService>? worker = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (worker is null)
                    {
                        var candidates = (await runtimes.ListAsync(token).ConfigureAwait(false)).Providers.Where(provider => provider.State == ServiceProviderState.Active
                            && provider.PackageId == "younics.sunder.package.maker").Take(2).ToArray();
                        if (candidates.Length != 1) throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "maker.development.unavailable", "One exact Maker Worker is required."));
                        worker = candidates[0];
                        if (await worker.Service.ConnectDevelopmentAppAsync(_appId, token).ConfigureAwait(false) != _appId)
                            throw new InvalidDataException("Maker returned a different App bridge identity.");
                    }
                    var batch = await BuilderContent.ReadAsync<BuilderDevelopmentCommandBatch>(worker.Service.ReadDevelopmentCommandAsync(_appId, token), token).ConfigureAwait(false);
                    if (batch.Command is not { } command) continue;
                    string response = "null"; string? error = null;
                    try
                    {
                        var result = command.Operation switch
                        {
                            "info" => GetInfoAsync(command.RequestJson, token),
                            "logs" => GetLogsAsync(command.RequestJson, token),
                            "query" => QueryPackageAsync(command.RequestJson, token),
                            "view-query" => QueryViewAsync(command.RequestJson, token),
                            "screenshot" => ScreenshotAsync(command.RequestJson, token),
                            "inspect-view" => InspectViewAsync(command.RequestJson, token),
                            "observe" => GetOperationAsync(command.OperationId, command.RequestJson, token),
                            "apply" or "open-view" or "input-view" => StartToolsetOperationAsync(command.OperationId, command.Operation, command.RequestJson, token),
                            "build" or "hotreload" or "interact-view" => StartOperationAsync(command.OperationId, command.Operation, command.RequestJson, token),
                            _ => throw new InvalidDataException("Unknown Maker App request."),
                        };
                        using var json = await BuilderContent.ReadAsync<JsonDocument>(result, token).ConfigureAwait(false);
                        response = json.RootElement.GetRawText();
                    }
                    catch (Exception failure) when (TryClassifyRequestFailure(failure, out _))
                    {
                        TryClassifyRequestFailure(failure, out var code);
                        // An accepted mutation remains observable by its original
                        // operation id even if rendering its acknowledgement failed.
                        bool unknown;
                        lock (_sync) unknown = _operations.ContainsKey(command.OperationId) || _toolsetOperations.ContainsKey(command.OperationId);
                        error = new BuilderDevelopmentFailure(code!, BuilderJobText.Safe(failure.Message, 4096), unknown).Encode();
                    }
                    await worker.Service.CompleteDevelopmentCommandAsync(_appId, command.RequestId, response, error, token).ConfigureAwait(false);
                }
                catch (SunderRpcException error) when (!token.IsCancellationRequested && IsObservationUnavailable(error))
                {
                    // Reconnect command observation only. A read command was already
                    // removed from its original queue; it is never replayed here.
                    worker = null;
                    await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException error) when (error.CancellationToken == token && token.IsCancellationRequested) { }
        catch (SunderRpcException error) when (token.IsCancellationRequested && IsObservationUnavailable(error))
        {
            // Retirement can settle the generated read with a stale-endpoint
            // failure after activation cancellation wins. That is an ended
            // observation, not a cleanup fault or authority to replay the batch.
        }
    }

    private static bool IsObservationUnavailable(SunderRpcException error)
        => error.Error.Kind is SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.ResourceExhausted
            || error.Error is { Kind: SunderRpcErrorKind.Domain, Code: "agent.builder.development-unavailable" };

    private static bool TryClassifyRequestFailure(Exception failure, out string? code)
    {
        code = failure switch
        {
            SunderRpcException { Error: { Kind: SunderRpcErrorKind.Domain, Code:
                "agent.builder.invalid-request" or "agent.builder.request-too-large" or "agent.builder.project-conflict"
                or "maker.development.invalid-request" } } => "maker.invalid-request",
            SunderRpcException { Error: { Kind: SunderRpcErrorKind.Domain, Code: "agent.builder.busy" or "maker.busy" } } => "maker.busy",
            SunderRpcException { Error: { Kind: SunderRpcErrorKind.Domain, Code:
                "agent.builder.development-unavailable" or "agent.builder.backend-changed"
                or "maker.development.unavailable" or "maker.development.operation-not-found" } } => "maker.development.unavailable",
            SunderRpcException { Error: { Kind: SunderRpcErrorKind.Domain, Code:
                "agent.builder.operation-failed" or "agent.builder.response-too-large" } } => "maker.operation-failed",
            SunderRpcException { Error.Kind: SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.StaleEndpoint
                or SunderRpcErrorKind.ResourceExhausted or SunderRpcErrorKind.DeadlineExceeded } => "maker.development.unavailable",
            FileNotFoundException or DirectoryNotFoundException or InvalidDataException or ArgumentException or JsonException => "maker.invalid-request",
            IOException or UnauthorizedAccessException => "maker.operation-failed",
            InvalidOperationException => "maker.development.unavailable",
            NotSupportedException => "maker.development.unavailable",
            _ => null,
        };
        return code is not null;
    }
}
