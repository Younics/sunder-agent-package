using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Workspace_Editor;
using Wire = Sunder.Generated.Rpc.WorkspaceEditor;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedWorkspaceEditorClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentWorkspaceEditorContributor, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.workspace-editor.provider-stale");
    private string? _id;
    public string ContributorId
    {
        get
        {
            _calls.ThrowIfStopped();
            return _id ??= _calls.Invoke(async (scope, token) => (await Client(scope).DescribeAsync(new(), token).ConfigureAwait(false)).Id,
                default).AsTask().GetAwaiter().GetResult();
        }
    }
    public bool CanEdit(AgentWorkspaceEditorContext context) => CanEditAsync(context, default).AsTask().GetAwaiter().GetResult();
    internal ValueTask<bool> CanEditAsync(AgentWorkspaceEditorContext context, CancellationToken token)
        => _calls.Invoke((scope, cancellation) => Client(scope).CanEditAsync(AgentConsumerValues.Convert<Wire.AgentWorkspaceEditorContext>(context), cancellation), token);
    public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default)
        => _calls.Invoke<IReadOnlyList<AgentEditorSection>>(async (scope, token) => AgentConsumerValues.Convert<AgentEditorSection[]>(
            await Client(scope).GetSectionsAsync(AgentConsumerValues.Convert<Wire.AgentWorkspaceEditorContext>(context), token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentEditorSaveResult> SaveSectionAsync(AgentWorkspaceEditorContext context, AgentEditorSaveRequest request, CancellationToken cancellationToken = default)
        => _calls.Invoke(async (scope, token) => AgentConsumerValues.Convert<AgentEditorSaveResult>(await Client(scope).SaveSectionAsync(new()
        {
            Context = AgentConsumerValues.Convert<Wire.AgentWorkspaceEditorContext>(context),
            Request = AgentConsumerValues.Convert<Wire.AgentEditorSaveRequest>(request),
        }, token).ConfigureAwait(false)), cancellationToken);

    private Wire.WorkspaceEditorClient Client(ISunderRpcCallScope scope)
        => new PackageApi(scope).WorkspaceEditor.CreateClient(scope, new Api.Provider(provider)).SWorkspace_2deditor;
    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
