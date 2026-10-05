using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Execution_Target;

namespace Sunder.Package.Agent.Execution.Docker.Rpc;

internal static class ExecutionValues
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new SunderRpcJsonConverterFactory(), new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false), new UtcConverter() },
    };
    internal static T Convert<T>(object value) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Json), Json)
            ?? throw new InvalidDataException("A Docker execution value could not be mapped.");
    internal static Domain.AgentExecutionTargetContext Context(Wire.AgentExecutionTargetContextWire value)
        => Convert<Domain.AgentExecutionTargetContext>(value.Context) with
        {
            ApprovedResourceCapabilities = value.ApprovedResourceCapabilities,
            CapturePostMutationResource = value.CapturePostMutationResource,
            ExpectedConfigurationGeneration = value.ExpectedConfigurationGeneration,
            ResourceOperation = value.ResourceOperation is { } operation ? new Domain.AgentResourceOperationContext(operation.RunId, operation.RunRevision,
                operation.ToolCallId, operation.ActionId, checked((int)operation.ResourceIndex), operation.WorkspaceGeneration, operation.BindingGeneration,
                operation.ToolOwnerPackageId, operation.ExecutionTargetOwnerPackageId, operation.AuthorityActivationId, checked((int)operation.AuthorityUseCount), operation.CanIssueOutsideAuthority) : null,
        };
    internal static Wire.AgentResolvedResourceWire Resource(Domain.AgentResolvedResource value)
        => new() { Resource = Convert<Wire.AgentResolvedResource>(value), AuthorityReferences = value.AuthorityReferences, DeleteAuthorityReferences = value.DeleteAuthorityReferences };
    internal static Wire.AgentFileMutationResultWire Mutation(Domain.AgentFileMutationResult value)
        => new() { Result = Convert<Wire.AgentFileMutationResult>(value), PostMutationResource = value.PostMutationResource is { } resource ? Resource(resource) : null };
    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new SunderRpcOffsetDateTime(reader.GetString()!).TryGetDateTimeOffset(out var value)
                ? value.ToUniversalTime() : throw new InvalidDataException("The Docker execution timestamp exceeds .NET precision.");
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
