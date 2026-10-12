using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentOwnerSourceMigrationTests
{
    // Observe and control dispatch around the actual generated domain-service handler.
    // No alternate implementation service or domain/wire DTO adapter is compiled.
    private sealed class OwnerProviderProbe(ISunderRpcServiceHandler inner) : ISunderRpcServiceHandler
    {
        public List<(string Route, JsonElement Request)> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public Exception? StreamFailure { get; set; }
        public bool Block { get; set; }
        public TaskCompletionSource Started { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentTransportProfile? OverrideProfile { get; set; }

        public async ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId,
            string methodId, JsonElement request, CancellationToken cancellationToken = default)
        {
            Requests.Add((methodId, request.Clone()));
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            if (methodId == "transport-profile" && OverrideProfile is { } profile)
                return JsonSerializer.SerializeToElement(profile, OwnerJson);
            return await inner.InvokeUnaryAsync(context, serviceId, methodId, request, cancellationToken);
        }

        public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId,
            string methodId, JsonElement request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add((methodId, request.Clone()));
            await foreach (var item in inner.InvokeServerStreamAsync(context, serviceId, methodId, request, cancellationToken))
            {
                yield return item;
                if (StreamFailure is not null) throw StreamFailure;
            }
        }
    }
}
