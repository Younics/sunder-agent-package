using System.Globalization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

public sealed record Unit;

[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
public readonly record struct Identifier
{
    public required string Value { get; init; }
    public static implicit operator Identifier(string value) => new() { Value = value };
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
public readonly record struct VersionText
{
    public required string Value { get; init; }
    public static implicit operator VersionText(string value) => new() { Value = value };
}

[SunderRpcTransparent]
public readonly record struct TextValue
{
    public required string Value { get; init; }
    public static implicit operator TextValue(string value) => new() { Value = value };
}

[SunderRpcTransparent]
public readonly record struct DisplayText
{
    public required string Value { get; init; }
    public static implicit operator DisplayText(string value) => new() { Value = value };
    public static SunderRpcOptional<DisplayText> Optional(string? value)
        => value is null ? default(SunderRpcOptional<DisplayText>) : new DisplayText { Value = value };
}

[SunderRpcTransparent]
public readonly record struct PathText
{
    public required string Value { get; init; }
    public static implicit operator PathText(string value) => new() { Value = value };
    public static SunderRpcOptional<PathText> Optional(string? value)
        => value is null ? default(SunderRpcOptional<PathText>) : new PathText { Value = value };
}

[SunderRpcTransparent]
public readonly record struct Flag
{
    public required bool Value { get; init; }
    public static implicit operator Flag(bool value) => new() { Value = value };
}

[SunderRpcTransparent]
public readonly record struct SortOrder
{
    public required long Value { get; init; }
    public static implicit operator SortOrder(long value) => new() { Value = value };
}

[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")]
public readonly record struct Revision
{
    public required long Value { get; init; }
    public static implicit operator Revision(long value) => new() { Value = value };
}

[SunderRpcTransparent]
public readonly record struct EntityId
{
    public required Guid Value { get; init; }
    public static implicit operator EntityId(Guid value) => new() { Value = value };
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 64, MaxLength = 64)]
public readonly record struct Sha256Text
{
    public required string Value { get; init; }
    public static implicit operator Sha256Text(string value) => new() { Value = value };
}

[SunderRpcTransparent]
public readonly record struct Timestamp
{
    public required SunderRpcOffsetDateTime Value { get; init; }
    public static Timestamp FromDomain(DateTimeOffset value)
        => new() { Value = new(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture)) };
    public DateTimeOffset ToDomain()
        => Value.TryGetDateTimeOffset(out var value) ? value : throw new SunderRpcException(new(
            SunderRpcErrorKind.Protocol, "agent.rpc.timestamp-not-representable",
            "The exact RPC date-time cannot be represented by DateTimeOffset."));
}

[SunderRpcTransparent]
public sealed class TextValues
{
    public required IReadOnlyList<TextValue> Value { get; init; }
    public static TextValues FromDomain(IReadOnlyList<string> values)
        => new() { Value = values.Select(value => (TextValue)value).ToArray() };
}

public sealed class AgentRpcIdentity
{
    public required DisplayText DisplayName { get; init; }
    public required Identifier Id { get; init; }
}
