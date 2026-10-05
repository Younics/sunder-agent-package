using System.Reflection;
using System.Runtime.Loader;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class CredentialSourceMigrationTests
{
    private static readonly string[] Families = ["Gemini", "Anthropic", "LMStudio"];
    internal static readonly string[] SourcePaths = Families.SelectMany(family => new[]
    {
        $"src/Sunder.Package.Agent.Provider.{family}/Rpc/CredentialSettings.cs",
        $"src/Sunder.Package.Agent.Provider.{family}/Rpc/CredentialClient.cs",
    }).ToArray();

    [AgentPublicSourceMigrationFact]
    public async Task CredentialSource_PreservesFrozenContractAndRoleIsolatedSecretBehavior()
    {
        foreach (var family in Families) await VerifyFamily(family);
    }

    private static async Task VerifyFamily(string family)
    {
        var packageId = "sunder.package.agent.provider." + family.ToLowerInvariant();
        var repository = BuilderSourceMigrationTests.FindRepository();
        var core = Path.GetFullPath(Path.Combine(repository, "..", "sunder-core"));
        var task = Path.Combine(core, "languages/csharp/dotnet/packages/Sunder.Package.Build/bin",
            new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0/Sunder.Package.Build.dll");
        Assert.True(File.Exists(task));
        var root = Directory.CreateTempSubdirectory("sunder-gemini-credential-source-").FullName;
        var context = new AssemblyLoadContext("gemini-credential-source", isCollectible: true);
        try
        {
            File.Copy(Path.Combine(repository, $"src/Sunder.Package.Agent.Provider.{family}/Rpc/CredentialSettings.cs"), Path.Combine(root, "CredentialSettings.cs"));
            File.Copy(Path.Combine(repository, "src/Sunder.Package.Agent.Provider.Shared/ProviderCredentialAccessor.cs"), Path.Combine(root, "ProviderCredentialAccessor.cs"));
            File.Copy(Path.Combine(core, "specs/rpc/v1/authoring/conformance/valid/minimal-contract-lock.json"), Path.Combine(root, "sunder-contracts.lock.json"));
            var references = new[] { typeof(SunderRpcServiceAttribute).Assembly.Location, typeof(SunderWorker).Assembly.Location };
            File.WriteAllText(Path.Combine(root, "CredentialSource.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><LangVersion>14</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
                  <ItemGroup><Compile Remove="App/**" />{{string.Join("", references.Select(path => $"<Reference Include=\"{SecurityElement.Escape(Path.GetFileNameWithoutExtension(path))}\" HintPath=\"{SecurityElement.Escape(path)}\" />"))}}</ItemGroup>
                  <UsingTask TaskName="Sunder.Package.Build.Tasks.GenerateSunderRpcBindingsTask" AssemblyFile="{{SecurityElement.Escape(task)}}" />
                  <Target Name="GenerateCandidateRpc" BeforeTargets="CoreCompile" DependsOnTargets="ResolveReferences">
                    <ItemGroup><_Source Include="@(Compile)" Condition="'%(Compile.AutoGen)' != 'true'" /></ItemGroup>
                    <GenerateSunderRpcBindingsTask ProjectDirectory="$(MSBuildProjectDirectory)" IntermediateOutputPath="$(IntermediateOutputPath)"
                        PackageId="{{packageId}}" PackageVersion="2.0.0" ProjectIdentity="CredentialSource.csproj" Configuration="$(Configuration)" TargetFramework="$(TargetFramework)"
                        SourceFiles="@(_Source)" ReferencePaths="@(ReferencePath)" ContractOperation="$(ContractOperation)" NullableEnabled="true">
                      <Output TaskParameter="CompileItems" ItemName="Compile" />
                    </GenerateSunderRpcBindingsTask>
                  </Target>
                </Project>
                """);
            await BuilderSourceMigrationTests.Run(root, "restore", "CredentialSource.csproj", "--disable-parallel");
            await BuilderSourceMigrationTests.Run(root, "msbuild", "CredentialSource.csproj", "-t:GenerateCandidateRpc", "-p:ContractOperation=Accept");
            await BuilderSourceMigrationTests.Run(root, "msbuild", "CredentialSource.csproj", "-t:GenerateCandidateRpc", "-p:ContractOperation=Freeze");
            var baseline = File.ReadAllBytes(Path.Combine(root, "sunder-contracts.owned.json"));
            await BuilderSourceMigrationTests.Run(root, "build", "CredentialSource.csproj", "--no-restore");
            await BuilderSourceMigrationTests.Run(root, "msbuild", "CredentialSource.csproj", "-t:GenerateCandidateRpc", "-p:ContractOperation=Check");
            Assert.Equal(baseline, File.ReadAllBytes(Path.Combine(root, "sunder-contracts.owned.json")));
            var current = Assert.Single(JsonNode.Parse(baseline)!["contracts"]!.AsObject()).Value!["current"]!;
            var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(current["canonicalDescriptor"]!.GetValue<string>()));
            using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "compatibility/agent-rpc-migration-baseline.json")));
            var original = Assert.Single(oracle.RootElement.GetProperty("descriptors").EnumerateArray(), value => value.GetProperty("contractId").GetString() == descriptor.ContractId);
            Assert.Equal(original.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
            Assert.Equal(original.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
            Assert.Equal(Convert.FromBase64String(original.GetProperty("canonicalDescriptorBase64").GetString()!), descriptor.GetCanonicalUtf8());
            using var workerStream = File.OpenRead(Path.Combine(root, "bin/Debug/net10.0/CredentialSource.dll"));
            var worker = context.LoadFromStream(workerStream);
            Assert.DoesNotContain(worker.GetReferencedAssemblies(), assembly => assembly.Name!.Contains("Protocol", StringComparison.Ordinal));
            var app = await BuildApp(repository, root, task, context, family, packageId);
            await VerifyBehavior(worker, app, descriptor, family, packageId);
        }
        finally { context.Unload(); Directory.Delete(root, recursive: true); }
    }

    private static async Task<Assembly> BuildApp(string repository, string root, string task, AssemblyLoadContext context, string family, string packageId)
    {
        var directory = Path.Combine(root, "App");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(repository, $"src/Sunder.Package.Agent.Provider.{family}/Rpc/CredentialClient.cs"), Path.Combine(directory, "CredentialClient.cs"));
        File.WriteAllText(Path.Combine(directory, "Probe.cs"), $$"""
            using Sunder.Sdk.Rpc;
            using Sunder.Package.Agent.Provider.{{family}};
            public sealed class CredentialAppProbe(ISunderRpcClient rpc)
            {
                private readonly ProviderCredentialOwnerRpcClient _client = new(rpc);
                public async Task<bool> Query(CancellationToken token) => (await _client.GetStatusAsync(token)).HasStoredCredential;
                public async Task<bool> Set(string value, CancellationToken token) => (await _client.SetAsync(value, token)).HasStoredCredential;
                public async Task<bool> Clear(CancellationToken token) => (await _client.ClearAsync(token)).HasStoredCredential;
            }
            """);
        var exportPath = Path.Combine(root, "obj/Debug/net10.0/sunder-rpc/package-export.v1.json");
        var exported = File.ReadAllBytes(exportPath);
        var sdk = typeof(SunderRpcServiceAttribute).Assembly.Location;
        File.WriteAllText(Path.Combine(directory, "CredentialApp.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><LangVersion>14</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
              <ItemGroup><Reference Include="Sunder.Sdk" HintPath="{{SecurityElement.Escape(sdk)}}" /></ItemGroup>
              <ItemGroup><_Export Include="{{SecurityElement.Escape(exportPath)}}" Sha256="{{Convert.ToHexStringLower(SHA256.HashData(exported))}}"
                ProjectIdentity="CredentialSource.csproj" Configuration="Debug" TargetFramework="net10.0" PackageId="{{packageId}}" PackageVersion="2.0.0" /></ItemGroup>
              <UsingTask TaskName="Sunder.Package.Build.Tasks.GenerateSunderRpcBindingsTask" AssemblyFile="{{SecurityElement.Escape(task)}}" />
              <Target Name="GenerateAppRpc" BeforeTargets="CoreCompile" DependsOnTargets="ResolveReferences">
                <GenerateSunderRpcBindingsTask ProjectDirectory="$(MSBuildProjectDirectory)" IntermediateOutputPath="$(IntermediateOutputPath)"
                    PackageId="{{packageId}}" PackageVersion="2.0.0" ProjectIdentity="CredentialApp.csproj" Configuration="$(Configuration)" TargetFramework="$(TargetFramework)"
                    GenerationMode="ProjectImport" ExpectedPackageProjectExports="@(_Export)">
                  <Output TaskParameter="CompileItems" ItemName="Compile" />
                </GenerateSunderRpcBindingsTask>
              </Target>
            </Project>
            """);
        await BuilderSourceMigrationTests.Run(directory, "restore", "CredentialApp.csproj", "--disable-parallel");
        await BuilderSourceMigrationTests.Run(directory, "build", "CredentialApp.csproj", "--no-restore");
        Assert.Equal(exported, File.ReadAllBytes(exportPath));
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "obj/Debug/net10.0/sunder-rpc/authoring-receipt.v1.json")));
        var use = Assert.Single(receipt.RootElement.GetProperty("uses").EnumerateArray());
        Assert.True(use.GetProperty("required").GetBoolean());
        Assert.Equal("app", use.GetProperty("role").GetString());
        Assert.Empty(receipt.RootElement.GetProperty("providers").EnumerateArray());
        using var stream = File.OpenRead(Path.Combine(directory, "bin/Debug/net10.0/CredentialApp.dll"));
        var assembly = context.LoadFromStream(stream);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), value => value.Name!.Contains("Protocol", StringComparison.Ordinal)
            || value.Name is "CredentialSource" or "Sunder.Sdk.Worker");
        return assembly;
    }

    private static async Task VerifyBehavior(Assembly worker, Assembly app, SunderRpcContractDescriptor descriptor, string family, string packageId)
    {
        var secrets = new Secrets();
        var secretKey = family == "LMStudio" ? "connection.apiKey" : "auth.apiKey";
        var accessor = Activator.CreateInstance(worker.GetType("Sunder.Package.Agent.Provider.Shared.ProviderCredentialAccessor", true)!, secrets, secretKey)!;
        var type = worker.GetType($"Sunder.Package.Agent.Provider.{family}.Rpc.CredentialSettingsService", true)!;
        var instance = Activator.CreateInstance(type, accessor)!;
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(instance, null));
        Assert.Equal(family.ToLowerInvariant() + ".credentials", registration.ProviderId);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, packageId);
        state.Add(packageId, descriptor, instance, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        var endpoint = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers).Endpoint;
        var clientType = app.GetType("CredentialAppProbe", true)!;
        var client = Activator.CreateInstance(clientType, rpc)!;
        var query = clientType.GetMethod("Query")!.CreateDelegate<Func<CancellationToken, Task<bool>>>(client);
        var set = clientType.GetMethod("Set")!.CreateDelegate<Func<string, CancellationToken, Task<bool>>>(client);
        var clear = clientType.GetMethod("Clear")!.CreateDelegate<Func<CancellationToken, Task<bool>>>(client);
        Assert.False(await query(default));
        Assert.True(await set("  isolated-credential-漢字  ", default));
        Assert.Equal("isolated-credential-漢字", secrets.Value);
        Assert.True(await query(default));
        var status = await rpc.InvokeAsync(endpoint, "credential-settings", "query", JsonSerializer.SerializeToElement(new { }));
        Assert.DoesNotContain(secrets.Value!, status.GetRawText(), StringComparison.Ordinal);
        foreach (var (request, code) in new[]
        {
            ("{\"kind\":\"set\"}", "provider.credential.required"),
            ("{\"kind\":\"set\",\"credential\":\"   \"}", "provider.credential.required"),
            ("{\"kind\":\"clear\",\"credential\":\"unexpected\"}", "provider.credential.unexpected"),
        })
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => rpc.InvokeAsync(endpoint, "credential-settings", "command", JsonDocument.Parse(request).RootElement).AsTask());
            Assert.Equal(code, error.Error.Code);
            Assert.Equal("isolated-credential-漢字", secrets.Value);
        }
        var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => rpc.InvokeAsync(endpoint, "credential-settings", "command",
            JsonSerializer.SerializeToElement(new { kind = "unknown" })).AsTask());
        Assert.Equal(SunderRpcErrorKind.Validation, invalid.Error.Kind);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => set("must-not-store", cancelled.Token));
            Assert.Equal("isolated-credential-漢字", secrets.Value);
        }
        Assert.False(await clear(default));
        Assert.Null(secrets.Value);
        Assert.False(await clear(default));
        secrets.Fail = true;
        var failed = await Assert.ThrowsAsync<SunderRpcException>(() => query(default));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failed.Error.Kind);
        Assert.DoesNotContain("private secret storage", failed.Message, StringComparison.Ordinal);
        secrets.Fail = false;
        await state.RetireAsync(instance);
        var absent = await Assert.ThrowsAsync<SunderRpcException>(() => query(default));
        Assert.Equal("provider.credential.unavailable", absent.Error.Code);
        Assert.All(secrets.Keys, key => Assert.Equal(secretKey, key));
    }

    private sealed class Secrets : IPackageSecrets
    {
        internal string? Value { get; private set; }
        internal bool Fail { get; set; }
        internal List<string> Keys { get; } = [];
        private void Check(string key, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Keys.Add(key);
            if (Fail) throw new IOException("private secret storage failure");
        }
        public Task<string?> GetSecretAsync(string key, CancellationToken token = default) { Check(key, token); return Task.FromResult(Value); }
        public Task SetSecretAsync(string key, string value, CancellationToken token = default) { Check(key, token); Value = value; return Task.CompletedTask; }
        public Task DeleteSecretAsync(string key, CancellationToken token = default) { Check(key, token); Value = null; return Task.CompletedTask; }
    }
    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
