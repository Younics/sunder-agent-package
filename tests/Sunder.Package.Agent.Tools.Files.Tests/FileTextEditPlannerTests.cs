extern alias FilesWorker;

using FilesWorker::Sunder.Package.Agent.Tools.Files;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class FileTextEditPlannerTests
{
    [Theory]
    [InlineData("same\nsame", "same")]
    [InlineData("aaa", "aa")]
    public void Edit_RejectsAmbiguityIncludingOverlappingMatches(string source, string oldText)
        => Assert.Contains("matches 2", Assert.Throws<InvalidOperationException>(() =>
            FileTextEditPlanner.Edit(source, oldText, "new")).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("\uFEFFone\r\ntwo\r\nend\n", "one\ntwo", "one\nchanged", "\uFEFFone\r\nchanged\r\nend\n")]
    [InlineData("a\r\nb\nc\r\n", "a\nb\nc", "a\nc", "a\r\nc\r\n")]
    [InlineData("a\r\nb\nc", "a\nb\nc", "a\ninsert\nb\nc", "a\r\ninsert\nb\nc")]
    [InlineData("a\r\nb", "a\r\nb", "a\nb", "a\nb")]
    public void Edit_UsesLfMatchingWithoutNormalizingUnchangedBytes(string source, string oldText, string newText, string expected)
        => Assert.Equal(expected, FileTextEditPlanner.Edit(source, oldText, newText).Content);

    [Fact]
    public void Edit_ReplaceAllIsExplicitAndProducesOneDiffPerNonoverlappingReplacement()
    {
        var plan = FileTextEditPlanner.Edit("aaa\r\naaa", "aa", "b", replaceAll: true);
        Assert.Equal("ba\r\nba", plan.Content);
        Assert.Equal([1, 2], plan.Replacements.Select(change => change.StartLine));
        Assert.Throws<InvalidOperationException>(() => FileTextEditPlanner.Edit("a", "a", "a"));
        Assert.Throws<InvalidOperationException>(() => FileTextEditPlanner.Edit("a", "a", new string('x', 4 * 1024 * 1024 + 1)));
    }

    [Theory]
    [InlineData("one\r\ntwo\r\n", "@@\n-two\n+changed\n*** End of File", "one\r\nchanged\r\n")]
    [InlineData("one\ntwo", "@@\n-two\n*** End of File", "one")]
    [InlineData("one\ntwo\n", "@@\n-two\n*** End of File", "one\n")]
    [InlineData("one\ntwo", "@@ one\n+insert", "one\ninsert\ntwo")]
    [InlineData("one", "@@\n+two\n*** End of File", "one\ntwo")]
    [InlineData("one\n", "@@\n+two\n*** End of File", "one\ntwo\n")]
    [InlineData("one\r\n", "@@\n-one\n+one\n\\ No newline at end of file", "one")]
    [InlineData("one", "@@\n-one\n\\ No newline at end of file\n+one", "one\n")]
    public void Patch_PreservesEndingsAndHandlesContextInsertionDeletionAndExplicitEof(string source, string hunk, string expected)
    {
        var operation = Assert.Single(FilePatchParser.Parse("*** Begin Patch\n*** Update File: file.txt\n" + hunk + "\n*** End Patch"));
        Assert.Equal(expected, FileTextEditPlanner.Patch(source, operation.Hunks).Content);
    }

    [Theory]
    [InlineData("@@\n-old\n+new\n*** End Patch\ntrailing")]
    [InlineData("unexpected\n*** End Patch")]
    [InlineData("@@\n*** End Patch")]
    [InlineData("@@\n-old\n+new\n\\ unsupported\n*** End Patch")]
    public void Patch_RejectsMalformedAndTrailingContent(string body)
        => Assert.Throws<InvalidOperationException>(() => FilePatchParser.Parse("*** Begin Patch\n*** Update File: file.txt\n" + body));

    [Fact]
    public void Patch_RequiresUniqueWholeLineMatchesAndRejectsUnanchoredInsertions()
    {
        Assert.Throws<InvalidOperationException>(() => FileTextEditPlanner.Patch("old old", [new("old", "new")]));
        Assert.Throws<InvalidOperationException>(() => FileTextEditPlanner.Patch("old\nold", [new("old", "new")]));
        Assert.Throws<InvalidOperationException>(() => FileTextEditPlanner.Patch("old", [new("", "new", IsInsertion: true)]));
        var move = Assert.Single(FilePatchParser.Parse("*** Begin Patch\n*** Update File: old.txt\n*** Move to: new.txt\n*** End Patch"));
        Assert.Equal("new.txt", move.MoveTo);
    }
}
