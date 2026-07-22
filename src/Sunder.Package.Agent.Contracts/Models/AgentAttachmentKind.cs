namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Classifies attachment content for provider capability negotiation and prompt projection.
/// </summary>
public enum AgentAttachmentKind
{
    /// <summary>Text decoded by the host and supplied through a bounded text projection.</summary>
    Text = 0,

    /// <summary>Image content that requires image-input support from the selected provider and model.</summary>
    Image = 1,

    /// <summary>PDF content that requires PDF-input support from the selected provider and model.</summary>
    Pdf = 2,

    /// <summary>Audio content that requires audio-input support from the selected provider and model.</summary>
    Audio = 3,

    /// <summary>Video content that requires video-input support from the selected provider and model.</summary>
    Video = 4,

    /// <summary>Unrecognized or generic binary content, which the base prompt pipeline reports but does not send as model input.</summary>
    Binary = 5,
}
