using System.IO.Compression;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyOwnerProcess(string root, string worker, string archivePath)
    {
        var projected = Path.Combine(worker, "bin/worker-projection");
        Assert.False(File.Exists(Path.Combine(projected, "lib/Sunder.Package.Agent.OwnerProtocol.dll")));
        var workspace = Directory.CreateDirectory(Path.Combine(root, "OwnerProcessWorkspace")).FullName;
        using var archive = ZipFile.OpenRead(archivePath);
        using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var value = manifest.RootElement;
        await using (var candidate = new PackageWorkerPeer(root, projected, workspace, value))
        {
            await candidate.Prepare();
            Assert.Empty(Directory.GetFiles(workspace, "*.db", SearchOption.AllDirectories));
            await candidate.Shutdown();
            Assert.Empty(Directory.GetFiles(workspace, "*.db", SearchOption.AllDirectories));
        }
        string profileId, workspaceId;
        Guid sessionId;
        await using (var active = new PackageWorkerPeer(root, projected, workspace, value))
        {
            await active.Prepare();
            await active.Activate();
            Assert.True(File.Exists(Path.Combine(workspace, "agent/agent.db")));
            var profile = await active.Invoke("younics.ai.agent.agent.profile.provider", "agent-profile", "create-profile", "Native source profile");
            profileId = profile.GetProperty("profileId").GetString()!;
            var createdWorkspace = await active.Invoke("agent.owner.runtime", "agent-runtime", "workspace-command",
                JsonSerializer.SerializeToElement(new AgentWorkspaceCommand(AgentWorkspaceCommandKind.Create, DisplayName: "Native source workspace"),
                    CurrentPrivateRpcDescriptors.OwnerJson));
            workspaceId = createdWorkspace.GetProperty("workspace").GetProperty("workspaceId").GetString()!;
            var session = await active.Invoke("agent.owner.runtime", "agent-runtime", "session-command",
                JsonSerializer.SerializeToElement(new AgentSessionCommand(AgentSessionCommandKind.Create, Title: "Native source session",
                    ProfileId: profileId, WorkspaceId: workspaceId), CurrentPrivateRpcDescriptors.OwnerJson));
            sessionId = session.GetProperty("session").GetProperty("session").GetProperty("sessionId").GetGuid();
            var dashboard = await active.Invoke("agent.owner.runtime", "agent-runtime", "dashboard", new { });
            Assert.Contains(dashboard.GetProperty("profiles").EnumerateArray(), item => item.GetProperty("profileId").GetString() == profileId);
            Assert.Contains(dashboard.GetProperty("workspaces").EnumerateArray(), item => item.GetProperty("workspaceId").GetString() == workspaceId);
            await active.Shutdown();
        }
        await using (var restarted = new PackageWorkerPeer(root, projected, workspace, value))
        {
            await restarted.Prepare();
            await restarted.Activate();
            var dashboard = await restarted.Invoke("agent.owner.runtime", "agent-runtime", "dashboard", new { });
            Assert.Contains(dashboard.GetProperty("profiles").EnumerateArray(), item => item.GetProperty("profileId").GetString() == profileId);
            Assert.Contains(dashboard.GetProperty("workspaces").EnumerateArray(), item => item.GetProperty("workspaceId").GetString() == workspaceId);
            foreach (var compact in new[] { false, true })
            {
                var request = new AgentChatSnapshotRequest(10, profileId, workspaceId, sessionId, CompactTranscript: compact);
                var snapshot = await restarted.Invoke("agent.owner.runtime", "agent-runtime", "chat-snapshot",
                    JsonSerializer.SerializeToElement(request, CurrentPrivateRpcDescriptors.OwnerJson));
                Assert.Equal(sessionId, snapshot.GetProperty("selectedSession").GetProperty("session").GetProperty("sessionId").GetGuid());
                var transcript = snapshot.GetProperty("initialTranscript");
                Assert.Empty(transcript.GetProperty("turns").EnumerateArray());
                Assert.Equal(compact ? JsonValueKind.Object : JsonValueKind.Null, transcript.GetProperty("compact").ValueKind);
                if (compact) Assert.Empty(transcript.GetProperty("compact").GetProperty("entries").EnumerateArray());
            }
            var compactPage = await restarted.Invoke("agent.owner.runtime", "agent-runtime", "compact-transcript-page",
                JsonSerializer.SerializeToElement(new AgentCompactTranscriptRequest(sessionId, AgentCompactTranscriptDirection.Recent, 10),
                    CurrentPrivateRpcDescriptors.OwnerJson));
            Assert.Empty(compactPage.GetProperty("entries").EnumerateArray());
            await restarted.Shutdown();
        }
    }
}
