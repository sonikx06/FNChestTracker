using ChestTracker.Core.Data;
using ChestTracker.Core.Models;
using ChestTracker.Core.Statistics;
using ChestTracker.Core.Tracking;

namespace ChestTracker.Tests;

/// <summary>Statistics audit, local side: every screen derives from the same event log and agrees.</summary>
public class LocalStatisticsAuditTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _db.Dispose();

    private TrackingEngine Engine() => _db.CreateEngine(_clock, new TrackingOptions { ManualDebounce = TimeSpan.Zero });

    private void Chests(TrackingEngine e, int n)
    {
        for (var i = 0; i < n; i++)
        {
            _clock.AdvanceSeconds(5);
            Assert.True(e.AddChestManual(fromHotkey: true).Applied);
        }
    }

    /// <summary>Dashboard/overlay (snapshot) vs Statistics page (summaries) vs raw event log.</summary>
    private void AssertScreensAgree(TrackingEngine e, int expectedLifetime, int expectedMatches)
    {
        var snapshot = e.GetSnapshot();
        var summaries = _db.Repo.GetMatchSummaries();
        var bests = StatisticsCalculator.Bests(summaries, TimeZoneInfo.Utc, _clock.UtcNow);
        var adjustments = _db.Repo.GetLifetimeTotal() - summaries.Sum(s => s.Chests);

        Assert.Equal(expectedLifetime, snapshot.LifetimeCount);
        Assert.Equal(expectedMatches, snapshot.MatchesPlayed);
        Assert.Equal(snapshot.MatchesPlayed, bests.TotalMatches);
        Assert.Equal(snapshot.LifetimeCount, bests.TotalChests + adjustments);
        Assert.Equal(snapshot.AveragePerMatch, bests.AveragePerMatch, 6);
        Assert.Equal(snapshot.Record, summaries.Select(s => s.Chests).DefaultIfEmpty(0).Max());
        if (snapshot.CurrentMatch is { } m)
        {
            Assert.Equal(snapshot.MatchCount, summaries.Single(s => s.Match.Id == m.Id).Chests);
        }
    }

    [Fact]
    public void All_screens_agree_through_matches_undo_corrections_edits_and_deletes()
    {
        var e = Engine();
        var first = e.StartMatch();
        Chests(e, 5);
        e.RemoveLastChest(fromHotkey: false);
        e.EndMatch();
        AssertScreensAgree(e, 4, 1);

        var second = e.StartMatch();
        Chests(e, 3);
        AssertScreensAgree(e, 7, 2);

        e.SetMatchCount(first.Id, 10);       // historical correction
        AssertScreensAgree(e, 13, 2);

        e.AdjustLifetime(50);                // chests from before tracking
        AssertScreensAgree(e, 63, 2);

        e.EndMatch();
        e.StartMatch();
        Chests(e, 2);
        e.DeleteMatch(second.Id);
        AssertScreensAgree(e, 62, 2);

        e.Shutdown();
        _db.Reopen();
        AssertScreensAgree(Engine(), 62, 2);
    }

    [Fact]
    public void Duplicate_event_ids_never_count_twice()
    {
        var e = Engine();
        e.StartMatch();
        Chests(e, 1);
        var ev = _db.Repo.GetRecentEvents(1).Single();

        Assert.False(_db.Repo.InsertEvent(ev)); // same id again (e.g. a replayed import)
        Assert.Equal(1, e.GetSnapshot().LifetimeCount);
    }

    [Fact]
    public void Switching_between_solo_and_party_never_changes_personal_statistics()
    {
        var e = Engine();
        var outbox = new SyncOutbox(_db.Db);
        e.StartMatch();
        Chests(e, 4);                         // solo
        var before = e.GetSnapshot();

        outbox.BeginParty(Guid.NewGuid());    // join a party
        outbox.LinkMatch(e.StartMatch().Id, Guid.NewGuid()); // shared match
        Chests(e, 2);
        outbox.EnqueueNew();
        outbox.EndParty();                    // leave: queued events dropped, local data kept
        outbox.BeginParty(Guid.NewGuid());    // another party

        var after = e.GetSnapshot();
        Assert.Equal(before.LifetimeCount + 2, after.LifetimeCount);
        Assert.Equal(2, after.MatchCount);
        Assert.Equal(2, after.MatchesPlayed);
        AssertScreensAgree(e, 6, 2);
    }
}
