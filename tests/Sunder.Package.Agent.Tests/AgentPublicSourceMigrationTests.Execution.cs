using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static readonly JsonSerializerOptions ExecutionJson = RegressionTestRpcValues.Json;

    private static async Task VerifyExecutionTargetAsync(string root, AssemblyLoadContext loadContext, Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        const string generatedNamespace = "Sunder.Generated.Rpc.ExecutionTarget.";
        var providerType = assembly.GetType(generatedNamespace + "IExecutionTargetProvider", true)!;
        var probe = new ExecutionTargetProbe();
        var proxy = await BuildGeneratedProvider(root, loadContext, providerType, probe.Invoke);
        var handlerType = assembly.GetType(generatedNamespace + "SunderAgentExecutionTargetRpcHandler", true)!;
        var handler = Assert.IsAssignableFrom<ISunderRpcServiceHandler>(Activator.CreateInstance(handlerType, proxy));
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.builder");
        state.Add("test.execution.backend", descriptor, proxy, handler, EmptyDisposable.Instance, "test.execution.target", "2.0.0");
        var provider = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers);
        await using var scope = await rpc.CreateCallScopeAsync();
        var client = Activator.CreateInstance(assembly.GetType(generatedNamespace + "ExecutionTargetClient", true)!, scope, provider.Endpoint)!;

        var domain = CreateResolver(rpc).Resolution;
        var context = JsonSerializer.SerializeToElement(new
        {
            approvedResourceCapabilities = Array.Empty<string>(),
            capturePostMutationResource = false,
            context = new
            {
                allowOutsideConfiguredScope = false,
                approvedResourceClaims = Array.Empty<object>(),
                approvedResourceReferences = Array.Empty<string>(),
                binding = domain.Binding,
                workspace = domain.Workspace,
            },
        }, ExecutionJson);
        probe.Replies["DescribeAsync"] = """
            {"description":"Test target","displayName":"Generated target","facets":["execution-scope"],"supportsFiles":true,"supportsShell":true,"targetId":"target.one","targetKind":"local"}
            """;
        probe.Replies["ConfigurationGenerationAsync"] = "null";
        probe.Replies["ReadinessAsync"] = """
            {"message":"Configure first","status":"needs-configuration","targetId":"target.one","targetKind":"local"}
            """;
        probe.Replies["ReadFileAsync"] = """
            {"content":"Generated content 漢字","isDirectory":false,"isError":false,"path":"/work/one.txt","wasTruncated":false}
            """;
        var description = await InvokeGeneratedClient(client, "DescribeAsync", JsonSerializer.SerializeToElement(new { }));
        Assert.Equal("Generated target", description.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, (await InvokeGeneratedClient(client, "ConfigurationGenerationAsync", context)).ValueKind);
        var readiness = await InvokeGeneratedClient(client, "ReadinessAsync", context);
        Assert.Equal("needs-configuration", readiness.GetProperty("status").GetString());
        var read = JsonSerializer.SerializeToElement(new { context, request = new { path = "/work/one.txt", offset = 0, limit = 2000 } });
        var result = await InvokeGeneratedClient(client, "ReadFileAsync", read);
        Assert.Equal("Generated content 漢字", result.GetProperty("content").GetString());
        Assert.False(result.TryGetProperty("endLine", out _));
        Assert.False(result.GetProperty("wasTruncated").GetBoolean());
        var calls = probe.Calls;
        var invalid = JsonSerializer.SerializeToElement(new { context, request = new { path = "/work/one.txt", limit = 2001 } });
        var invalidError = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(client, "ReadFileAsync", invalid));
        Assert.Equal(SunderRpcErrorKind.Validation, invalidError.Error.Kind);
        Assert.Equal(calls, probe.Calls);

        probe.FacetFailure = true;
        var process = JsonSerializer.SerializeToElement(new { context, request = new { arguments = Array.Empty<string>(), fileName = "tool" } });
        var facet = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(client, "ExecuteProcessAsync", process));
        Assert.Equal(SunderRpcErrorKind.Domain, facet.Error.Kind);
        Assert.Equal("agent.execution.facet-unavailable", facet.Error.Code);
        var forged = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(client, "ReadFileAsync", read));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, forged.Error.Kind);
        probe.FacetFailure = false;

        using var cancellation = new CancellationTokenSource();
        probe.WaitForCancellation = true;
        var pending = InvokeGeneratedClient(client, "ReadinessAsync", context, cancellation.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<JsonElement> InvokeGeneratedClient(object client, string methodName, JsonElement request,
        CancellationToken cancellationToken = default)
    {
        var method = client.GetType().GetMethod(methodName)!;
        var value = JsonSerializer.Deserialize(request.GetRawText(), method.GetParameters()[0].ParameterType, ExecutionJson);
        object result;
        try { result = method.Invoke(client, [value, cancellationToken])!; }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        var task = (Task)result.GetType().GetMethod("AsTask")!.Invoke(result, null)!;
        await task;
        return JsonSerializer.SerializeToElement(task.GetType().GetProperty("Result")!.GetValue(task), ExecutionJson);
    }

    private static async Task<object> BuildGeneratedProvider(string root, AssemblyLoadContext loadContext, Type providerType,
        Func<string, Type, object?, CancellationToken, object> reply)
    {
        var name = providerType.Namespace!.Split('.')[^1] + "Probe";
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var methods = providerType.GetMethods().Select(method =>
        {
            var parameters = method.GetParameters();
            var resultType = method.ReturnType.GetGenericArguments()[0];
            Assert.Equal(3, parameters.Length);
            return $"public {TypeName(method.ReturnType)} {method.Name}({string.Join(", ", parameters.Select((parameter, index) => TypeName(parameter.ParameterType) + " p" + index))}) => ({TypeName(method.ReturnType)})_reply(\"{method.Name}\", typeof({TypeName(resultType)}), p1, p{parameters.Length - 1});";
        });
        File.WriteAllText(Path.Combine(directory, "Probe.cs"), $$"""
            #nullable disable
            public sealed class GeneratedProviderProbe : {{TypeName(providerType)}}
            {
                private readonly System.Func<string, System.Type, object, System.Threading.CancellationToken, object> _reply;
                public GeneratedProviderProbe(System.Func<string, System.Type, object, System.Threading.CancellationToken, object> reply) => _reply = reply;
                {{string.Join(Environment.NewLine, methods)}}
            }
            """);
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup>
                <Reference Include="Sunder.Package.Agent" HintPath="{{SecurityElement.Escape(providerType.Assembly.Location)}}" />
                <Reference Include="Sunder.Sdk" HintPath="{{SecurityElement.Escape(typeof(SunderRpcServiceAttribute).Assembly.Location)}}" />
              </ItemGroup>
            </Project>
            """);
        await BuilderSourceMigrationTests.Run(directory, "restore", name + ".csproj", "--disable-parallel");
        await BuilderSourceMigrationTests.Run(directory, "build", name + ".csproj", "--no-restore");
        using var stream = File.OpenRead(Path.Combine(directory, "bin/Debug/net10.0/" + name + ".dll"));
        var implementation = loadContext.LoadFromStream(stream).GetType("GeneratedProviderProbe", true)!;
        return Activator.CreateInstance(implementation, reply)!;
    }

    private static string TypeName(Type type)
        => type.IsGenericType
            ? "global::" + type.GetGenericTypeDefinition().FullName!.Split('`')[0] + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">"
            : "global::" + type.FullName!.Replace('+', '.');

    // Test-only behavior behind a concrete implementation of the actual generated
    // interface. Both assemblies stay collectible; no owner registration is added.
    private sealed class ExecutionTargetProbe
    {
        public Dictionary<string, string> Replies { get; } = new(StringComparer.Ordinal);
        public int Calls { get; private set; }
        public bool FacetFailure { get; set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public object Invoke(string method, Type resultType, object? request, CancellationToken token)
            => typeof(ExecutionTargetProbe).GetMethod(nameof(Reply), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(resultType).Invoke(this, [method, token])!;

        private async ValueTask<T> Reply<T>(string method, CancellationToken token)
        {
            Calls++;
            if (WaitForCancellation)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            if (FacetFailure)
                throw new SunderRpcException(new SunderRpcError(SunderRpcErrorKind.Domain,
                    "agent.execution.facet-unavailable", "Optional facet is unavailable."));
            return JsonSerializer.Deserialize<T>(Replies[method], ExecutionJson)!;
        }
    }
}
