extern alias McpWorker;
extern alias MemoryWorker;
extern alias OpenAiWorker;
extern alias SkillsWorker;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.HistorySearch;
using McpWorker::Sunder.Package.Agent.Mcp.Services;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using OpenAiWorker::Sunder.Package.Agent.Provider.OpenAI.Auth;
using SkillsWorker::Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentCompatibilityCandidateTests
{
    private const string UpdateEnvironmentVariable = "SUNDER_UPDATE_AGENT_COMPATIBILITY_CANDIDATE";
    private const string CandidateRelativePath = "compatibility/agent-compatibility-candidate.json";
    private const string ReleaseEvidenceRelativePath = "compatibility/agent-release-evidence.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [Fact]
    public void GeneratedCompatibilityCandidate_MatchesSourceAndLiveSchemas()
    {
        RejectHistoricalCandidateUpdate(Environment.GetEnvironmentVariable(UpdateEnvironmentVariable));
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var candidatePath = Path.Combine(root, NormalizePath(CandidateRelativePath));
        Assert.True(File.Exists(candidatePath), $"Compatibility candidate was not found: {candidatePath}");
        Assert.Equal(AgentSourceMigrationProjectChanges.HistoricalCandidateSha256, HashFile(candidatePath));
        AgentSourceMigrationProjectChanges.AssertRetirementCurrentTopology(root);
        var generated = Generate(root);
        // This pre-canonical candidate is historical evidence. Current topology and
        // generated contract/policy closure are checked independently below; neither
        // this candidate nor the frozen RPC baseline is an update-switch output.
        Assert.Equal(Encoding.UTF8.GetString(File.ReadAllBytes(candidatePath)), Encoding.UTF8.GetString(generated));
    }

    [Fact]
    public void HistoricalCandidate_RejectsTheFormerUpdateSwitch()
        => Assert.Throws<InvalidOperationException>(() => RejectHistoricalCandidateUpdate("1"));

    [Fact]
    public void CanonicalCompatibilityClosure_MatchesCurrentDescriptorsPoliciesAndTopology()
        => AgentSourceMigrationProjectChanges.AssertCurrentCanonicalCompatibility(AgentPackageRepositoryInventory.RepositoryRoot.FullName);

    private static void RejectHistoricalCandidateUpdate(string? value)
    {
        if (string.Equals(value, "1", StringComparison.Ordinal))
            throw new InvalidOperationException("The pre-canonical compatibility candidate and RPC baseline are immutable history; the candidate-update switch cannot rewrite them.");
    }

    private static byte[] Generate(string root)
    {
        var descriptors = ReadDescriptors(root);
        var declarations = ReadDeclarations(root);
        var historicalInputs = AgentSourceMigrationProjectChanges.ReadCandidateInputHistory(root);
        var packageInventoryInput = historicalInputs["packages.json"];
        using var packageInventoryDocument = JsonDocument.Parse(packageInventoryInput.Text);
        var packageInventory = packageInventoryDocument.RootElement.Clone();
        var extensionPoints = ReadExtensionPoints(root);
        var roles = ReadProjectRoles(root, historicalInputs["Sunder.Agent.Worker.props"].Text);
        var packages = ReadRuntimePackages(root);
        var locks = ReadReleaseLocks(root, historicalInputs);
        var publicApi = new[]
        {
            ReadTextSnapshot(root, "src/Sunder.Package.Agent.Contracts/PublicAPI.Shipped.txt"),
            ReadTextSnapshot(root, "src/Sunder.Package.Agent.Contracts/PublicAPI.Unshipped.txt"),
        };
        var schemas = ReadSchemas();
        var sourceVocabulary = ReadSourceVocabulary(root);
        var releaseEvidencePath = Path.Combine(root, NormalizePath(ReleaseEvidenceRelativePath));
        using var releaseEvidenceDocument = JsonDocument.Parse(File.ReadAllBytes(releaseEvidencePath));

        Assert.Equal(31, descriptors.Count);
        Assert.Equal(19, descriptors.Count(static descriptor => descriptor.Exposure == "public"));
        Assert.Equal(12, descriptors.Count(static descriptor => descriptor.Exposure == "package"));
        Assert.Equal(31, declarations.Bindings.Count);
        Assert.Equal(19, declarations.Bindings.Count(static binding =>
            binding.Metadata.TryGetValue("Accessibility", out var accessibility)
            && accessibility == "Public"));
        Assert.Equal(12, declarations.Bindings.Count(static binding =>
            binding.Metadata.TryGetValue("Accessibility", out var accessibility)
            && accessibility == "Internal"));
        Assert.Equal(63, declarations.Providers.Count);
        Assert.Equal(46, declarations.Consumers.Count);
        Assert.Equal(63, declarations.Bundles.Count);
        Assert.Equal(19, extensionPoints.Count);
        Assert.Equal(18, extensionPoints.Count(static extension => !extension.ReservedFirstParty));
        Assert.Equal(15, packages.Count);
        Assert.Equal(1, publicApi[1].LineCount);

        var candidate = new CompatibilityCandidate(
            SchemaVersion: 2,
            ReleaseVersion: packageInventory.GetProperty("releaseVersion").GetString()!,
            Descriptors: descriptors,
            Declarations: declarations,
            ExtensionPoints: extensionPoints,
            PackageInventorySha256: packageInventoryInput.Sha256,
            PackageInventory: packageInventory,
            RuntimePackages: packages,
            ProjectRoles: roles,
            RuntimeReleaseLocks: locks.Runtime,
            TestRestoreLocks: locks.Tests,
            PublicApi: publicApi,
            SqliteSchemas: schemas,
            JsonDocuments:
            [
                new("skills-index", SkillPersistenceSchemas.IndexId, SkillPersistenceSchemas.CurrentVersion, "released top-level array", "accept legacy; reject malformed/unknown without overwrite; write current envelope"),
                new("skills-import-journal", SkillPersistenceSchemas.ImportJournalId, SkillPersistenceSchemas.CurrentVersion, "released unversioned object", "recover legacy/current; reject malformed/unknown without filesystem mutation"),
                new("mcp-server-record", McpServerRecordSerializer.DocumentSchemaId, McpServerRecordSerializer.DocumentSchemaVersion, "released flat record", "accept legacy; preserve and diagnose malformed/unknown; write current envelope"),
                new("openai-codex-session", CodexSessionStore.DocumentSchemaId, CodexSessionStore.DocumentSchemaVersion, "released unversioned secret", "accept legacy; fail closed and preserve malformed/unknown; write current envelope"),
            ],
            ConfigurationKeys: sourceVocabulary.ConfigurationKeys,
            EnvironmentVariables: sourceVocabulary.EnvironmentVariables,
            TelemetryVocabulary: sourceVocabulary.TelemetryVocabulary,
            ReleaseEvidenceSha256: HashFile(releaseEvidencePath),
            ReleaseEvidence: releaseEvidenceDocument.RootElement.Clone());
        return JsonSerializer.SerializeToUtf8Bytes(candidate, JsonOptions);
    }

    private static IReadOnlyList<DescriptorSnapshot> ReadDescriptors(string root)
    {
        var publicRoot = NormalizeRelativePath("src/Sunder.Package.Agent.Contracts/Contracts") + "/";
        return AgentSourceMigrationProjectChanges.EnumerateMigrationHistoryFiles(root, Path.Combine(root, "src"), "*.rpc.json")
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Select(path =>
            {
                var relativePath = RelativePath(root, path);
                var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(AgentSourceMigrationProjectChanges.ReadMigrationSourceText(path)));
                return new DescriptorSnapshot(
                    relativePath,
                    descriptor.ContractId,
                    descriptor.DescriptorVersion,
                    descriptor.Version,
                    descriptor.SchemaProfile ?? throw new InvalidDataException($"Descriptor '{relativePath}' has no schema profile."),
                    descriptor.Sha256,
                    descriptor.AbiSha256 ?? throw new InvalidDataException($"Descriptor '{relativePath}' has no ABI hash."),
                    relativePath.StartsWith(publicRoot, StringComparison.Ordinal) ? "public" : "package");
            })
            .OrderBy(static descriptor => descriptor.ContractId, StringComparer.Ordinal)
            .ThenBy(static descriptor => descriptor.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static DeclarationInventory ReadDeclarations(string root)
    {
        var sources = AgentSourceMigrationProjectChanges.EnumerateMigrationHistoryFiles(root, Path.Combine(root, "src"), "*")
            .Concat(Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(static path => Path.GetExtension(path) is ".csproj" or ".props" or ".targets")
            .Order(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<MsBuildDeclaration> Read(string itemName) => sources
            .SelectMany(source => AgentSourceMigrationProjectChanges.ReadMsBuildHistoryDocument(root, source)
                .Descendants(itemName)
                .Select(item => new MsBuildDeclaration(
                    RelativePath(root, source),
                    NormalizeRelativePath(item.Attribute("Include")?.Value ?? string.Empty),
                    ReadMetadata(item))))
            .OrderBy(static declaration => declaration.Project, StringComparer.Ordinal)
            .ThenBy(static declaration => declaration.Include, StringComparer.Ordinal)
            .ToArray();
        return new DeclarationInventory(
            Read("SunderRpcBinding"),
            Read("SunderRpcProvider"),
            Read("SunderUsesContract"),
            Read("SunderContractBundle"));
    }

    private static IReadOnlyDictionary<string, string> ReadMetadata(XElement item)
    {
        var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in item.Attributes().Where(static attribute => attribute.Name.LocalName != "Include"))
        {
            metadata[attribute.Name.LocalName] = NormalizeRelativePath(attribute.Value.Trim());
        }
        foreach (var child in item.Elements())
        {
            metadata[child.Name.LocalName] = NormalizeRelativePath(child.Value.Trim());
        }
        return metadata;
    }

    private static IReadOnlyList<ExtensionPointSnapshot> ReadExtensionPoints(string root)
    {
        var path = Path.Combine(root, "src", "Sunder.Package.Agent.Contracts", "buildTransitive", "Sunder.Package.Agent.Protocol.props");
        return AgentSourceMigrationProjectChanges.ReadMsBuildHistoryDocument(root, path)
            .Descendants("SunderContractBundle")
            .Select(item => new ExtensionPointSnapshot(
                item.Attribute("ContractId")?.Value ?? string.Empty,
                item.Attribute("Version")?.Value ?? string.Empty,
                item.Attribute("DescriptorPath")?.Value ?? string.Empty,
                string.Equals(item.Attribute("ContractId")?.Value, "sunder.agent.tool.execution.observed", StringComparison.Ordinal)))
            .OrderBy(static extension => extension.ContractId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ProjectRoleSnapshot> ReadProjectRoles(string root, string historicalWorkerProps)
    {
        var sharedWorkerProps = XDocument.Parse(historicalWorkerProps);
        var workerDefaults = sharedWorkerProps.Descendants("PropertyGroup").Single(group =>
            string.Equals(group.Attribute("Condition")?.Value, "'$(SunderPackageRole)' == 'worker'", StringComparison.Ordinal));
        var workerPrimary = workerDefaults.Elements("SunderPackagePrimaryProject").Single().Value.Trim();
        var workerRuntimeIdentifiers = workerDefaults.Elements("RuntimeIdentifiers").Single().Value.Trim();
        return AgentSourceMigrationProjectChanges.EnumerateMigrationHistoryFiles(root, Path.Combine(root, "src"), "*.csproj")
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Select(path => (Path: path, Project: AgentSourceMigrationProjectChanges.ReadMsBuildHistoryDocument(root, path)))
            .Where(static item => ReadProperty(item.Project, "SunderPackageRole") is not null)
            .Select(item =>
            {
                var role = ReadProperty(item.Project, "SunderPackageRole")!;
                return new ProjectRoleSnapshot(
                    RelativePath(root, item.Path),
                    ReadProperty(item.Project, "SunderPackageFamilyId"),
                    role,
                    ReadProperty(item.Project, "SunderPackagePrimaryProject")
                    ?? (role == "worker" ? workerPrimary : null),
                    NormalizeNullablePath(ReadProperty(item.Project, "SunderPackageAppProject")),
                    SplitValues(ReadProperty(item.Project, "RuntimeIdentifiers")
                                ?? (role == "worker" ? workerRuntimeIdentifiers : null)));
            })
            .OrderBy(static item => item.Project, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<RuntimePackageSnapshot> ReadRuntimePackages(string root)
        => AgentSourceMigrationProjectChanges.CanonicalProjectHistories
            .Select(history =>
            {
                var directory = Path.GetDirectoryName(history.Project)!;
                var metadataPath = Path.Combine(root, directory, "PackageMetadata.cs");
                var project = AgentSourceMigrationProjectChanges.ReadMsBuildHistoryDocument(root, Path.Combine(root, history.Project));
                var app = ReadProperty(project, "SunderPackageAppProject");
                var protocols = AgentSourceMigrationProjectChanges.EnumerateMigrationHistoryFiles(root, Path.Combine(root, directory), "*.csproj")
                    .Where(path => ReadProperty(AgentSourceMigrationProjectChanges.ReadMsBuildHistoryDocument(root, path), "SunderPackageRole") == "protocol")
                    .Select(path => RelativePath(root, path)).Order(StringComparer.Ordinal).ToArray();
                var metadataText = AgentSourceMigrationProjectChanges.ReadPackageMetadataBeforeMigration(metadataPath);
                var packageBody = Regex.Match(
                    metadataText,
                    @"SunderPackage\((?<body>.*?)\)\]",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant).Groups["body"].Value;
                var dependencies = Regex.Matches(
                        metadataText,
                        @"SunderPackageDependency\((?<body>.*?)\)\]",
                        RegexOptions.Singleline | RegexOptions.CultureInvariant)
                    .Select(match => new PackageDependencySnapshot(
                        ReadNamedString(match.Groups["body"].Value, "PackageId"),
                        ReadNamedString(match.Groups["body"].Value, "VersionRange")))
                    .OrderBy(static dependency => dependency.PackageId, StringComparer.Ordinal)
                    .ToArray();
                return new RuntimePackageSnapshot(
                    ReadNamedString(packageBody, "Id"),
                    history.Project,
                    app is null ? null : NormalizeRelativePath(Path.Combine(directory, NormalizePath(app))),
                    protocols,
                    ReadProperty(project, "SunderPackageFamilyId"),
                    ReadProperty(project, "SunderPackageRole"),
                    history.Family != "Agent" ? Hash(Encoding.UTF8.GetBytes(metadataText)) : HashFile(metadataPath),
                    dependencies);
            })
            .OrderBy(static package => package.PackageId, StringComparer.Ordinal)
            .ToArray();

    private static string ReadNamedString(string body, string name)
    {
        var match = Regex.Match(
            body,
            $"\\b{Regex.Escape(name)}\\s*=\\s*\"(?<value>[^\"]+)\"",
            RegexOptions.CultureInvariant);
        return match.Success
            ? match.Groups["value"].Value
            : throw new InvalidDataException($"Package metadata is missing '{name}'.");
    }

    private static ReleaseLockInventory ReadReleaseLocks(string root, IReadOnlyDictionary<string, AgentSourceMigrationProjectChanges.CandidateInputCapture> inputs)
    {
        // The original lock graph is captured byte-for-byte, not compared to the
        // intentionally different canonical NuGet locks maintained by release tooling.
        using var baseline = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "compatibility/agent-rpc-migration-baseline.json")));
        var projects = baseline.RootElement.GetProperty("buildTopology").GetProperty("projects").EnumerateArray()
            .Where(project => project.GetProperty("inSolution").GetBoolean())
            .Select(project => project.GetProperty("path").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedPaths = projects
            .Select(static project => NormalizeRelativePath(
                Path.Combine(Path.GetDirectoryName(project)!, "packages.release.lock.json")))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sourceOnlyTestLocks = projects
            .Where(static project => project.StartsWith("tests/", StringComparison.Ordinal))
            .Where(project =>
            {
                var document = AgentSourceMigrationProjectChanges.ReadMsBuildHistoryDocument(root, Path.Combine(root, NormalizePath(project)));
                return string.Equals(ReadProperty(document, "SunderTestSourceMode"), "true", StringComparison.OrdinalIgnoreCase)
                       && string.Equals(ReadProperty(document, "SunderTestPackageMode"), "false", StringComparison.OrdinalIgnoreCase);
            })
            .Select(static project => NormalizeRelativePath(
                Path.Combine(Path.GetDirectoryName(project)!, "packages.release.lock.json")))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(56, projects.Length);
        Assert.Equal(expectedPaths.Where(path => path.StartsWith("src/", StringComparison.Ordinal)),
            inputs.Keys.Where(path => path.EndsWith("/packages.release.lock.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

        return new ReleaseLockInventory(
            expectedPaths
                .Where(static path => path.StartsWith("src/", StringComparison.Ordinal))
                .Select(path => new FileHashSnapshot(path, inputs[path].Sha256))
                .ToArray(),
            expectedPaths
                .Where(static path => path.StartsWith("tests/", StringComparison.Ordinal))
                .Where(path => !sourceOnlyTestLocks.Contains(path))
                .ToArray());
    }

    private static TextSnapshot ReadTextSnapshot(string root, string relativePath)
    {
        var path = Path.Combine(root, NormalizePath(relativePath));
        var text = AgentSourceMigrationProjectChanges.ReadMigrationSourceText(path);
        using var lines = new StringReader(text);
        var count = 0;
        while (lines.ReadLine() is not null) count++;
        return new TextSnapshot(
            NormalizeRelativePath(relativePath),
            Hash(Encoding.UTF8.GetBytes(text)),
            count);
    }

    private static IReadOnlyList<SqliteSchemaSnapshot> ReadSchemas()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var agent = new AgentLocalStore(scope.Context);
        using var history = new HistorySearchStore(scope.Context);
        using var memory = new MemoryLocalStore(scope.Context);
        return
        [
            CaptureSchema(
                "agent",
                "Sunder.Package.Agent.Storage.AgentLocalStore",
                "agent/agent.db",
                "durable; exact current ledger required",
                AgentLocalStore.BaselineVersion,
                AgentLocalStore.BaselineName,
                AgentLocalStore.BaselineChecksum,
                agent.DatabasePath),
            CaptureSchema(
                "history",
                "Sunder.Package.Agent.HistorySearch.HistorySearchStore",
                "agent/history-search.db",
                "derived projection; confirmed schema corruption recreates explicitly",
                HistorySearchVersions.Schema,
                "history-search-schema",
                null,
                history.DatabasePath),
            CaptureSchema(
                "semantic-memory",
                "Sunder.Package.Agent.Memory.Semantic.MemoryLocalStore",
                "memory/agent-memory.db",
                "durable; exact current migration ledger required",
                MemorySchemaMigrator.CurrentVersion,
                MemorySchemaMigrator.BaselineName,
                MemorySchemaMigrator.BaselineChecksum,
                memory.DatabasePath),
        ];
    }

    private static SqliteSchemaSnapshot CaptureSchema(
        string id,
        string owner,
        string relativePath,
        string policy,
        int version,
        string baselineName,
        string? sourceChecksum,
        string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, tbl_name, sql
            FROM sqlite_schema
            WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name;
            """;
        using var reader = command.ExecuteReader();
        var objects = new List<SqliteObjectSnapshot>();
        while (reader.Read())
        {
            objects.Add(new SqliteObjectSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Regex.Replace(reader.GetString(3), @"\s+", " ", RegexOptions.CultureInvariant).Trim()));
        }

        var normalized = string.Join(
            "\n",
            objects.Select(static item => $"{item.Type}\0{item.Name}\0{item.Table}\0{item.Sql}"));
        return new SqliteSchemaSnapshot(
            id,
            owner,
            relativePath,
            policy,
            version,
            baselineName,
            sourceChecksum,
            Hash(Encoding.UTF8.GetBytes(normalized)),
            objects);
    }

    private static SourceVocabulary ReadSourceVocabulary(string root)
    {
        var sources = AgentSourceMigrationProjectChanges.EnumerateMigrationHistoryFiles(root, Path.Combine(root, "src"), "*.cs")
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            // Reconstruct view intent and constructor moves before reading the
            // frozen logging vocabulary; view ids are not new telemetry events.
            .Select(path => AgentSourceMigrationProjectChanges.BeforeAgentSemanticSeparation(path,
                AgentSourceMigrationProjectChanges.BeforeAgentComposition(path,
                    AgentSourceMigrationProjectChanges.BeforeBuilderRelocation(path,
                        AgentSourceMigrationProjectChanges.ReadMigrationSourceText(path)))))
            .ToArray();
        var configurationKeys = sources
            .SelectMany(source => Regex.Matches(
                source,
                "const\\s+string\\s+\\w*(?:Key|SecretKey)\\s*=\\s*\"(?<value>[^\"]+)\"",
                RegexOptions.CultureInvariant).Select(static match => match.Groups["value"].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var environmentVariables = sources
            .Where(static source => source.Contains("Environment.GetEnvironmentVariable", StringComparison.Ordinal))
            .SelectMany(source => Regex.Matches(
                source,
                "\"(?<value>[A-Z][A-Z0-9_]+)\"",
                RegexOptions.CultureInvariant).Select(static match => match.Groups["value"].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var telemetryVocabulary = sources
            .Where(static source =>
                source.Contains("LogEvent(", StringComparison.Ordinal)
                || source.Contains("LogRunEvent(", StringComparison.Ordinal)
                || source.Contains("LogProviderEventAsync(", StringComparison.Ordinal)
                || source.Contains("IPackageEventLogger", StringComparison.Ordinal)
                || source.Contains("Logging.Events", StringComparison.Ordinal)
                || source.Contains("ProviderStreamTelemetry", StringComparison.Ordinal)
                || source.Contains("CodexAuthTelemetry", StringComparison.Ordinal)
                || source.Contains("PresentationOperation", StringComparison.Ordinal))
            .SelectMany(source => Regex.Matches(
                source,
                "\"(?<value>[a-z][a-z0-9_-]*(?:\\.[a-z0-9_-]+)+)\"",
                RegexOptions.CultureInvariant).Select(static match => match.Groups["value"].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new SourceVocabulary(configurationKeys, environmentVariables, telemetryVocabulary);
    }

    private static string? ReadProperty(XDocument project, string name)
        => project.Descendants(name).Select(static element => element.Value.Trim()).SingleOrDefault();

    private static IReadOnlyList<string> SplitValues(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? NormalizeNullablePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : NormalizeRelativePath(path);

    private static string RelativePath(string root, string path)
        => NormalizeRelativePath(Path.GetRelativePath(root, path));

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string NormalizePath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string HashFile(string path) => Hash(File.ReadAllBytes(path));

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record CompatibilityCandidate(
        int SchemaVersion,
        string ReleaseVersion,
        IReadOnlyList<DescriptorSnapshot> Descriptors,
        DeclarationInventory Declarations,
        IReadOnlyList<ExtensionPointSnapshot> ExtensionPoints,
        string PackageInventorySha256,
        JsonElement PackageInventory,
        IReadOnlyList<RuntimePackageSnapshot> RuntimePackages,
        IReadOnlyList<ProjectRoleSnapshot> ProjectRoles,
        IReadOnlyList<FileHashSnapshot> RuntimeReleaseLocks,
        IReadOnlyList<string> TestRestoreLocks,
        IReadOnlyList<TextSnapshot> PublicApi,
        IReadOnlyList<SqliteSchemaSnapshot> SqliteSchemas,
        IReadOnlyList<JsonDocumentPolicy> JsonDocuments,
        IReadOnlyList<string> ConfigurationKeys,
        IReadOnlyList<string> EnvironmentVariables,
        IReadOnlyList<string> TelemetryVocabulary,
        string ReleaseEvidenceSha256,
        JsonElement ReleaseEvidence);

    private sealed record DescriptorSnapshot(
        string Path,
        string ContractId,
        int DescriptorVersion,
        string Version,
        string SchemaProfile,
        string DescriptorSha256,
        string AbiSha256,
        string Exposure);

    private sealed record DeclarationInventory(
        IReadOnlyList<MsBuildDeclaration> Bindings,
        IReadOnlyList<MsBuildDeclaration> Providers,
        IReadOnlyList<MsBuildDeclaration> Consumers,
        IReadOnlyList<MsBuildDeclaration> Bundles);

    private sealed record MsBuildDeclaration(
        string Project,
        string Include,
        IReadOnlyDictionary<string, string> Metadata);

    private sealed record ExtensionPointSnapshot(
        string ContractId,
        string Version,
        string DescriptorPath,
        bool ReservedFirstParty);

    private sealed record ProjectRoleSnapshot(
        string Project,
        string? FamilyId,
        string Role,
        string? Primary,
        string? AppProject,
        IReadOnlyList<string> RuntimeIdentifiers);

    private sealed record RuntimePackageSnapshot(
        string PackageId,
        string WorkerProject,
        string? AppProject,
        IReadOnlyList<string> ProtocolProjects,
        string? FamilyId,
        string? Role,
        string MetadataSha256,
        IReadOnlyList<PackageDependencySnapshot> Dependencies);

    private sealed record PackageDependencySnapshot(string PackageId, string VersionRange);

    private sealed record FileHashSnapshot(string Path, string Sha256);

    private sealed record ReleaseLockInventory(
        IReadOnlyList<FileHashSnapshot> Runtime,
        IReadOnlyList<string> Tests);

    private sealed record TextSnapshot(string Path, string Sha256, int LineCount);

    private sealed record SqliteSchemaSnapshot(
        string Id,
        string Owner,
        string RelativePath,
        string CompatibilityPolicy,
        int Version,
        string BaselineName,
        string? SourceChecksum,
        string NormalizedSha256,
        IReadOnlyList<SqliteObjectSnapshot> Objects);

    private sealed record SqliteObjectSnapshot(string Type, string Name, string Table, string Sql);

    private sealed record JsonDocumentPolicy(
        string Id,
        string SchemaId,
        int SchemaVersion,
        string ReleasedPredecessor,
        string ReadFailurePolicy);

    private sealed record SourceVocabulary(
        IReadOnlyList<string> ConfigurationKeys,
        IReadOnlyList<string> EnvironmentVariables,
        IReadOnlyList<string> TelemetryVocabulary);
}
