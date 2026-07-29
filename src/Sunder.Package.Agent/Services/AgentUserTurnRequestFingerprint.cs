using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services;

internal static class AgentUserTurnRequestFingerprint
{
    internal static string Compute(
        Guid sessionId,
        string profileId,
        string workspaceId,
        string userMessage,
        AgentRunAdmissionKind admissionKind,
        Guid? rollbackAnchorTurnId,
        IReadOnlyList<AgentAdmissionAttachmentDescriptor> attachments)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "sunder-agent-user-turn-admission-v1");
        Append(hash, sessionId.ToString("D"));
        Append(hash, profileId);
        Append(hash, workspaceId);
        Append(hash, userMessage);
        Append(hash, admissionKind.ToString());
        Append(hash, rollbackAnchorTurnId?.ToString("D") ?? string.Empty);
        Append(hash, attachments.Count);
        foreach (var attachment in attachments)
        {
            Append(hash, attachment.FileName);
            Append(hash, attachment.MediaType ?? string.Empty);
            Append(hash, attachment.SizeBytes);
            Append(hash, attachment.Sha256.ToLowerInvariant());
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
