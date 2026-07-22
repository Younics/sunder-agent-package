namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes a host-owned package resource that may be exposed inside an execution target.
/// </summary>
/// <remarks>
/// Resource descriptors are candidates, not mounts or permission grants. The execution target must canonicalize the host path, prevent unsafe
/// target-path collisions or traversal, and decline resources it cannot expose with the requested access. Ownership of the host files remains
/// with the source package. Metadata is non-secret routing data and should be treated as immutable and untrusted by consumers.
/// </remarks>
/// <param name="ResourceId">The stable identity of the resource within its source and resource kind.</param>
/// <param name="ResourceKind">A stable discriminator describing how consumers interpret the resource.</param>
/// <param name="SourceId">The stable identity of the package or component that owns the host resource.</param>
/// <param name="DisplayName">The human-readable name shown when the resource is presented to users or models.</param>
/// <param name="HostPath">
/// The host filesystem root owned by the source. It may be outside workspace roots and must be canonicalized before exposure.
/// </param>
/// <param name="PreferredExecutionPath">
/// A preferred target-visible root. It is a hint only; the resolver may relocate or reject it to preserve containment and uniqueness.
/// </param>
/// <param name="AccessMode">
/// The maximum intended access to the exposed resource. A resolver must not broaden it, but this value alone does not prove operating-system enforcement.
/// </param>
/// <param name="Metadata">
/// Optional non-secret source metadata. Keys and values cross extension boundaries and must not be interpreted as trusted authorization input.
/// </param>
public sealed record AgentExecutionResourceDescriptor(
    string ResourceId,
    string ResourceKind,
    string SourceId,
    string DisplayName,
    string HostPath,
    string PreferredExecutionPath,
    AgentExecutionResourceAccessMode AccessMode = AgentExecutionResourceAccessMode.ReadOnly,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Describes a host-owned package resource that an execution target actually exposed at a target-visible path.
/// </summary>
/// <remarks>
/// This record owns no file handle, mount lease, stream, or disposal responsibility. The resolver remains responsible for any target-side
/// materialization lifecycle. Access mode is declarative unless the selected backend explicitly provides an isolation guarantee.
/// </remarks>
/// <param name="ResourceId">The source-defined resource identity preserved from the candidate descriptor.</param>
/// <param name="ResourceKind">The stable resource-kind discriminator preserved from the candidate descriptor.</param>
/// <param name="SourceId">The identity of the package or component that owns the host resource.</param>
/// <param name="DisplayName">The human-readable resource name.</param>
/// <param name="HostPath">The canonical host resource root retained for host-side correlation; consumers must avoid disclosing it unnecessarily.</param>
/// <param name="ExecutionPath">The canonical path through which commands in the selected target can address the resource.</param>
/// <param name="AccessMode">The effective intended access, which must be no broader than the candidate's requested mode.</param>
/// <param name="Metadata">Optional non-secret source metadata, preserved as immutable descriptive data.</param>
public sealed record AgentResolvedExecutionResource(
    string ResourceId,
    string ResourceKind,
    string SourceId,
    string DisplayName,
    string HostPath,
    string ExecutionPath,
    AgentExecutionResourceAccessMode AccessMode = AgentExecutionResourceAccessMode.ReadOnly,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Declares the maximum intended access when a host resource is exposed to an execution target.
/// </summary>
/// <remarks>
/// The value guides target materialization but is not by itself an operating-system security boundary. For example, a trusted local process
/// may retain host-user access beyond what a descriptor declares.
/// </remarks>
public enum AgentExecutionResourceAccessMode
{
    /// <summary>
    /// Consumers may inspect the resource but must not modify it; capable targets should enforce read-only exposure.
    /// </summary>
    ReadOnly = 0,

    /// <summary>
    /// Consumers may inspect and modify the resource, subject to target capabilities and separately evaluated permissions.
    /// </summary>
    ReadWrite = 1,
}
