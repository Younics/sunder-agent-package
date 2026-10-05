using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Tests;

// Value projection only. Leases, cancellation tokens, delegates and transient
// authority are handled explicitly by the calling fixture, never JSON-cloned.
internal static class RegressionTestRpcValues
{
    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new SunderRpcJsonConverterFactory(), new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false), new UtcConverter() },
    };

    internal static T Convert<T>(object value) where T : class
        => JsonSerializer.SerializeToElement(value, value.GetType(), Json).Deserialize<T>(Json)
            ?? throw new InvalidDataException($"A test RPC value could not be projected to {typeof(T)}.");

    internal static T? Optional<T>(object? value) where T : class => value is null ? null : Convert<T>(value);

    internal static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Json);

    internal static JsonElement ExecutionContext(AgentExecutionTargetContext value) => JsonSerializer.SerializeToElement(new
    {
        Context = value, value.ApprovedResourceCapabilities, value.CapturePostMutationResource, value.ExpectedConfigurationGeneration,
        ResourceOperation = Operation(value.ResourceOperation),
    }, Json);

    internal static JsonElement ToolInvocation(AgentToolExecutionContext context, AgentToolRequest request,
        AgentRpcProviderHandle? executionTarget = null) => JsonSerializer.SerializeToElement(new
    {
        Context = new
        {
            Context = context,
            ExecutionTarget = executionTarget ?? context.ExecutionTargetReference?.ToHandle(),
            context.ApprovedResourceCapabilities, context.ExecutionTargetConfigurationGeneration,
            ResourceOperation = Operation(context.ResourceOperation),
        },
        Request = request,
    }, Json);

    private static object? Operation(AgentResourceOperationContext? value) => value is null ? null : new
    {
        value.RunId, value.RunRevision, value.ToolCallId, value.ActionId, value.ResourceIndex, value.WorkspaceGeneration,
        value.BindingGeneration, value.ToolOwnerPackageId, value.ExecutionTargetOwnerPackageId, value.AuthorityActivationId,
        value.AuthorityUseCount, value.CanIssueOutsideAuthority,
    };

    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDateTimeOffset();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
