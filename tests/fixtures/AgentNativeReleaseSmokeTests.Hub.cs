using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class AgentNativeReleaseSmokeTests
{
    private static async Task<object> ApplyHubPlanAsync(IServiceProvider services, string origin, string rid,
        IReadOnlyList<RegistryPackageVersionIdentity> subjects, IReadOnlyDictionary<string, string> archives, string? reinstallId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var token = deadline.Token;
        var change = services.GetRequiredService<RegistryPackageChangeOrchestrator>();
        var selected = subjects.Where(s => reinstallId is null || s.PackageId == reinstallId).OrderBy(s => s.PackageId, StringComparer.Ordinal).ToArray();
        var request = new RuntimeRegistryPackageBatchRequest(origin,
            selected.Select(s => new RuntimeRegistryPackageChangeRequest(s.PackageId, s.Version, [])).ToArray(), Reinstall: reinstallId is not null);
        var preview = await change.ResolveAsync(request, token);
        Assert.True(preview.Success, string.Join("; ", preview.Errors));
        Assert.Empty(preview.Errors);
        Assert.Empty(preview.Conflicts);
        Assert.Empty(preview.CompatibilityConflicts);
        Assert.Matches("^[0-9a-f]{64}$", preview.ResolutionFingerprint);
        Assert.True(preview.ExpiresAtUtc > DateTimeOffset.UtcNow);
        var prerequisites = Assert.IsType<RuntimePackageRuntimePrerequisitePlan>(preview.RuntimePrerequisites);
        Assert.True(prerequisites.Success && prerequisites.Satisfied, prerequisites.Message);
        Assert.Equal(rid, prerequisites.Rid);
        Assert.Matches("^[0-9a-f]{64}$", prerequisites.SourceFingerprint);
        var modified = preview.Items.Where(i => i.ActionKind != RegistryPackageActionKinds.Keep).ToArray();
        AssertSamePackageIds(selected.Select(s => s.PackageId), modified.Select(i => i.PackageId));
        foreach (var item in preview.Items)
        {
            var subject = Assert.Single(subjects, s => s.PackageId == item.PackageId);
            Assert.Equal(subject.Version, item.Version);
            Assert.Equal(subject.PackageContentIdentity, item.PackageContentIdentity);
            if (item.ActionKind == RegistryPackageActionKinds.Keep) continue;
            Assert.Equal(reinstallId is null ? RegistryPackageActionKinds.PackageInstall : RegistryPackageActionKinds.PackageUpdate, item.ActionKind);
            var path = archives[item.PackageId];
            using var input = File.OpenRead(path);
            var archiveHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("manifest/sunder-package.json")!;
            using var manifestStream = entry.Open();
            var manifestHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(manifestStream, token));
            using var metadata = entry.Open();
            using var manifest = JsonDocument.Parse(metadata);
            var roles = manifest.RootElement.GetProperty("targets").EnumerateArray()
                .Where(t => t.GetProperty("rid").GetString() == rid).Select(t => t.GetProperty("role").GetString()!).ToArray();
            AssertSamePackageIds(roles.Append("shared"), item.Artifacts.Select(a => a.Kind));
            foreach (var artifact in item.Artifacts)
            {
                Assert.Equal(artifact.Kind == "shared" ? null : rid, artifact.Rid);
                Assert.Equal(archiveHash, artifact.SourceArchiveSha256);
                Assert.Equal(manifestHash, artifact.ManifestSha256);
                Assert.Matches("^[0-9a-f]{64}$", artifact.ProjectionContentIdentity);
                Assert.Matches("^[0-9a-f]{64}$", artifact.Sha256);
                Assert.True(artifact.Size > 0);
            }
        }
        // Only approve sensitive capabilities declared by the exact canonical
        // bytes. Reinstalling the same manifest must inherit its existing grants.
        var consents = reinstallId is null
            ? selected.Select(s => ReadCapabilityConsent(archives[s.PackageId], rid)).OfType<PackageCapabilityConsent>().ToArray()
            : [];
        AssertSamePackageIds(consents.Select(c => c.PackageId), preview.RequiredCapabilityConsents.Select(c => c.PackageId));
        foreach (var approved in consents)
        {
            var required = Assert.Single(preview.RequiredCapabilityConsents, c => c.PackageId == approved.PackageId);
            Assert.Equal(approved.PackageVersion, required.PackageVersion);
            Assert.Equal(approved.ManifestSha256, required.ManifestSha256);
            AssertSamePackageIds(approved.Capabilities, required.Capabilities);
        }
        var result = await change.ExecuteAsync(request with
        {
            ExpectedResolutionFingerprint = preview.ResolutionFingerprint, CapabilityConsents = consents,
            RuntimePrerequisiteExpectation = new(prerequisites.SourceFingerprint, rid),
        }, token);
        Assert.True(result.Success, result.Message + ": " + string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
        Assert.Empty(result.Conflicts);
        Assert.True(result.RuntimeSessionApplied);
        Assert.NotNull(result.CommittedStamp);
        var confirmed = Assert.Single(result.PlanExpectations);
        Assert.Equal(new Uri(origin), new Uri(confirmed.RegistryOrigin));
        Assert.Equal(preview.ResolutionFingerprint, confirmed.ExpectedResolutionFingerprint);
        Assert.Equal(prerequisites.SourceFingerprint, result.RuntimePrerequisites?.SourceFingerprint);
        var store = await services.GetRequiredService<InstalledPackageStore>().ListAsync(token);
        AssertSamePackageIds(subjects.Select(s => s.PackageId), store.Select(p => p.PackageId));
        var packages = new List<object>();
        foreach (var item in modified)
        {
            var record = Assert.Single(store, p => p.PackageId == item.PackageId);
            Assert.Equal(InstalledPackageSourceKind.Registry, record.Provenance.SourceKind);
            Assert.Equal(InstalledPackageVersionPolicy.ExplicitVersion, record.Provenance.VersionPolicy);
            Assert.Equal(item.PackageId, record.Provenance.SourcePackageId);
            Assert.Equal(new Uri(origin), new Uri(record.Provenance.RegistryOrigin!));
            Assert.Equal(item.Version, record.Provenance.RequestedVersion);
            Assert.Equal(item.Version, record.Version);
            Assert.Equal(item.PackageContentIdentity, record.Provenance.SourceIdentity);
            Assert.Equal(item.PackageContentIdentity, record.SourcePackageContentIdentity);
            Assert.NotNull(record.Compatibility);
            record.Compatibility.Validate(record);
            Assert.NotNull(record.Policy);
            record.Policy.Validate(record);
            Assert.False(record.Policy.IsBlocked);
            Assert.NotNull(record.AcquiredTargets);
            AssertSamePackageIds(item.Artifacts.Where(a => a.Kind != "shared").Select(a => a.Kind),
                record.AcquiredTargets.Select(t => t.Role));
            Assert.All(record.AcquiredTargets, t => Assert.Equal(rid, t.Rid));
            packages.Add(new { item.PackageId, record.Version, item.ActionKind, record.SourcePackageContentIdentity,
                provenance = new { source = record.Provenance.SourceKind.ToString(), policy = record.Provenance.VersionPolicy.ToString(),
                    record.Provenance.RegistryOrigin, record.Provenance.SourcePackageId, record.Provenance.RequestedVersion, record.Provenance.SourceIdentity },
                targets = record.AcquiredTargets,
                projections = item.Artifacts.Select(a => new { a.Kind, a.Rid, a.Sha256, a.Size, a.ManifestSha256, a.SourceArchiveSha256, a.ProjectionContentIdentity }) });
        }
        return new { preview.ResolutionFingerprint, confirmed.ExpectedResolutionFingerprint, prerequisites.SourceFingerprint,
            prerequisites.Rid, result.RuntimeSessionApplied, result.CommittedStamp, consents, packages };
    }

    // The Hub orchestrator owns the stage. Time the actual Host acknowledgement
    // on its emitting thread, not the later return from the whole install.
    private sealed class HubActivationObserver : ILoggerProvider, ILogger
    {
        private const string Ack = "Worker V1 activation acknowledged for package {PackageId}, activation {ActivationId}, generation {SessionGeneration}";
        private readonly ConcurrentDictionary<(string, Guid, long), DateTimeOffset> _acks = new();
        public void Attach(ILoggingBuilder logging)
            => logging.AddProvider(this).AddFilter<HubActivationObserver>(null, LogLevel.Information);
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level == LogLevel.Information;
        public void Dispose() { }
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> fields || fields.Count != 4
                || !Equals(fields[^1].Value, Ack)) return;
            if (fields[0].Value is string package && fields[1].Value is Guid activation && fields[2].Value is long generation)
                _acks.TryAdd((package, activation, generation), DateTimeOffset.UtcNow);
        }
        public WorkerActivationMeasurement Measure(ActiveLoadedPackage package)
        {
            var worker = Assert.IsType<ProcessRuntimeWorker>(package.Worker);
            Assert.True(worker.ActivationCompletion.IsCompletedSuccessfully);
            Assert.True(_acks.TryGetValue((package.Descriptor.PackageId, package.RuntimeActivationId,
                package.ActivationSessionGeneration!.Value), out var at), $"Missing Host activation acknowledgement for {package.Descriptor.PackageId}.");
            return new(worker, Task.FromResult(at));
        }
    }
}
