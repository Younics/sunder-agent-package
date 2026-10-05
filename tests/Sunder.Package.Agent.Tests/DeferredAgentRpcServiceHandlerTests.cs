using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class DeferredAgentRpcServiceHandlerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly SunderRpcContractDescriptor TestDescriptor =
        RegressionTestRpcDescriptors.Get(AgentRpcContractIds.SessionCleaner);
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> TestDeclaredErrors =
        RegressionTestRpcDescriptors.ErrorMap(TestDescriptor);

    [Fact]
    public async Task ActivationBridge_CompilesWithOnlySdkAndWorkerContracts()
    {
        var root = Directory.CreateTempSubdirectory("sunder-deferred-sdk-boundary-").FullName;
        var context = new AssemblyLoadContext("deferred-sdk-boundary", isCollectible: true);
        try
        {
            var repository = BuilderSourceMigrationTests.FindRepository();
            File.Copy(Path.Combine(repository, "src/Sunder.Package.Agent/Runtime/DeferredAgentRpcServiceHandler.cs"), Path.Combine(root, "Bridge.cs"));
            var references = new[] { typeof(SunderRpcContractDescriptor).Assembly.Location, typeof(SunderWorkerProviderRegistration).Assembly.Location };
            File.WriteAllText(Path.Combine(root, "SdkBoundary.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><LangVersion>14</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
                  <ItemGroup>{{string.Join("", references.Select(path => $"<Reference Include=\"{SecurityElement.Escape(Path.GetFileNameWithoutExtension(path))}\" HintPath=\"{SecurityElement.Escape(path)}\" />"))}}</ItemGroup>
                </Project>
                """);
            await BuilderSourceMigrationTests.Run(root, "restore", "SdkBoundary.csproj", "--disable-parallel");
            await BuilderSourceMigrationTests.Run(root, "build", "SdkBoundary.csproj", "--no-restore");
            using var bytes = File.OpenRead(Path.Combine(root, "bin/Debug/net10.0/SdkBoundary.dll"));
            var assembly = context.LoadFromStream(bytes);
            Assert.All(assembly.GetReferencedAssemblies(), reference => Assert.True(reference.Name!.StartsWith("System.", StringComparison.Ordinal)
                || reference.Name is "Sunder.Sdk" or "Sunder.Sdk.Worker", reference.FullName));
        }
        finally { context.Unload(); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DeclaredErrors_AreAvailableBeforeActivationAndMismatchedHandlerIsRejected()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var deferred = new DeferredAgentRpcServiceHandler(
            "test.service",
            TestDescriptor,
            static _ => new UndeclaredHandler());

        Assert.Equal(
            FlattenDeclaredErrors(TestDeclaredErrors),
            FlattenDeclaredErrors(deferred.DeclaredErrors));
        var exception = Assert.Throws<InvalidOperationException>(() => deferred.Activate(services));
        Assert.Contains("does not implement the declared domain errors", exception.Message, StringComparison.Ordinal);
        Assert.False(deferred.IsActive);
    }

    private static IReadOnlyList<string> FlattenDeclaredErrors(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> declaredErrors)
        => declaredErrors
            .SelectMany(static service => service.Value.Select(method =>
                $"{service.Key}/{method.Key}:{string.Join(',', method.Value)}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Theory]
    [InlineData("provider")]
    [InlineData("contract")]
    [InlineData("version")]
    [InlineData("descriptor")]
    [InlineData("abi")]
    [InlineData("errors")]
    public void GeneratedDeclaration_RejectsMismatchedActivationIdentity(string mutation)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var calls = 0;
        var deferred = new DeferredAgentRpcServiceHandler(
            handler => Registration("test.service", TestDescriptor.ContractId, TestDescriptor.Version, TestDescriptor.Sha256, TestDescriptor.AbiSha256!, new DeclarationHandler(handler)),
            _ =>
            {
                calls++;
                return Registration(mutation == "provider" ? "test.other" : "test.service",
                    mutation == "contract" ? "test.other" : TestDescriptor.ContractId,
                    mutation == "version" ? "9.0.0" : TestDescriptor.Version,
                    mutation == "descriptor" ? new string('f', 64) : TestDescriptor.Sha256,
                    mutation == "abi" ? new string('f', 64) : TestDescriptor.AbiSha256!,
                    mutation == "errors" ? new UndeclaredHandler() : new EchoHandler());
            });
        Assert.Equal(0, calls);
        Assert.Equal(FlattenDeclaredErrors(TestDeclaredErrors), FlattenDeclaredErrors(deferred.Registration.DeclaredErrors));
        Assert.Contains("does not match its generated declaration", Assert.Throws<InvalidOperationException>(() => deferred.Activate(services)).Message, StringComparison.Ordinal);
        Assert.Equal(1, calls);
        Assert.False(deferred.IsActive);
        deferred.Deactivate();

        static SunderWorkerProviderRegistration Registration(string provider, string contract, string version, string descriptor, string abi, ISunderRpcServiceHandler handler)
            => new(provider, contract, version, descriptor, abi, handler);
    }

    private sealed class DeclarationHandler(ISunderRpcServiceHandler inner) : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors => TestDeclaredErrors;
        public ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
            => inner.InvokeUnaryAsync(context, serviceId, methodId, request, token);
        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
            => inner.InvokeServerStreamAsync(context, serviceId, methodId, request, token);
    }

    private sealed class UndeclaredHandler : ISunderRpcServiceHandler
    {
        public ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
            => throw new NotSupportedException("A metadata-mismatched handler must never be activated.");
        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
            => throw new NotSupportedException("A metadata-mismatched handler must never be activated.");
    }

    [Fact]
    public async Task InvokeUnaryAsync_WaitsForProviderActivation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var deferred = new DeferredAgentRpcServiceHandler(
            "test.service",
            TestDescriptor,
            static _ => new EchoHandler());
        var request = JsonSerializer.SerializeToElement(new { value = "ready" });

        var invocation = deferred.InvokeUnaryAsync(
            null!,
            "test.service",
            "echo",
            request,
            CancellationToken.None).AsTask();
        await Task.Yield();
        Assert.False(invocation.IsCompleted);

        deferred.Activate(services);

        Assert.Equal(request, await invocation.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Deactivate_CancelsPendingInvocation()
    {
        var deferred = new DeferredAgentRpcServiceHandler(
            "test.service",
            TestDescriptor,
            static _ => new EchoHandler());
        var invocation = deferred.InvokeUnaryAsync(
            null!,
            "test.service",
            "echo",
            default,
            CancellationToken.None).AsTask();

        deferred.Deactivate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invocation.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Deactivate_AfterActivationPreventsUnaryDispatchThroughCompletedActivation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new RetirementAwareHandler();
        var deferred = new DeferredAgentRpcServiceHandler("test.service", TestDescriptor, _ => handler);
        deferred.Activate(services);

        deferred.Deactivate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deferred.InvokeUnaryAsync(
            null!,
            "test.service",
            "blocked",
            default).AsTask());
        Assert.Equal(0, handler.UnaryInvocations);
    }

    [Fact]
    public async Task Deactivate_AfterActivationPreventsStreamDispatchThroughCompletedActivation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new RetirementAwareHandler();
        var deferred = new DeferredAgentRpcServiceHandler("test.service", TestDescriptor, _ => handler);
        deferred.Activate(services);

        deferred.Deactivate();

        await using var stream = deferred.InvokeServerStreamAsync(
                null!,
                "test.service",
                "blocked",
                default)
            .GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.MoveNextAsync().AsTask());
        Assert.Equal(0, handler.StreamInvocations);
    }

    [Fact]
    public async Task Deactivate_CancelsActiveUnaryInvocation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new RetirementAwareHandler();
        var deferred = new DeferredAgentRpcServiceHandler("test.service", TestDescriptor, _ => handler);
        deferred.Activate(services);
        var invocation = deferred.InvokeUnaryAsync(
            null!,
            "test.service",
            "blocked",
            default).AsTask();
        await handler.UnaryStarted.Task.WaitAsync(TestTimeout);

        deferred.Deactivate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invocation.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Deactivate_CancelsActiveStreamInvocation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new RetirementAwareHandler();
        var deferred = new DeferredAgentRpcServiceHandler("test.service", TestDescriptor, _ => handler);
        deferred.Activate(services);
        await using var stream = deferred.InvokeServerStreamAsync(
                null!,
                "test.service",
                "blocked",
                default)
            .GetAsyncEnumerator();
        var moveNext = stream.MoveNextAsync().AsTask();
        await handler.StreamStarted.Task.WaitAsync(TestTimeout);

        deferred.Deactivate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => moveNext.WaitAsync(TestTimeout));
    }

    private sealed class EchoHandler : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors
            => TestDeclaredErrors;

        public ValueTask<JsonElement> InvokeUnaryAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(request);
        }

        public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return request;
            await Task.CompletedTask;
        }
    }

    private sealed class RetirementAwareHandler : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        private int _streamInvocations;
        private int _unaryInvocations;

        internal int StreamInvocations => Volatile.Read(ref _streamInvocations);

        internal int UnaryInvocations => Volatile.Read(ref _unaryInvocations);

        internal TaskCompletionSource StreamStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource UnaryStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors
            => TestDeclaredErrors;

        public async ValueTask<JsonElement> InvokeUnaryAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _unaryInvocations);
            UnaryStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return request;
        }

        public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _streamInvocations);
            StreamStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield return request;
        }
    }
}
