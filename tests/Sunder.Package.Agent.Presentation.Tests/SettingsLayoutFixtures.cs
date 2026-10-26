extern alias BuilderApp;
extern alias McpApp;
extern alias SkillsApp;

using BuilderApp::Sunder.Package.Agent.Builder;
using Mcp = McpApp::Sunder.Package.Agent.Mcp;
using ParsedMcpServerConfiguration = McpApp::Sunder.Package.Agent.Mcp.ParsedMcpServerConfiguration;
using McpApp::Sunder.Package.Agent.Mcp.Runtime;
using McpApp::Sunder.Package.Agent.Mcp.Services;
using SkillsApp::Sunder.Package.Agent.Skills.Runtime;
using SkillsApp::Sunder.Package.Agent.Skills.Services;

namespace Sunder.Package.Agent.Presentation.Tests;

// Observation-only fixtures: no live Runtime, files, network, SDK jobs or project mutations.
internal static class SettingsLayoutFixtures
{
    internal sealed class McpGateway : IMcpManagementGateway
    {
        public event Action? ServersChanged { add { } remove { } }
        public event Action? StatusChanged { add { } remove { } }
        public int CommonImports { get; private set; }
        public int FileImports { get; private set; }
        public IReadOnlyList<McpCatalogDiagnostic> Diagnostics => [];
        public Task<IReadOnlyList<Mcp.ConfiguredMcpServerRecord>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Mcp.ConfiguredMcpServerRecord>>([]);
        public Task<McpConfigurationImportResult> InitializeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new McpConfigurationImportResult(0, 0, []));
        public Task<McpConfigurationImportResult> ImportCommonAsync(CancellationToken cancellationToken = default)
        { CommonImports++; return Task.FromResult(new McpConfigurationImportResult(2, 0, [])); }
        public Task<McpConfigurationImportResult> ImportFileAsync(string filePath, CancellationToken cancellationToken = default)
        { FileImports++; return Task.FromResult(new McpConfigurationImportResult(1, 0, [])); }
        public Task<string?> LoadDocumentAsync(string serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string NormalizeName(string? name) => name ?? string.Empty;
        public ParsedMcpServerConfiguration Parse(string serverId, string name, string document, Mcp.ConfiguredMcpServerRecord? existing) => throw new NotSupportedException();
        public string Format(string serverId, string name, string document, Mcp.ConfiguredMcpServerRecord? existing) => document;
        public Task<string> SaveAsync(ParsedMcpServerConfiguration parsed, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> DiscoverAsync(Mcp.ConfiguredMcpServerRecord server, bool reconnect, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DisconnectAsync(string serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<McpConnectionPresentation> GetPresentationAsync(Mcp.ConfiguredMcpServerRecord server, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AuthorizeAsync(Mcp.ConfiguredMcpServerRecord server, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearAuthorizationAsync(Mcp.ConfiguredMcpServerRecord server, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal sealed class SkillGateway : ISkillManagementGateway
    {
        public event Action? SkillsChanged { add { } remove { } }
        public Task<IReadOnlyList<InstalledSkillRecord>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InstalledSkillRecord>>([]);
        public Task<IReadOnlyList<InstalledSkillRecord>> ImportCommonAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InstalledSkillRecord>>([]);
        public Task<IReadOnlyList<InstalledSkillRecord>> ImportGitHubAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InstalledSkillRecord>> ImportLocalAsync(string folderPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string skillId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    internal sealed class SdkBackend : IBuilderBackend
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Checks;
        public async Task<BuilderToolchainStatus> GetToolchainsAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref Checks) == 2) Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var version = kind == BuilderProjectKind.Dotnet ? "10.0.204" : "24.18.1";
            return new(kind, [new() { Kind = kind, Version = version, ExecutablePath = "/synthetic/sdk/" + (kind == BuilderProjectKind.Dotnet ? "dotnet" : "node"),
                RootPath = "/synthetic/sdk", IsHealthy = true, IsManaged = false, NpmVersion = kind == BuilderProjectKind.Node ? "11.0.0" : null }],
                version, DateTimeOffset.UtcNow, false, null);
        }
        public Task<IReadOnlyList<BuilderProjectInspection>> DiscoverAsync(string folder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BuilderProjectInspection> InspectAsync(string entryPoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BuilderRecordedJobRecovery?> RecoverRecordedJobAsync(BuilderJobReservation reservation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
