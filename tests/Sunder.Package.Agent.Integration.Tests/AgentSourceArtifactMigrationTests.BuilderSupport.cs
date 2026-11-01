using System.Reflection;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderSourceMigrationTests
{
    private static readonly JsonSerializerOptions BuilderJson = new(JsonSerializerDefaults.Web)
    { Converters = { new SunderRpcJsonConverterFactory() } };

    private static async Task<object?> Invoke(object service, string method, params object?[] args)
        => await AgentRoleBoundary.Await(AgentRoleBoundary.Call(service, method, args)!);

    private static JsonElement Value(object? value) => JsonSerializer.SerializeToElement(value, value!.GetType(), BuilderJson);

    private static async Task<JsonElement> WaitForJob(object gateway, string id, string backendId, Func<JsonElement, bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var result = Value(await Invoke(gateway, "GetJobAsync", id, backendId, deadline.Token));
            Assert.Equal(backendId, result.GetProperty("backendId").GetString());
            if (ready(result)) return result;
            Assert.Equal("running", result.GetProperty("state").GetString());
            await Task.Delay(10, deadline.Token);
        }
    }

    private static void AssertResponseBounds(Assembly assembly)
    {
        var create = assembly.GetType("Sunder.Package.Agent.Builder.BuilderContent", true)!
            .GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(string));
        var failure = Assert.Throws<TargetInvocationException>(() => create.Invoke(null, [new string('x', 8 * 1024 * 1024)]));
        Assert.Equal("agent.builder.response-too-large", Assert.IsType<SunderRpcException>(failure.InnerException).Error.Code);
    }
}
