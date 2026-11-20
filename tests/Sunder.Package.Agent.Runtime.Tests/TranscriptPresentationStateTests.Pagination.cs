extern alias AgentCore;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using Xunit;
using Presentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Tests;

public sealed partial class TranscriptPresentationStateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PageTrimCannotAdvanceItsCursorPastFetchedButEvictedRows(bool older)
    {
        using var timeline = CreateTimeline(60, 30, 60);
        var session = Guid.NewGuid();
        var turns = CreateTurns(session, 0, 90);
        var protectedKey = Presentation.TranscriptRowAnchorKey.Text(turns[older ? 85 : 5].TurnId);
        Assert.True(timeline.TryCompleteAnchoredInitialLoad(timeline.BeginAnchoredInitialLoad(session),
            older ? turns[30..] : turns[..60], older, !older, protectedKey));
        var authority = new Presentation.TranscriptPageAnchorAuthority(protectedKey, () => protectedKey);
        if (older)
            Assert.True(await timeline.LoadOlderAsync((_, _, _, _, _) => Task.FromResult(new Presentation.TranscriptTurnPage(turns[..30], false)), authority));
        else
            await timeline.LoadNewerAsync((_, _, _, _, _) => Task.FromResult(new Presentation.TranscriptTurnPage(turns[60..], false)), authority);

        var retained = timeline.Projector.TurnWindow.OrderedTurns();
        var boundary = older ? retained[0] : retained[^1];
        var expectedCursor = boundary.TurnId;
        Guid? actualCursor = null;
        Task<Presentation.TranscriptTurnPage> Read(Guid _, DateTimeOffset __, Guid cursor, int ___, CancellationToken ____)
        {
            actualCursor = cursor;
            return Task.FromResult(new Presentation.TranscriptTurnPage([], false));
        }
        if (older) await timeline.LoadOlderAsync(Read, authority);
        else await timeline.LoadNewerAsync(Read, authority);
        Assert.Equal(expectedCursor, actualCursor);
        Assert.Contains(timeline.Projector.Rows, row => Equals(row.AnchorKey, protectedKey));
    }
}
