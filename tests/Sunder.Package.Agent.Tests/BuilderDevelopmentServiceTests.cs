extern alias BuilderApp;

using System.Text.Json;
using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderDevelopmentServiceTests
{
    [Fact]
    public async Task AcceptedBuildHasActivationOwnershipNotRpcContextAndDuplicateAdmissionOnlyObserves()
    {
        await using var fixture = new BuilderUiFixture();
        var project = Project(fixture, "owned-admission");
        await using var service = new BuilderDevelopmentService(fixture.PackageOperations, fixture.Application, null!);
        var entered = BuilderUiFixture.Signal();
        var resume = BuilderUiFixture.Signal();
        var ambient = new AsyncLocal<string?> { Value = "incoming-rpc-lineage" };
        string? observedAmbient = "not-started";
        fixture.Backend.BeforeGetBackendId = async token =>
        {
            observedAmbient = ambient.Value;
            entered.TrySetResult();
            await resume.Task.WaitAsync(token);
        };
        using var caller = new CancellationTokenSource();
        var operation = Guid.NewGuid().ToString("N");
        var request = Request(project);
        using var accepted = await BuilderContent.ReadAsync<JsonDocument>(service.StartOperationAsync(operation, "build", request, caller.Token), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            Assert.Null(observedAmbient);
            Assert.Equal("incoming-rpc-lineage", ambient.Value);
            Assert.Equal("running", accepted.RootElement.GetProperty("state").GetString());
            resume.TrySetResult();
            await WaitForOperationAsync(service, operation, request, "succeeded");
            fixture.Backend.Projects[project.EntryPoint] = project with { SourceRevision = "later-source" };
            var repeated = Enumerable.Range(0, 8).Select(async _ =>
            {
                using var observed = await BuilderContent.ReadAsync<JsonDocument>(service.StartOperationAsync(operation, "build", request), default);
                Assert.Equal("succeeded", observed.RootElement.GetProperty("state").GetString());
            });
            await Task.WhenAll(repeated);
            Assert.Single(fixture.Backend.Starts);
            Assert.Empty(fixture.Development.Loads);
            Assert.Empty(fixture.Model.Projects);
        }
        finally { resume.TrySetResult(); }
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("missing-preflight")]
    [InlineData("scope")]
    public async Task AdmissionRejectsChangedOrUnreviewedSourceBeforeAnyStart(string boundary)
    {
        await using var fixture = new BuilderUiFixture();
        var project = Project(fixture, "source-fence");
        await using var service = new BuilderDevelopmentService(fixture.PackageOperations, fixture.Application, null!);
        var json = boundary switch
        {
            "revision" => Request(project with { SourceRevision = "stale" }),
            "missing-preflight" => JsonSerializer.Serialize(new { project.EntryPoint, roots = new[] { project.ProjectFolder } }),
            _ => JsonSerializer.Serialize(new { project.EntryPoint, roots = new[] { Path.Combine(project.ProjectFolder, "unrelated") }, expectedSourceRevision = project.SourceRevision }),
        };
        await Assert.ThrowsAsync<SunderRpcException>(async () =>
        {
            using var response = await BuilderContent.ReadAsync<JsonDocument>(service.StartOperationAsync(Guid.NewGuid().ToString("N"), "build", json), default);
        });
        Assert.Empty(fixture.Backend.Starts);
    }

    [Fact]
    public async Task BoundedAdmissionNeverEvictsAMutationReceiptAndCannotReplayAnOldId()
    {
        await using var fixture = new BuilderUiFixture();
        var project = Project(fixture, "receipt-capacity");
        await using var service = new BuilderDevelopmentService(fixture.PackageOperations, fixture.Application, null!);
        var request = Request(project);
        var first = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 64; i++)
        {
            var id = i == 0 ? first : Guid.NewGuid().ToString("N");
            using var accepted = await BuilderContent.ReadAsync<JsonDocument>(service.StartOperationAsync(id, "build", request), default);
            await WaitForOperationAsync(service, id, request, "succeeded");
        }
        await Assert.ThrowsAsync<SunderRpcException>(async () =>
        {
            using var overflow = await BuilderContent.ReadAsync<JsonDocument>(service.StartOperationAsync(Guid.NewGuid().ToString("N"), "build", request), default);
        });
        using var original = await BuilderContent.ReadAsync<JsonDocument>(service.StartOperationAsync(first, "build", request), default);
        Assert.Equal("succeeded", original.RootElement.GetProperty("state").GetString());
        Assert.Equal(64, fixture.Backend.Starts.Count);
    }

    private static BuilderProjectInspection Project(BuilderUiFixture fixture, string name)
    {
        var source = BuilderUiFixture.Project(name);
        var paths = new BuilderPathService();
        var folder = paths.ResolveContainedHostPath(source.ProjectFolder, Path.GetTempPath());
        var project = source with { ProjectFolder = folder, EntryPoint = Path.Combine(folder, "Package.csproj") };
        fixture.Backend.Projects[project.EntryPoint] = project;
        return project;
    }
    private static string Request(BuilderProjectInspection project) => JsonSerializer.Serialize(new
    { entryPoint = project.EntryPoint, roots = new[] { project.ProjectFolder }, expectedSourceRevision = project.SourceRevision });
    private static async Task WaitForOperationAsync(BuilderDevelopmentService service, string id, string request, string state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            using var observed = await BuilderContent.ReadAsync<JsonDocument>(service.GetOperationAsync(id, request, timeout.Token), timeout.Token);
            if (observed.RootElement.GetProperty("state").GetString() == state) return;
            await Task.Delay(10, timeout.Token);
        }
    }
}
