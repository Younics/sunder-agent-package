using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderPackageOperations
{
    private async Task<T> WithViewSessionAsync<T>(BuilderProjectInspection project, Func<IPackageDevelopmentSession, CancellationToken, Task<T>> action, CancellationToken token, string? expectedOwner = null)
    {
        using var operation = Begin(token);
        var gate = Gate(project.EntryPoint);
        var entered = false;
        try
        {
            await gate.WaitAsync(operation.Token).ConfigureAwait(false); entered = true;
            operation.Token.ThrowIfCancellationRequested();
            var session = GetSession(project.EntryPoint) ?? throw new InvalidOperationException("Load this exact development package in Maker before accessing its open view.");
            if (expectedOwner is not null && session.Identity != expectedOwner) throw new InvalidOperationException("The selected development owner was replaced. Query current package views again; never replay an old input.");
            RequireMutable(session);
            if (session.SourcePackageId != project.PackageId)
                throw new InvalidOperationException("The development owner identifies a different package. Unload it before changing package identity.");
            return await action(session.Handle, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (error.CancellationToken == operation.Token && operation.IsCancellationRequested)
        { throw PublicCancellation(error, token); }
        finally { if (entered) gate.Release(); }
    }
}
