using System.Text.RegularExpressions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    private const string OwnedPublicPrefix = "src/Sunder.Package.Agent/Rpc/Public/";
    private const string OwnedImplementationPrefix = "src/Sunder.Package.Agent/Services/";
    private const string ChildImplementationHash = "0fbf8960f2ca8aedb253c8355ee500887d957d5d19410483bfb08276437c6199";

    // These declarations are noncompiled before-images from the implementation-first
    // cleanup. Existing migration snapshots and their original hashes stay immutable.
    private static class OwnedPublicServiceHistory
    {
        internal static readonly IReadOnlyDictionary<string, (string Implementation, string DeclarationHash)> Declarations =
            new Dictionary<string, (string, string)>(StringComparer.Ordinal)
            {
                [OwnedPublicPrefix + "RuntimeCatalog.cs"] = ("AgentRuntimeCatalog.cs", "305b6d612d91ee5eb56afbec81991c0a4bd8068647f3b95abb66eb646bd14a2d"),
                [OwnedPublicPrefix + "RunControl.cs"] = ("AgentRunControlService.cs", "0f3bbdba6665ec700f725b09b0b98a683a37a756351658da8119237f3b60a269"),
                [OwnedPublicPrefix + "WorkspaceExecutionResolver.cs"] = ("AgentWorkspaceExecutionResolver.cs", "7cc06dd68b9f694f26c30f76f01709af62ecf4e180210a75972869230f2085fc"),
                [OwnedPublicPrefix + "ChildRunExecutor.cs"] = ("AgentChildRunExecutor.cs", "19a57356e5439ec06cb8ba45cb3261d3c4de0cc8537cd64d46ddc18b3cc866fa"),
            };
    }

    private static string ReadOwnedPublicHistoryInput(string root, string path)
        => OwnedPublicServiceHistory.Declarations.ContainsKey(Path.GetRelativePath(root, path).Replace('\\', '/'))
            ? string.Empty : ReadNormalizedProjectText(path);

    private static string BeforeOwnedPublicServices(string relative, string current)
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        if (OwnedPublicServiceHistory.Declarations.TryGetValue(relative, out var declaration))
        {
            Assert.False(File.Exists(Path.Combine(root, relative)), $"Removed owned-service declaration was reintroduced: {relative}");
            var implementationPath = OwnedImplementationPrefix + declaration.Implementation;
            if (declaration.Implementation == "AgentChildRunExecutor.cs")
                AssertProjectHash(ChildImplementationHash, ReadNormalizedProjectText(Path.Combine(root, implementationPath)), implementationPath);
            else _ = BeforeOwnedPublicServices(implementationPath, ReadNormalizedProjectText(Path.Combine(root, implementationPath)));
            var before = ReadNormalizedProjectText(Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/OwnedPublicServices", Path.GetFileName(relative) + ".txt"));
            AssertProjectHash(declaration.DeclarationHash, before, relative + " (declaration before-image)");
            if (current.Length != 0) AssertProjectHash(declaration.DeclarationHash, current, relative);
            return before;
        }

        var identity = relative switch
        {
            OwnedImplementationPrefix + "AgentRuntimeCatalog.cs" => ("RuntimeCatalogService", "agent.runtime.catalog",
                "9a61e3f7dabc8aea8df36e7d9f4600befab70c49d4944bb2d95cf49871ec1bc0", "00cec94a6f2bb45d5928ac9ec6e644b6da6f9edc513e85de8dfd7ff82fa99918"),
            OwnedImplementationPrefix + "AgentRunControlService.cs" => ("RunControlService", "agent.run.control",
                "5f6a1bee2f907461df731e5f8122829a00d200598e71480b6655095c3d60fd71", "f226d7b5e7290c6712dfbc0b785c0f8631d93c26ac7e95fe037add7965f8e4ed"),
            OwnedImplementationPrefix + "AgentWorkspaceExecutionResolver.cs" => ("WorkspaceExecutionResolverService", "agent.workspace.execution.resolver",
                "2338e7f3ee0181cd715b903b2aa4a11f75e3b7335f3e2aea1a4f9ff9468ededa", "0608f035f30e3730715f5e95a94087a720fd93826b282dc84b62e744b1b14335"),
            _ => default,
        };
        if (identity.Item1 is not null)
        {
            AssertProjectHash(identity.Item3, current, relative + " (implementation-first source)");
            var start = current.IndexOf("[SunderService(", StringComparison.Ordinal);
            var type = current.IndexOf("public sealed partial class", start, StringComparison.Ordinal);
            current = current[..start] + $"[SunderServiceProvider(typeof(Rpc.Public.{identity.Item1}), ProviderId = \"{identity.Item2}\")]\n" + current[type..];
            current = Regex.Replace(current, "^    \\[SunderMethod[^\\n]+\\]\\n", "", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            current = Regex.Replace(current, ",\\n        \\[SunderValueConstraints\\(Minimum = \"1\", Maximum = \"(?:100|1000)\"\\)\\] int limit", ", int limit", RegexOptions.CultureInvariant);
            current = current.Replace(")\n{\n", $") : Rpc.Public.{identity.Item1}\n{{\n", StringComparison.Ordinal);
            if (identity.Item1 == "RunControlService")
                current = current.Replace("    public ValueTask LogEventAsync(Guid invocationId,\n"
                    + "        [SunderValueConstraints(AllowedStrings = [\"trace\", \"debug\", \"information\", \"warning\", \"error\", \"critical\"])] string level,\n"
                    + "        string eventName, string message, [SunderValueConstraints(Minimum = \"0\")] long? elapsedMilliseconds,\n",
                    "    public ValueTask LogEventAsync(Guid invocationId, string level, string eventName, string message, long? elapsedMilliseconds,\n", StringComparison.Ordinal);
            AssertProjectHash(identity.Item4, current, relative + " (authenticated preceding implementation)");
            return current;
        }
        if (relative != OwnedImplementationPrefix + "AgentRunCoordinator.cs") return current;
        AssertProjectHash("25f3a8a38c333854fa4ab1034adda7cac4e7ec4ba241304215d9a87379a5822c", current, relative);
        AssertProjectHash(ChildImplementationHash, ReadNormalizedProjectText(Path.Combine(root, OwnedImplementationPrefix + "AgentChildRunExecutor.cs")), "Moved child-run implementation");
        current = current.Replace("using Sunder.Package.Agent.Runtime;\n", "using Sunder.Package.Agent.Runtime;\nusing Sunder.Sdk.Services;\n", StringComparison.Ordinal)
            .Replace("public sealed partial class AgentRunCoordinator(", "[SunderServiceProvider(typeof(Rpc.Public.ChildRunExecutorService), ProviderId = \"agent.child.runs\")]\npublic sealed partial class AgentRunCoordinator(", StringComparison.Ordinal)
            .Replace("AgentChildRunExecutor childRunExecutor", "AgentChildRunSessionService childRunSessionService", StringComparison.Ordinal)
            .Replace("    : IAgentChildRunExecutor,\n", "    : IAgentChildRunExecutor,\n      Rpc.Public.ChildRunExecutorService,\n", StringComparison.Ordinal)
            .Replace("private readonly AgentChildRunExecutor _childRunExecutor = childRunExecutor;", "private readonly AgentChildRunSessionService _childRunSessionService = childRunSessionService;", StringComparison.Ordinal)
            .Replace("public ValueTask<AgentChildRunResult> RunChildAsync(", "public async ValueTask<AgentChildRunResult> RunChildAsync(", StringComparison.Ordinal)
            .Replace("        => _childRunExecutor.RunChildAsync(request, cancellationToken);", """
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var childRunSession = _childRunSessionService.PrepareChildSession(request);
                        var checkpoint = await QueueUserMessageAsync(
                            childRunSession.ChildSession.SessionId,
                            childRunSession.ChildProfile.ProfileId,
                            request.UserMessage,
                            request.WorkspaceId,
                            cancellationToken);
                        return _childRunSessionService.BuildResult(childRunSession.ChildSession, checkpoint);
                    }
                """, StringComparison.Ordinal);
        AssertProjectHash("fdd23e767e5bbfd4141f9445ce401d320e36ad27792fd42ac3aee86fe5136212", current, relative + " (authenticated preceding coordinator)");
        return current;
    }

    private static bool IsOwnedPublicServiceAddition(string root, string path)
    {
        if (Path.GetRelativePath(root, path).Replace('\\', '/') != OwnedImplementationPrefix + "AgentChildRunExecutor.cs") return false;
        AssertProjectHash(ChildImplementationHash, ReadNormalizedProjectText(path), path);
        return true;
    }

    private static string CurrentOwnedPublicReplacement(string path)
        => OwnedPublicServiceHistory.Declarations.TryGetValue(path, out var declaration)
            ? OwnedImplementationPrefix + declaration.Implementation : path;

    private static IEnumerable<string> EnumerateOwnedPublicDeclarationHistory(string root, string directory, string pattern)
    {
        var prefix = Path.GetRelativePath(root, directory).Replace('\\', '/').TrimEnd('/') + "/";
        return OwnedPublicServiceHistory.Declarations.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)
            && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path))).Select(path => Path.Combine(root, path));
    }
}
