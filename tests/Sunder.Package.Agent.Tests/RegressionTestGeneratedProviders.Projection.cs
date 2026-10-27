using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Tests;

// This is deliberately narrower than AgentRoleBoundary: the candidate explicitly
// admits every model identity per contract, and no same-short-name fallback exists.
internal sealed class RegressionTestGeneratedProviderProjection
{
    private static readonly string[] SourceNamespaces =
    [
        "Sunder.Package.Agent.Contracts.Models",
        "Sunder.Package.Agent.Contracts",
        "Sunder.Package.Agent.Protocol",
        "Sunder.Package.Agent.Runtime",
    ];

    private readonly HashSet<string> _models;
    private readonly IReadOnlyDictionary<string, HashSet<long>> _numericEnums;

    private RegressionTestGeneratedProviderProjection(HashSet<string> models, IReadOnlyDictionary<string, HashSet<long>> numericEnums)
        => (_models, _numericEnums) = (models, numericEnums);

    internal static RegressionTestGeneratedProviderProjection FromCandidate(string candidate, string contractId)
    {
        using var archive = ZipFile.OpenRead(candidate);
        var entry = archive.GetEntry("payload/shared/contracts/rpc-model-projection.v1.json")
            ?? throw new InvalidDataException("The qualified candidate has no RPC model-projection manifest.");
        using var document = JsonDocument.Parse(entry.Open());
        var contract = document.RootElement.GetProperty("contracts").EnumerateArray().Single(value =>
            value.GetProperty("contractId").GetString() == contractId);
        var models = contract.GetProperty("modelProjection").GetProperty("models").EnumerateObject()
            .Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        return new(models, ReadNumericEnums(archive, contractId));
    }

    private static IReadOnlyDictionary<string, HashSet<long>> ReadNumericEnums(ZipArchive archive, string contractId)
    {
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("payload/shared/contracts/", StringComparison.Ordinal)
                     && entry.FullName.EndsWith(".rpc.json", StringComparison.Ordinal)))
        {
            using var descriptor = JsonDocument.Parse(entry.Open());
            if (descriptor.RootElement.GetProperty("contractId").GetString() != contractId) continue;
            return descriptor.RootElement.GetProperty("$defs").EnumerateObject()
                .Where(definition => definition.Value.TryGetProperty("type", out var type) && type.GetString() == "integer"
                    && definition.Value.TryGetProperty("enum", out _))
                .ToDictionary(definition => definition.Name,
                    definition => definition.Value.GetProperty("enum").EnumerateArray().Select(value => value.GetInt64()).ToHashSet(), StringComparer.Ordinal);
        }
        throw new InvalidDataException("The qualified candidate has no matching RPC descriptor.");
    }

    internal async ValueTask<object?> ProjectAsync(object? value, Type target, GeneratedServiceTestScope receiver,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null) return null;
        if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(ServiceReference<>))
            return await receiver.RebindReferenceAsync(value, target.GenericTypeArguments[0], cancellationToken).ConfigureAwait(false);
        if (target.IsInstanceOfType(value)) return value;
        var nullable = Nullable.GetUnderlyingType(target);
        if (nullable is not null) return await ProjectAsync(value, nullable, receiver, cancellationToken).ConfigureAwait(false);
        // Imported JSON integer schemas use Int64; authored domain models also use
        // Int32. Preserve values exactly and reject narrowing overflow.
        if (value is int integer && target == typeof(long)) return (long)integer;
        if (value is long wideInteger && target == typeof(int)) return checked((int)wideInteger);
        if (value is float binary32 && target == typeof(double)) return (double)binary32;
        if (value is double binary64 && target == typeof(float))
        {
            var narrowed = (float)binary64;
            if (!float.IsFinite(narrowed)) throw new OverflowException("Imported number exceeds the authored binary32 range.");
            return narrowed;
        }
        if (target == typeof(DateTimeOffset)) return Offset(value);
        if (target == typeof(SunderRpcOffsetDateTime)) return new SunderRpcOffsetDateTime(Offset(value).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        if (target.IsEnum)
        {
            if (!IsAdmitted(value.GetType()) || !IsAdmitted(target) || value.GetType().Name != target.Name)
                throw new InvalidOperationException($"Enum projection is not admitted for '{value.GetType().FullName}' to '{target.FullName}'.");
            // Numeric wire enums have generated ValueN names, not authored source
            // symbols. Admit only exact values from this authenticated descriptor.
            if (_numericEnums.TryGetValue(target.Name, out var admitted))
            {
                var number = System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                var projected = Enum.ToObject(target, number);
                if (!value.GetType().IsEnum || !Enum.IsDefined(value.GetType(), value)
                    || !admitted.Contains(number) || !Enum.IsDefined(target, projected))
                    throw new InvalidOperationException($"'{value}' is not an admitted {target.FullName} numeric value.");
                return projected;
            }
            var name = value.GetType().IsEnum ? Enum.GetName(value.GetType(), value) : value.ToString();
            if (name is null || !Enum.GetNames(target).Contains(name, StringComparer.Ordinal))
                throw new InvalidOperationException($"'{value}' is not an admitted {target.FullName} symbol.");
            return Enum.Parse(target, name, ignoreCase: false);
        }
        if (target.IsArray || target.IsGenericType && target.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            if (value is not IEnumerable source) throw new InvalidOperationException($"'{value.GetType()}' is not a collection.");
            var element = target.IsArray ? target.GetElementType()! : target.GenericTypeArguments[0];
            var values = source.Cast<object?>().ToArray();
            var array = Array.CreateInstance(element, values.Length);
            for (var index = 0; index < values.Length; index++)
                array.SetValue(await ProjectAsync(values[index], element, receiver, cancellationToken).ConfigureAwait(false), index);
            return array;
        }
        if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
        {
            var types = target.GenericTypeArguments;
            var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(types))!;
            foreach (var item in (IEnumerable)value)
            {
                var itemType = item!.GetType();
                dictionary.Add((await ProjectAsync(itemType.GetProperty("Key")!.GetValue(item), types[0], receiver, cancellationToken).ConfigureAwait(false))!,
                    await ProjectAsync(itemType.GetProperty("Value")!.GetValue(item), types[1], receiver, cancellationToken).ConfigureAwait(false));
            }
            return dictionary;
        }
        target = ResolveUnionCase(value.GetType(), target);
        if (!IsAdmitted(value.GetType()) || !IsAdmitted(target) || value.GetType().Name != target.Name)
            throw new InvalidOperationException($"Model projection is not admitted for '{value.GetType().FullName}' to '{target.FullName}'.");
        return await StructuralAsync(value, target, receiver, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<object> StructuralAsync(object value, Type target, GeneratedServiceTestScope receiver,
        CancellationToken cancellationToken)
    {
        var sourceProperties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(property => property.Name, StringComparer.Ordinal);
        var constructor = target.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .OrderByDescending(candidate => candidate.GetParameters().Length).FirstOrDefault()
            ?? throw new InvalidOperationException($"'{target.FullName}' has no public projection constructor.");
        var parameters = constructor.GetParameters();
        var arguments = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            if (!sourceProperties.TryGetValue(Name(parameter.Name!), out var property))
                throw new InvalidOperationException($"'{value.GetType().FullName}' has no '{parameter.Name}' property for '{target.FullName}'.");
            arguments[index] = await ProjectAsync(property.GetValue(value), parameter.ParameterType, receiver, cancellationToken).ConfigureAwait(false);
        }
        var result = constructor.Invoke(arguments)!;
        var consumed = constructor.GetParameters().Select(parameter => Name(parameter.Name!)).ToHashSet(StringComparer.Ordinal);
        foreach (var property in target.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!sourceProperties.TryGetValue(property.Name, out var source))
            {
                if (TryDiscriminator(value.GetType(), property.Name, out var discriminator))
                {
                    if (property.SetMethod is not null) property.SetValue(result, discriminator);
                    else if (!Equals(property.GetValue(result), discriminator))
                        throw new InvalidOperationException("Generated discriminator differs from the admitted union case.");
                    continue;
                }
                throw new InvalidOperationException($"'{value.GetType().FullName}' dropped '{property.Name}' for '{target.FullName}'.");
            }
            consumed.Add(property.Name);
            if (property.SetMethod is not null)
                property.SetValue(result, await ProjectAsync(source.GetValue(value), property.PropertyType, receiver, cancellationToken).ConfigureAwait(false));
        }
        foreach (var property in sourceProperties.Values.Where(property => !consumed.Contains(property.Name)))
            if (TryDiscriminator(target, property.Name, out var discriminator))
            {
                if (!Equals(property.GetValue(value), discriminator))
                    throw new InvalidOperationException("Received discriminator differs from the admitted union case.");
                consumed.Add(property.Name);
            }
        var unmapped = sourceProperties.Keys.Where(name => !consumed.Contains(name)).ToArray();
        if (unmapped.Length != 0)
            throw new InvalidOperationException($"'{value.GetType().FullName}' has unmapped serialized properties: {string.Join(", ", unmapped)}.");
        return result;
    }

    private bool IsAdmitted(Type type)
        => _models.Contains(type.Name) && (type.Namespace == "Sunder.Generated.Packages.Younics_Ai_Agent_Models"
            // This retained domain result is declared beside its authority port,
            // rather than in Models. Its name must still be in this contract's
            // authenticated candidate model inventory.
            || type.FullName == "Sunder.Package.Agent.Contracts.Contracts.AgentResourceAuthorityValidation"
            || type.FullName == "Sunder.Package.Agent.Models.AgentRunActivityUpdate"
            || SourceNamespaces.Contains(type.Namespace, StringComparer.Ordinal));

    private static bool TryDiscriminator(Type concrete, string propertyName, out object? discriminator)
    {
        for (var parent = concrete.BaseType; parent is not null; parent = parent.BaseType)
        {
            var servicePolicy = parent.GetCustomAttribute<SunderDiscriminatorAttribute>();
            var serviceVariant = concrete.GetCustomAttribute<SunderVariantAttribute>();
            if (servicePolicy is not null && serviceVariant is not null && Name(servicePolicy.PropertyName) == propertyName)
            {
                discriminator = serviceVariant.Tag;
                return true;
            }
            var policy = parent.GetCustomAttribute<JsonPolymorphicAttribute>();
            var variant = parent.GetCustomAttributes<JsonDerivedTypeAttribute>().SingleOrDefault(value => value.DerivedType == concrete);
            if (policy?.TypeDiscriminatorPropertyName is { Length: > 0 } wireName && variant is not null && Name(wireName) == propertyName)
            {
                discriminator = variant.TypeDiscriminator;
                return true;
            }
        }
        discriminator = null;
        return false;
    }

    // Union bases are projected only to their same-schema named concrete case.
    // This does not select a similarly shaped or merely admitted alternative.
    private Type ResolveUnionCase(Type source, Type target)
    {
        if (!target.IsAbstract && !target.IsInterface) return target;
        var matches = target.Assembly.GetTypes().Where(candidate => target.IsAssignableFrom(candidate)
            && !candidate.IsAbstract && candidate.Name == source.Name && IsAdmitted(candidate)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException(
            $"'{source.FullName}' has no unique admitted union case for '{target.FullName}'.");
    }

    private static DateTimeOffset Offset(object value)
        => value switch
        {
            DateTimeOffset offset => offset,
            SunderRpcOffsetDateTime timestamp when timestamp.TryGetDateTimeOffset(out var offset) => offset,
            SunderRpcOffsetDateTime timestamp => throw new InvalidOperationException(
                $"'{timestamp.Value}' cannot be represented as an exact DateTimeOffset."),
            _ => throw new InvalidOperationException($"'{value.GetType().FullName}' is not an exact RPC offset timestamp."),
        };

    private static string Name(string value) => char.ToUpperInvariant(value[0]) + value[1..];
}
