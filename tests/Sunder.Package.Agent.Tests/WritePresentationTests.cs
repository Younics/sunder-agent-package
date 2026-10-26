extern alias AgentApp;

using System.Text.Json;
using Xunit;
using Presentation = AgentApp::Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Tests;

public sealed class WritePresentationTests
{
    [Fact]
    public void PersistedWriteDiffShowsRecordedBeforeAndAfterWithoutReadingCurrentFile()
    {
        var payload = JsonSerializer.Serialize(new
        {
            schema = "younics.ai.agent.tools.files.diff.v1",
            files = new[] { new { path = "Services/Weather.cs", operation = "Update", addedLineCount = 1, deletedLineCount = 1,
                lines = new[] { new { kind = "deleted", lineNumber = 1, text = "old" }, new { kind = "added", lineNumber = 1, text = "new" } } } },
        });
        var diff = Presentation.ToolDiffViewModel.TryCreate("write", "{}", "Wrote 3 characters", null, false, payload);
        Assert.NotNull(diff);
        Assert.Equal("Written changes", diff.SectionTitle);
        var file = Assert.Single(diff.Files);
        Assert.Equal("Services/Weather.cs", file.Path);
        Assert.Contains(file.Lines, line => line.IsDeleted && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.Text == "new");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrFailedPayloadShowsRequestedContentWithoutInventingAppliedDiff(bool failed)
    {
        var args = JsonSerializer.Serialize(new { path = "new.txt", content = "requested\ntext" });
        var diff = Presentation.ToolDiffViewModel.TryCreate("write", args, null, null, failed, "malformed");
        Assert.NotNull(diff);
        Assert.Equal("Requested content", diff.SectionTitle);
        Assert.All(Assert.Single(diff.Files).Lines, line => Assert.False(line.IsAdded || line.IsDeleted));
    }
}
