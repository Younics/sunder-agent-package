using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task<WebBackend> BuildWebBackend(string root, string worker, AssemblyLoadContext load, WebHttpPeer peer)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "WebBackend")).FullName;
        var repository = BuilderSourceMigrationTests.FindRepository();
        File.Copy(Path.Combine(repository, "tests/Sunder.Package.Agent.Tests/Fixtures/WebArtifactBackend.cs.txt"), Path.Combine(directory, "Backend.cs"));
        var references = Directory.GetFiles(SingleProjectRoleOutput(worker, "worker"), "*.dll");
        WriteControlledWorkerEntry(worker, directory);
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("AssemblyName", "Sunder.Package.Agent.Tests"),
                new XElement("Nullable", "enable"), new XElement("ImplicitUsings", "enable"), new XElement("LangVersion", "14"), new XElement("TreatWarningsAsErrors", "true")),
            new XElement("ItemGroup", references.Select(path => new XElement("Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(path)), new XAttribute("HintPath", path))))))
            .Save(Path.Combine(directory, "Backend.csproj"));
        await Run(directory, "restore", "Backend.csproj", "--disable-parallel");
        await Run(directory, "build", "Backend.csproj", "--no-restore");
        using var bytes = File.OpenRead(Path.Combine(directory, "bin/Debug/net10.0/Sunder.Package.Agent.Tests.dll"));
        var assembly = load.LoadFromStream(bytes);
        var type = assembly.GetType("WebArtifactBackend", true)!;
        var backend = Activator.CreateInstance(type)!;
        type.GetProperty("Resolve")!.SetValue(backend, (Func<string, CancellationToken, Task<IPAddress[]>>)peer.Resolve);
        type.GetProperty("Fetch")!.SetValue(backend, (Func<Uri, IReadOnlyList<IPAddress>, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)peer.Fetch);
        type.GetProperty("Search")!.SetValue(backend, (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)peer.Search);
        return new(backend, type);
    }

    private sealed class WebBackend(object value, Type type) : IAsyncDisposable
    {
        internal SunderWorkerOptions Create(IPackageContext context, ISunderRpcClient rpc) => (SunderWorkerOptions)type.GetMethod("Create")!.Invoke(value, [context, rpc])!;
        internal SunderWorkerOptions CreateGenerated(IPackageContext context, ISunderRpcClient rpc)
            => (SunderWorkerOptions)type.GetMethod("CreateGenerated")!.Invoke(value, [context, rpc])!;
        internal int FetchClients => (int)type.GetField("FetchClients")!.GetValue(value)!;
        internal int DisposedFetchClients => (int)type.GetField("DisposedFetchClients")!.GetValue(value)!;
        internal int DisposedSearchClients => (int)type.GetField("DisposedSearchClients")!.GetValue(value)!;
        public ValueTask DisposeAsync() => ((IAsyncDisposable)value).DisposeAsync();
    }
}
