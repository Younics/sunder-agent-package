using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

internal static class AgentPermissionFingerprint
{
    private const string Version = "agent-permission-fingerprint-v1";

    public static string Create(
        Guid runId,
        long runRevision,
        AgentToolDescriptor descriptor,
        string callId,
        string argumentsJson,
        AgentWorkspaceRecord? workspace,
        AgentWorkspaceBindingRecord? binding,
        AgentExecutionTargetDescriptor? target,
        AgentPermissionRequest? permissionRequest)
    {
        var values = new[]
        {
            Version,
            runId.ToString("N"),
            runRevision.ToString(CultureInfo.InvariantCulture),
            NormalizeIdentity(descriptor.ToolId),
            NormalizeIdentity(descriptor.SourceKind),
            NormalizeIdentity(descriptor.SourceId),
            callId.Trim(),
            NormalizeJson(argumentsJson),
            NormalizeIdentity(workspace?.WorkspaceId),
            NormalizeIdentity(binding?.BindingId),
            NormalizeIdentity(binding?.ExtensionPointId),
            NormalizeIdentity(binding?.ContributionId),
            NormalizeIdentity(binding?.Role),
            NormalizeIdentity(target?.TargetKind),
            NormalizeIdentity(target?.TargetId),
            NormalizeIdentity(permissionRequest?.ActionId),
            NormalizeIdentity(permissionRequest?.BoundaryId),
            NormalizeIdentity(permissionRequest?.WorkspaceId),
            NormalizeIdentity(permissionRequest?.BindingId),
            NormalizeResource(permissionRequest?.ResourceReference),
            NormalizeResource(permissionRequest?.Path),
            NormalizeResource(permissionRequest?.Command),
        };

        var material = new StringBuilder();
        foreach (var value in values)
        {
            material.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            material.Append(':');
            material.Append(value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()))).ToLowerInvariant();
    }

    private static string NormalizeIdentity(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string NormalizeResource(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteCanonicalJson(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
