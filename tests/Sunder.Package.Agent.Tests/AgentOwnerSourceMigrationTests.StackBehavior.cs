using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentOwnerSourceMigrationTests
{
    private static readonly JsonSerializerOptions StackJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SunderRpcJsonConverterFactory() },
    };
    private static async Task VerifyStackBehavior(Assembly assembly)
    {
        using var source = RegressionTestPackageScope.Create();
        using var target = RegressionTestPackageScope.Create();
        using var sourceCatalog = new RegressionTestExtensionCatalog();
        using var targetCatalog = new RegressionTestExtensionCatalog();
        var sourceRuntime = await AgentPublicSourceMigrationTests.CreateCanonicalRuntime(typeof(AgentProfileService).Assembly, source.Context, sourceCatalog);
        var targetRuntime = await AgentPublicSourceMigrationTests.CreateCanonicalRuntime(typeof(AgentProfileService).Assembly, target.Context, targetCatalog);
        await using var sourceLifetime = sourceRuntime.Lifetime;
        await using var targetLifetime = targetRuntime.Lifetime;
        var sourceServices = sourceRuntime.Services;
        var targetServices = targetRuntime.Services;
        var profiles = sourceServices.GetRequiredService<AgentProfileService>();
        var profile = await profiles.CreateProfileAsync("Stack profile 漢字");
        profiles.ImportProfile(profile with { Description = "Profile details", Instructions = "Preserved instructions" });
        profiles.SaveRuntimeProfile(profile with { ProfileId = "internal.profile", DisplayName = "Hidden internal" });
        var workspaces = sourceServices.GetRequiredService<AgentWorkspaceService>();
        var workspace = workspaces.CreateWorkspace("Stack workspace 漢字");
        workspaces.ImportWorkspace(workspace with { Description = "Workspace details" },
            [new("root", workspace.WorkspaceId, source.RootPath, true, 0, default, default)],
            [new("guide", workspace.WorkspaceId, Path.Combine(source.RootPath, "guide.md"), 0, default, default)], "local");

        foreach (var isProfile in new[] { true, false })
        {
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Rpc.Owner."
                + (isProfile ? "AgentProfileStackProvider" : "AgentWorkspaceStackProvider")));
            var providerId = isProfile ? "agent.stack.profiles" : "agent.stack.workspaces";
            var sourceDomain = isProfile ? (IPackageStackContributor)sourceServices.GetRequiredService<AgentProfileStackContributor>()
                : sourceServices.GetRequiredService<AgentWorkspaceStackContributor>();
            var targetDomain = isProfile ? (IPackageStackContributor)targetServices.GetRequiredService<AgentProfileStackContributor>()
                : targetServices.GetRequiredService<AgentWorkspaceStackContributor>();
            var probe = new StackProbe(targetDomain);
            var implementation = sourceDomain;
            var targetImplementation = probe;
            var registration = Registration(implementation);
            var descriptor = SunderStackContributorRpc.Descriptor; // Frozen oracle, not generation input.
            var targetRegistration = new SunderWorkerProviderRegistration(providerId, descriptor.ContractId, descriptor.Version,
                descriptor.Sha256, descriptor.AbiSha256!, SunderStackContributorRpc.CreateHandler(targetImplementation));
            Assert.Equal(providerId, registration.ProviderId);
            Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
            Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
            Assert.Equal(6, registration.DeclaredErrors["stack-contributor"].Count);
            Assert.Empty(registration.DeclaredErrors["stack-contributor"]["metadata"]);
            Assert.All(registration.DeclaredErrors["stack-contributor"].Where(pair => pair.Key != "metadata"),
                pair => Assert.Equal(new[] { "stack.method.unsupported" }, pair.Value));
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, "sunder.core");
            state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, Noop.Instance, providerId, "2.0.0");
            var sourceEndpoint = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers).Endpoint;

            var metadata = await Call(sourceEndpoint, "metadata", new { });
            Assert.Equal(sourceDomain.ContributorId, metadata.GetProperty("contributorId").GetString());
            Assert.True(metadata.GetProperty("supportsExport").GetBoolean());
            Assert.True(metadata.GetProperty("supportsImport").GetBoolean());
            Assert.True(metadata.GetProperty("supportsImportApplied").GetBoolean());
            var itemId = isProfile ? profile.ProfileId : workspace.WorkspaceId;
            var discoveryRequest = new StackExportDiscoveryContext("sunder.package.agent");
            var items = await Call(sourceEndpoint, "list-export-items", discoveryRequest);
            Assert.Contains(items.GetProperty("items").EnumerateArray(), item => item.GetProperty("itemId").GetString() == itemId);
            Assert.DoesNotContain(items.GetProperty("items").EnumerateArray(), item => item.GetProperty("itemId").GetString() is "internal.profile" or AgentWorkspaceService.UnassignedSessionsWorkspaceId);
            var exportRequest = new StackExportRequest([new(itemId)]);
            var exported = await Call(sourceEndpoint, "export", exportRequest);
            foreach (var (route, parityRequest) in new (string, object)[] { ("metadata", new { }), ("list-export-items", discoveryRequest), ("export", exportRequest) })
            {
                object expectedValue = route switch
                {
                    "metadata" => new { sourceDomain.ContributorId, sourceDomain.DisplayName, SupportsExport = sourceDomain is IPackageStackExporter,
                        SupportsImport = sourceDomain is IPackageStackImporter, SupportsImportApplied = sourceDomain is IPackageStackImportAppliedHandler },
                    "list-export-items" => new { Items = await ((IPackageStackExporter)sourceDomain).ListExportItemsAsync(discoveryRequest) },
                    "export" => await ((IPackageStackExporter)sourceDomain).ExportAsync(exportRequest),
                    _ => throw new InvalidOperationException("Unknown stack scenario."),
                };
                var expected = JsonSerializer.SerializeToElement(expectedValue, expectedValue.GetType(), StackJson);
                Assert.True(JsonElement.DeepEquals(expected, await Call(sourceEndpoint, route, parityRequest)), route);
            }
            var requirement = Assert.Single(exported.GetProperty("packageRequirements").EnumerateArray());
            Assert.Equal("sunder.package.agent", requirement.GetProperty("packageId").GetString());
            Assert.Equal("1.1.0", requirement.GetProperty("minimumVersion").GetString());
            var fragment = Assert.Single(exported.GetProperty("fragments").EnumerateArray());
            Assert.Contains(isProfile ? "Preserved instructions" : source.RootPath, fragment.GetProperty("jsonPayload").GetString()!, StringComparison.Ordinal);
            var imported = new StackRpcFragmentImport(fragment.GetProperty("fragmentId").GetString()!, "forged.owner", "forged.contributor",
                fragment.GetProperty("schemaId").GetString()!, fragment.GetProperty("schemaVersion").GetInt32(), fragment.GetProperty("displayName").GetString()!,
                fragment.GetProperty("jsonPayload").GetString()!);
            await state.RetireAsync(implementation);
            state.Add("sunder.package.agent", descriptor, targetImplementation, targetRegistration.Handler, Noop.Instance, providerId, "2.0.0");
            var targetEndpoint = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers).Endpoint;
            Assert.NotEqual(sourceEndpoint, targetEndpoint);
            var stale = await Assert.ThrowsAsync<SunderRpcException>(() => Call(sourceEndpoint, "metadata", new { }));
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
            var remaps = new Dictionary<string, string> { [profile.ProfileId] = "imported.profile" };
            var previewRequest = new StackRpcImportPreviewRequest([imported], new Dictionary<string, string>(), remaps);
            var preview = await Call(targetEndpoint, "preview-import", previewRequest);
            var action = Assert.Single(preview.GetProperty("actions").EnumerateArray());
            Assert.Equal((int)StackImportActionKind.Create, action.GetProperty("kind").GetInt32());
            Assert.Equal("sunder.package.agent", probe.LastFragment!.OwnerPackageId);
            Assert.Equal(targetDomain.ContributorId, probe.LastFragment.ContributorId);
            if (isProfile) Assert.Null(targetServices.GetRequiredService<AgentProfileService>().GetProfile("imported.profile"));
            else Assert.Null(targetServices.GetRequiredService<AgentWorkspaceService>().GetWorkspace(workspace.WorkspaceId));
            var request = new StackRpcImportRequest([imported], new Dictionary<string, string>(), remaps, []);
            var unselected = await Call(targetEndpoint, "import", request);
            Assert.Empty(unselected.GetProperty("importedItems").EnumerateArray());
            request = request with { SelectedActionIds = [action.GetProperty("actionId").GetString()!] };
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(targetEndpoint, "import", request, cancelled.Token));
            }
            var result = await Call(targetEndpoint, "import", request);
            Assert.Equal((int)StackImportOutcome.Completed, result.GetProperty("outcome").GetInt32());
            Assert.Single(result.GetProperty("importedItems").EnumerateArray());
            if (isProfile)
            {
                var persisted = targetServices.GetRequiredService<AgentProfileService>().GetProfile("imported.profile");
                Assert.NotNull(persisted);
                Assert.Equal(profile.DisplayName, persisted.DisplayName);
                Assert.Equal("Preserved instructions", persisted.Instructions);
                Assert.False(persisted.IsInternal);
            }
            else
            {
                var persisted = targetServices.GetRequiredService<AgentWorkspaceService>().GetWorkspace(workspace.WorkspaceId);
                Assert.NotNull(persisted);
                Assert.Equal(source.RootPath, Assert.Single(persisted.Paths).HostPath);
                Assert.Equal(Path.Combine(source.RootPath, "guide.md"), Assert.Single(persisted.Documents).FilePath);
                Assert.Equal("local", Assert.Single(targetServices.GetRequiredService<AgentWorkspaceService>().ListBindings(workspace.WorkspaceId)).ContributionId);
            }
            var notifications = 0;
            if (isProfile) targetServices.GetRequiredService<AgentProfileService>().ProfileChanged += _ => notifications++;
            else targetServices.GetRequiredService<AgentWorkspaceService>().WorkspacesChanged += () => notifications++;
            var applied = new StackImportAppliedContext("forged.owner", "forged.contributor", [imported.FragmentId],
                result.GetProperty("importedItems").Deserialize<StackImportedItem[]>(StackJson)!);
            Assert.Empty((await Call(targetEndpoint, "import-applied", applied)).EnumerateObject());
            Assert.Equal("sunder.package.agent", probe.Applied!.OwnerPackageId);
            Assert.Equal(targetDomain.ContributorId, probe.Applied.ContributorId);
            Assert.True(notifications > 0);
            var malformed = await Call(targetEndpoint, "preview-import", previewRequest with
            {
                Fragments = [imported with { JsonPayload = "{broken" }],
            });
            Assert.Empty(malformed.GetProperty("actions").EnumerateArray());
            Assert.NotEmpty(malformed.GetProperty("warnings").EnumerateArray());
            probe.Fail = true;
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => Call(targetEndpoint, "export", exportRequest));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
            Assert.DoesNotContain("private stack failure", error.Message, StringComparison.Ordinal);
            await state.RetireAsync(targetImplementation);
            Assert.Empty((await rpc.DiscoverAsync(descriptor.ContractId)).Providers);

            Task<JsonElement> Call(SunderRpcEndpointReference endpoint, string method, object value, CancellationToken token = default)
                => rpc.InvokeAsync(endpoint, "stack-contributor", method, JsonSerializer.SerializeToElement(value, StackJson), cancellationToken: token).AsTask();
        }

        static SunderWorkerProviderRegistration Registration(object value)
            => Assert.IsType<SunderWorkerProviderRegistration>(value.GetType().GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(value, null));
    }

    private sealed class StackProbe(IPackageStackContributor contributor) : IPackageStackExporter, IPackageStackImporter, IPackageStackImportAppliedHandler
    {
        public string ContributorId => contributor.ContributorId;
        public string DisplayName => contributor.DisplayName;
        public StackFragmentImport? LastFragment { get; private set; }
        public StackImportAppliedContext? Applied { get; private set; }
        public bool Fail { get; set; }
        public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(StackExportDiscoveryContext request, CancellationToken token = default)
            => ((IPackageStackExporter)contributor).ListExportItemsAsync(request, token);
        public ValueTask<StackExportContribution> ExportAsync(StackExportRequest request, CancellationToken token = default)
            => Fail ? throw new InvalidOperationException("private stack failure") : ((IPackageStackExporter)contributor).ExportAsync(request, token);
        public ValueTask<StackImportPreview> PreviewImportAsync(StackImportPreviewRequest request, CancellationToken token = default)
        {
            LastFragment = request.Fragments.FirstOrDefault();
            return ((IPackageStackImporter)contributor).PreviewImportAsync(request, token);
        }
        public ValueTask<StackImportResult> ImportAsync(StackImportRequest request, CancellationToken token = default)
            => ((IPackageStackImporter)contributor).ImportAsync(request, token);
        public ValueTask OnStackImportAppliedAsync(StackImportAppliedContext request, CancellationToken token = default)
        {
            Applied = request;
            return ((IPackageStackImportAppliedHandler)contributor).OnStackImportAppliedAsync(request, token);
        }
    }
}
