using System.Text.Json;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

// The private relay's error string contains this bounded envelope, not an RPC
// infrastructure exception. Expected request failures must retain their meaning
// when the App reply completes the original Worker-side request.
[SunderPackageCodeRole(SunderPackageCodeRole.Shared)]
internal sealed record BuilderDevelopmentFailure(string Code, string Message, bool OutcomeUnknown = false)
{
    internal string Encode() => JsonSerializer.Serialize(this);

    internal static BuilderDevelopmentFailure Decode(string json)
    {
        if (json.Length > 32 * 1024) throw new InvalidDataException("Maker relay error exceeds its bound.");
        var error = JsonSerializer.Deserialize<BuilderDevelopmentFailure>(json, new JsonSerializerOptions
            { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 2 });
        if (error is null || !KnownCode(error.Code) || error.Message is not { Length: > 0 and <= 4096 })
            throw new InvalidDataException("Maker returned an invalid relay error.");
        return error;
    }

    internal static bool KnownCode(string? code) => code is
        "maker.invalid-request" or "maker.development.unavailable" or "maker.operation-failed" or "maker.busy";
}

[SunderPackageCodeRole(SunderPackageCodeRole.Worker)]
internal sealed class BuilderDevelopmentRequestException(BuilderDevelopmentFailure failure) : Exception(failure.Message)
{
    internal BuilderDevelopmentFailure Failure { get; } = failure;
}
