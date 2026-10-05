using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// Assertion-side adapters only. Real services, calls, cancellation and leases stay
// in the generated role. Collectible proxies implement its actual interfaces;
// no product assembly or exported contract depends on this test bridge.
internal static class AgentRoleBoundary
{
    private static readonly ConditionalWeakTable<object, object> Originals = new();
    private static readonly ConditionalWeakTable<object, ConversionCache> Converted = new();

    // Neither an assertion snapshot nor a long-lived test collaborator may pin a
    // retired role through its converted type/value. Live consumers and event
    // subscriptions own proxies; this cache only preserves their reuse.
    private sealed class ConversionCache
    {
        private readonly List<(WeakReference<Type> Type, WeakReference<object> Value)> _entries = [];

        internal bool TryGetValue(Type target, out object result)
        {
            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                var entry = _entries[index];
                if (!entry.Type.TryGetTarget(out var type) || !entry.Value.TryGetTarget(out var value))
                    _entries.RemoveAt(index);
                else if (type == target)
                {
                    result = value;
                    return true;
                }
            }
            result = null!;
            return false;
        }

        internal void Add(Type target, object value) => _entries.Add((new(target), new(value)));
    }

    internal static object Unwrap(object value) => Originals.TryGetValue(value, out var original) ? original : value;
    internal static T As<T>(object value) => (T)Convert(value, typeof(T))!;

    internal static object[] References(object catalog, object service)
    {
        var method = catalog.GetType().GetMethods().Single(item => item.Name == "GetServiceReferences"
            && item.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == service.GetType().GetGenericTypeDefinition())
            .MakeGenericMethod(service.GetType().GenericTypeArguments);
        return ((IEnumerable)Invoke(() => method.Invoke(catalog, [service]))!).Cast<object>().ToArray();
    }

    internal static object? AssertionValue(object? value)
    {
        if (value is null) return null;
        if (value is Task)
        {
            for (var current = value.GetType(); current is not null; current = current.BaseType)
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Task<>))
                    return Convert(value, typeof(Task<>).MakeGenericType(AssertionType(current.GenericTypeArguments[0])));
            return value;
        }
        var type = AssertionType(value.GetType());
        return type == value.GetType() ? value : Convert(value, type);
    }
    private static Type AssertionType(Type type)
    {
        if (type.IsArray) return AssertionType(type.GetElementType()!).MakeArrayType();
        if (type.IsGenericType) return type.GetGenericTypeDefinition().MakeGenericType(type.GenericTypeArguments.Select(AssertionType).ToArray());
        return type.Assembly.GetName().Name == "Sunder.Package.Agent" && type.Namespace?.StartsWith("Sunder.Package.Agent.Contracts", StringComparison.Ordinal) == true
            ? typeof(Sunder.Package.Agent.Contracts.Models.AgentProfileRecord).Assembly.GetType(type.FullName!) ?? type : type;
    }

    internal static object Create(Type type, params object?[] arguments)
    {
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(item => item.GetParameters().Length == arguments.Length).OrderByDescending(item => item.GetParameters()
                .Select((parameter, index) => arguments[index] is not null && parameter.ParameterType.IsInstanceOfType(arguments[index])).Count(value => value))
            .ThenByDescending(item => item.IsPublic).First();
        return Invoke(() => constructor.Invoke(Arguments(constructor.GetParameters(), arguments)))!;
    }

    internal static object? Call(object value, string name, params object?[] arguments)
    {
        value = Unwrap(value);
        var method = value.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(item => item.Name == name && item.GetParameters().Length == arguments.Length);
        return Invoke(() => method.Invoke(value, Arguments(method.GetParameters(), arguments)));
    }

    internal static object? Convert(object? value, Type target)
    {
        if (value is null || target == typeof(void)) return null;
        value = Unwrap(value);
        if (target.IsInstanceOfType(value)) return value;
        var nullable = Nullable.GetUnderlyingType(target);
        if (nullable is not null) return Convert(value, nullable);
        if (target.IsEnum) return Enum.Parse(target, value.ToString()!);
        if (target == typeof(ValueTask)) return new ValueTask(Await(value));
        if (target.IsGenericType && target.GetGenericTypeDefinition() is var asynchronous &&
            (asynchronous == typeof(Task<>) || asynchronous == typeof(ValueTask<>)))
            return typeof(AgentRoleBoundary).GetMethod(asynchronous == typeof(Task<>) ? nameof(TaskResult) : nameof(ValueTaskResult), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(target.GenericTypeArguments[0]).Invoke(null, [value]);
        if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            return typeof(AgentRoleBoundary).GetMethod(nameof(Stream), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(target.GenericTypeArguments[0]).Invoke(null, [value, CancellationToken.None]);

        var cache = Converted.GetValue(value, static _ => new ConversionCache());
        lock (cache)
        {
            if (cache.TryGetValue(target, out var previous)) return previous;
            object result;
            if (target.FullName == "Sunder.Package.Agent.Protocol.AgentRpcCatalog")
            {
                var client = Field(value.GetType(), "_client").GetValue(value)!;
                result = target.GetMethod("CreateDormant", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [client])!;
                var lifetime = (CancellationTokenSource)Field(value.GetType(), "_lifetime").GetValue(value)!;
                lifetime.Token.Register(((IDisposable)result).Dispose);
                if (Field(value.GetType(), "_watchTask").GetValue(value) is not null)
                    target.GetMethod("StartWatching")!.Invoke(result, null);
            }
            else if (target.IsGenericType && target.GetGenericTypeDefinition().FullName is "Sunder.Package.Agent.Protocol.AgentRpcService`1" or "Sunder.Package.Agent.Protocol.AgentRpcProviderService`1")
            {
                var sourceFactory = value.GetType().GetProperty("ProviderFactory", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value);
                var id = value.GetType().GetProperty("ContractId")!.GetValue(value)!;
                if (sourceFactory is not null)
                {
                    var factory = target.GetMethod("WithProviderIdentity", BindingFlags.Static | BindingFlags.NonPublic)!;
                    result = factory.Invoke(null, [id, Convert(sourceFactory, factory.GetParameters()[1].ParameterType)])!;
                }
                else
                {
                    var factory = value.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Select(field => field.GetValue(value)).OfType<Delegate>().Single();
                    var ctor = target.GetConstructors().Single();
                    result = ctor.Invoke([id, Convert(factory, ctor.GetParameters()[1].ParameterType)]);
                }
            }
            else if (target.IsGenericType && target.GetGenericTypeDefinition().FullName == "Sunder.Package.Agent.Protocol.AgentRpcReference`1")
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var ctor = target.GetConstructors(flags).Single();
                var sourceService = value.GetType().GetField("_service", flags)!.GetValue(value)!;
                var binding = value.GetType().GetField("_clientBinding", flags)!.GetValue(value)!;
                Func<ISunderRpcClient, SunderRpcEndpointReference, object> borrow = (_, _) => binding;
                var serviceType = ctor.GetParameters()[1].ParameterType;
                var serviceConstructor = serviceType.GetConstructors().Single();
                var service = serviceConstructor.Invoke([sourceService.GetType().GetProperty("ContractId")!.GetValue(sourceService),
                    Convert(borrow, serviceConstructor.GetParameters()[1].ParameterType)]);
                result = ctor.Invoke([
                    value.GetType().GetField("_client", flags)!.GetValue(value),
                    service,
                    value.GetType().GetProperty("Provider")!.GetValue(value),
                ]);
                var lifetime = (CancellationTokenSource)value.GetType().GetField("_retirement", flags)!.GetValue(value)!;
                lifetime.Token.Register(() => target.GetMethod("Retire", flags)!.Invoke(result, null));
            }
            else if (typeof(Delegate).IsAssignableFrom(target)) result = AdaptDelegate((Delegate)value, target);
            else if (target.IsArray || target.IsGenericType && target.GenericTypeArguments.Length == 1
                && typeof(IEnumerable).IsAssignableFrom(target))
            {
                var element = target.IsArray ? target.GetElementType()! : target.GenericTypeArguments[0];
                var items = ((IEnumerable)value).Cast<object?>().ToArray();
                var array = Array.CreateInstance(element, items.Length);
                for (var index = 0; index < items.Length; index++) array.SetValue(Convert(items[index], element), index);
                if (target.IsInstanceOfType(array)) result = array;
                else
                {
                    var constructor = target.GetConstructor([typeof(IEnumerable<>).MakeGenericType(element)]);
                    Assert.NotNull(constructor);
                    result = constructor.Invoke([array]);
                }
            }
            else if (target.IsGenericType && target.GenericTypeArguments.Length == 2 && typeof(IEnumerable).IsAssignableFrom(target))
            {
                var types = target.GenericTypeArguments;
                var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(types))!;
                foreach (var pair in (IEnumerable)value)
                {
                    var type = pair!.GetType();
                    dictionary.Add(Convert(type.GetProperty("Key")!.GetValue(pair), types[0])!, Convert(type.GetProperty("Value")!.GetValue(pair), types[1]));
                }
                result = dictionary;
            }
            else if (target.IsInterface)
            {
                // Interfaces are live ports, even when both roles contain a
                // same-named implementation. Reconstructing it would duplicate
                // its state/lifetime instead of borrowing the original service.
                result = CreateProxy(value, target);
            }
            else
            {
                Assert.Equal(value.GetType().FullName, target.FullName);
                var properties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var constructor = target.GetConstructors().OrderByDescending(item => item.GetParameters().Length).First();
                var arguments = constructor.GetParameters().Select(parameter => Convert(properties.Single(property =>
                    string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)).GetValue(value), parameter.ParameterType)).ToArray();
                result = Invoke(() => constructor.Invoke(arguments))!;
                cache.Add(target, result);
                foreach (var property in target.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(property => property.SetMethod is not null))
                {
                    var source = properties.Single(item => item.Name == property.Name);
                    property.SetValue(result, Convert(source.GetValue(value), property.PropertyType));
                }
            }
            if (!cache.TryGetValue(target, out _)) cache.Add(target, result);
            return result;
        }
    }

    private static object?[] Arguments(ParameterInfo[] parameters, object?[] values)
        => parameters.Select((parameter, index) => Convert(values[index], parameter.ParameterType)).ToArray();

    private static FieldInfo Field(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field) return field;
        throw new MissingFieldException(type.FullName, name);
    }

    private static object CreateProxy(object source, Type target)
    {
        var contracts = source.GetType().GetInterfaces().Select(type => target.Assembly.GetType(type.FullName!))
            .Where(type => type?.IsInterface == true).Cast<Type>().Append(target)
            .Concat(source.GetType().GetInterfaces().Where(type => type == typeof(IDisposable) || type == typeof(IAsyncDisposable)))
            .Distinct().ToArray();
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Sunder.Package.Agent.Tests"), AssemblyBuilderAccess.RunAndCollect).DefineDynamicModule("RoleBoundary");
        var builder = module.DefineType("RoleBoundary_" + Guid.NewGuid().ToString("N"), TypeAttributes.Public | TypeAttributes.Sealed);
        foreach (var contract in contracts) builder.AddInterfaceImplementation(contract);
        var callbackType = typeof(Func<int, object?[], object?>);
        var callback = builder.DefineField("_call", callbackType, FieldAttributes.Private | FieldAttributes.InitOnly);
        var constructor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [callbackType]);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, callback); il.Emit(OpCodes.Ret);
        var methods = contracts.SelectMany(type => type.GetInterfaces().Append(type)).Distinct().SelectMany(type => type.GetMethods())
            .Distinct().ToArray();
        for (var index = 0; index < methods.Length; index++)
        {
            var method = methods[index]; var parameters = method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
            Assert.False(method.IsGenericMethod || parameters.Any(type => type.IsByRef));
            var implementation = builder.DefineMethod("Forward_" + index, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
                method.ReturnType, parameters);
            il = implementation.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, callback); il.Emit(OpCodes.Ldc_I4, index);
            il.Emit(OpCodes.Ldc_I4, parameters.Length); il.Emit(OpCodes.Newarr, typeof(object));
            for (var argument = 0; argument < parameters.Length; argument++)
            {
                il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, argument); il.Emit(OpCodes.Ldarg, argument + 1);
                if (parameters[argument].IsValueType) il.Emit(OpCodes.Box, parameters[argument]);
                il.Emit(OpCodes.Stelem_Ref);
            }
            il.Emit(OpCodes.Callvirt, callbackType.GetMethod("Invoke")!);
            if (method.ReturnType == typeof(void)) il.Emit(OpCodes.Pop);
            else il.Emit(method.ReturnType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, method.ReturnType);
            il.Emit(OpCodes.Ret); builder.DefineMethodOverride(implementation, method);
        }
        Func<int, object?[], object?> invoke = (index, arguments) =>
        {
            var destination = methods[index];
            var contract = source.GetType().GetInterfaces().Single(type => type.FullName == destination.DeclaringType!.FullName);
            var implementation = contract.GetMethods().Single(method => method.Name == destination.Name && method.GetParameters().Length == destination.GetParameters().Length);
            try
            {
                var returned = Invoke(() => implementation.Invoke(source, Arguments(implementation.GetParameters(), arguments)));
                // These shared lifetime contracts must expose the owner's exact
                // disposal completion, including repeated DisposeAsync calls.
                if (destination.DeclaringType == typeof(IDisposable) || destination.DeclaringType == typeof(IAsyncDisposable))
                    return returned;
                return AgentRoleExceptionBoundary.Return(returned, destination.ReturnType, target.Assembly);
            }
            catch (Exception error)
            {
                ExceptionDispatchInfo.Capture(AgentRoleExceptionBoundary.Project(error, target.Assembly)).Throw();
                throw;
            }
        };
        var proxy = Activator.CreateInstance(builder.CreateType()!, invoke)!;
        Originals.Add(proxy, source);
        return proxy;
    }

    private static Delegate AdaptDelegate(Delegate source, Type target)
    {
        var invoke = target.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
        Func<object?[], object?> call = arguments => Convert(Invoke(() => source.DynamicInvoke(Arguments(source.GetType().GetMethod("Invoke")!.GetParameters(), arguments))), invoke.ReturnType);
        var body = Expression.Invoke(Expression.Constant(call), Expression.NewArrayInit(typeof(object), parameters.Select(parameter => Expression.Convert(parameter, typeof(object)))));
        return Expression.Lambda(target, invoke.ReturnType == typeof(void) ? Expression.Block(body, Expression.Empty()) : Expression.Convert(body, invoke.ReturnType), parameters).Compile();
    }

    private static object? Invoke(Func<object?> call)
    {
        try { return call(); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    internal static async Task<object?> Await(object pending)
    {
        var task = pending as Task ?? (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }
    private static async Task<T> TaskResult<T>(object pending) => As<T>((await Await(pending))!);
    private static async ValueTask<T> ValueTaskResult<T>(object pending) => As<T>((await Await(pending))!);
    private static async IAsyncEnumerable<T> Stream<T>(object source, [EnumeratorCancellation] CancellationToken token = default)
    {
        var contract = source.GetType().GetInterfaces().Single(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
        var iterator = contract.GetMethod("GetAsyncEnumerator")!.Invoke(source, [token])!;
        await using var lifetime = (IAsyncDisposable)iterator;
        var cursor = typeof(IAsyncEnumerator<>).MakeGenericType(contract.GenericTypeArguments[0]);
        while (await (ValueTask<bool>)cursor.GetMethod("MoveNextAsync")!.Invoke(iterator, null)!)
            yield return As<T>(cursor.GetProperty("Current")!.GetValue(iterator)!);
    }
}
