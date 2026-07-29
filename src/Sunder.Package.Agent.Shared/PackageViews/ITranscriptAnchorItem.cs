namespace Sunder.Package.Agent.Shared.PackageViews;

internal enum TranscriptAnchorItemRole
{
    Transient,
    Persistent,
    TailSentinel,
}

internal interface ITranscriptAnchorItem
{
    object AnchorKey { get; }

    TranscriptAnchorItemRole AnchorRole { get; }
}
