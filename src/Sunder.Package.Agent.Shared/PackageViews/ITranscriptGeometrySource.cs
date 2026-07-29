namespace Sunder.Package.Agent.Shared.PackageViews;

internal interface ITranscriptGeometrySource
{
    long RequestedRevision { get; }

    long SettledRevision { get; }

    long GeometryRevision { get; }

    bool IsGeometryPending { get; }

    event EventHandler? GeometryChanged;
}
