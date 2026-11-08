using ChestTracker.Core.Models;
using ChestTracker.Core.Tracking;

namespace ChestTracker.Tests;

public class CounterTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Adding_chest_without_match_auto_starts_one()
    {
        var engine = _db.CreateEngine(_clock);

        var result = engine.AddChestManual(fromHotkey: true);

        Assert.True(result.Applied);
        Assert.NotNull(engine.CurrentMatch);
        Assert.Equal(1, engine.GetSnapshot().MatchCount);
    }

    [Fact]
    public void Adding_chest_without_match_fails_when_auto_start_disabled()
    {
        var engine = _db.CreateEngine(_clock, new TrackingOptions { AutoStartMatch = false });

        var result = engine.AddChestManual(fromHotkey: true);

        Assert.Equal(TrackingResultCode.NoActiveMatch, result.Code);
        Assert.Equal(0, engine.GetSnapshot().LifetimeCount);
    }

    [Fact]
    public void Match_session_and_lifetime_counters_track_independently()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();
        for (var i = 0; i < 3; i++)
        {
            _clock.AdvanceSeconds(10);
            engine.AddChestManual(fromHotkey: true);
        }

        engine.EndMatch();
        engine.StartMatch();
        _clock.AdvanceSeconds(10);
        engine.AddChestManual(fromHotkey: true);

        var s = engine.GetSnapshot();
        Assert.Equal(1, s.MatchCount);
        Assert.Equal(4, s.SessionCount);
        Assert.Equal(4, s.LifetimeCount);
        Assert.Equal(2, s.MatchesPlayed);
        Assert.Equal(2.0, s.AveragePerMatch);
        Assert.Equal(3, s.Record);
    }

    [Fact]
    public void Remove_last_chest_appends_undo_of_latest_add()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();
        var first = engine.AddChestManual(false).Event!;
        var second = engine.AddChestManual(false).Event!;

        var removed = engine.RemoveLastChest(fromHotkey: false);

        Assert.True(removed.Applied);
        Assert.Equal(ChestEventKind.Undo, removed.Event!.Kind);
        Assert.Equal(second.EventId, removed.Event.UndoesEventId);
        Assert.Equal(1, engine.GetSnapshot().MatchCount);

        var again = engine.RemoveLastChest(false);
        Assert.Equal(first.EventId, again.Event!.UndoesEventId);
        Assert.Equal(0, engine.GetSnapshot().MatchCount);
    }

    [Fact]
    public void Remove_never_goes_below_zero()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();

        Assert.Equal(TrackingResultCode.NothingToRemove, engine.RemoveLastChest(false).Code);
        Assert.Equal(0, engine.GetSnapshot().MatchCount);
        Assert.Equal(0, engine.GetSnapshot().LifetimeCount);
    }

    [Fact]
    public void Remove_without_active_match_is_reported()
    {
        var engine = _db.CreateEngine(_clock);
        Assert.Equal(TrackingResultCode.NoActiveMatch, engine.RemoveLastChest(false).Code);
    }

    [Fact]
    public void An_add_event_cannot_be_undone_twice()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();
        var add = engine.AddChestManual(false).Event!;
        Assert.True(engine.RemoveLastChest(false).Applied);

        var duplicateUndo = new ChestEvent
        {
            MatchId = add.MatchId, Timestamp = _clock.UtcNow, Kind = ChestEventKind.Undo, Delta = -1,
            UndoesEventId = add.EventId,
        };

        Assert.False(_db.Repo.InsertEvent(duplicateUndo));
        Assert.Equal(0, engine.GetSnapshot().MatchCount);
    }

    [Fact]
    public void Set_count_writes_correction_and_reset_returns_to_zero()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();
        engine.AddChestManual(false);

        var set = engine.SetCurrentMatchCount(7);
        Assert.Equal(ChestEventKind.Correction, set.Event!.Kind);
        Assert.Equal(6, set.Event.Delta);
        Assert.Equal(7, engine.GetSnapshot().MatchCount);

        engine.ResetCurrentMatch();
        Assert.Equal(0, engine.GetSnapshot().MatchCount);
        Assert.Equal(0, engine.GetSnapshot().LifetimeCount);
    }

    [Fact]
    public void Remove_after_positive_correction_uses_correction_when_no_add_left()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();
        engine.SetCurrentMatchCount(2);

        var removed = engine.RemoveLastChest(false);

        Assert.Equal(ChestEventKind.Correction, removed.Event!.Kind);
        Assert.Equal(1, engine.GetSnapshot().MatchCount);
    }

    [Fact]
    public void Editing_past_match_changes_lifetime_but_not_current_match()
    {
        var engine = _db.CreateEngine(_clock);
        var past = engine.StartMatch();
        engine.AddChestManual(false);
        engine.EndMatch();
        engine.StartMatch();
        engine.AddChestManual(false);

        engine.SetMatchCount(past.Id, 5);

        var s = engine.GetSnapshot();
        Assert.Equal(1, s.MatchCount);
        Assert.Equal(6, s.LifetimeCount);
        Assert.Equal(5, s.Record);
    }

    [Fact]
    public void Lifetime_adjustment_does_not_affect_match_average()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();
        engine.AddChestManual(false);
        engine.AddChestManual(false);
        engine.EndMatch();

        engine.AdjustLifetime(100);

        var s = engine.GetSnapshot();
        Assert.Equal(102, s.LifetimeCount);
        Assert.Equal(2.0, s.AveragePerMatch);
        Assert.Equal(1, s.MatchesPlayed);
    }

    [Fact]
    public void Deleting_current_match_clears_it_and_its_events()
    {
        var engine = _db.CreateEngine(_clock);
        var match = engine.StartMatch();
        engine.AddChestManual(false);

        Assert.True(engine.DeleteMatch(match.Id));

        Assert.Null(engine.CurrentMatch);
        Assert.Empty(_db.Repo.GetEventsForMatch(match.Id));
        Assert.Equal(0, engine.GetSnapshot().LifetimeCount);
    }

    [Fact]
    public void Starting_a_match_ends_the_previous_one()
    {
        var engine = _db.CreateEngine(_clock);
        var first = engine.StartMatch();
        _clock.AdvanceSeconds(60);
        engine.StartMatch();

        var stored = _db.Repo.GetMatch(first.Id)!;
        Assert.Equal(_clock.UtcNow, stored.EndedAt);
    }

    [Fact]
    public void Mode_change_mid_match_marks_match_hybrid()
    {
        var engine = _db.CreateEngine(_clock);
        var match = engine.StartMatch();
        engine.AddChestManual(false);

        engine.SetMode(TrackingMode.Automatic);

        Assert.Equal(TrackingMode.Hybrid, _db.Repo.GetMatch(match.Id)!.Mode);
    }

    [Fact]
    public void Accepting_review_counts_chest_and_rejecting_does_not()
    {
        var engine = _db.CreateEngine(_clock, mode: TrackingMode.Hybrid);
        engine.StartMatch();
        var a = engine.AddPendingReview(0.5);
        var b = engine.AddPendingReview(0.4);

        Assert.True(engine.ResolveReview(a.Id, accept: true).Applied);
        Assert.True(engine.ResolveReview(b.Id, accept: false).Applied);
        Assert.Equal(TrackingResultCode.NotFound, engine.ResolveReview(a.Id, accept: true).Code);

        var s = engine.GetSnapshot();
        Assert.Equal(1, s.MatchCount);
        Assert.Equal(0, s.PendingReviews);
    }

    [Fact]
    public void Changed_event_carries_fresh_snapshot()
    {
        var engine = _db.CreateEngine(_clock);
        TrackingChangedEventArgs? last = null;
        engine.Changed += (_, e) => last = e;

        engine.AddChestManual(false);

        Assert.NotNull(last);
        Assert.Equal(TrackingChangeKind.ChestAdded, last!.Kind);
        Assert.Equal(1, last.Snapshot.MatchCount);
    }

    [Fact]
    public void Concurrent_adds_are_all_counted()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();

        Parallel.For(0, 50, _ => engine.AddChestManual(fromHotkey: false));

        Assert.Equal(50, engine.GetSnapshot().MatchCount);
    }
}
