extern alias DockerApp;

using System.Text.Json;
using System.Text.Json.Serialization;
using DockerResponse = DockerApp::Sunder.Package.Agent.Execution.Docker.DockerExecutionOperationResponse;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Execution.Local.Tests;

internal static class DockerPresentationTestContent
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    internal static SunderContent Create(DockerResponse response)
        => SunderContent.FromBytes(
            JsonSerializer.SerializeToUtf8Bytes(response, Json),
            "application/json",
            "docker-catalog.json");
}
