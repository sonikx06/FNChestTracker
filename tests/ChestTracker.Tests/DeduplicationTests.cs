using ChestTracker.Core.Models;
using ChestTracker.Core.Tracking;

namespace ChestTracker.Tests;

public class DeduplicationTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Hotkey_double_press_within_debounce_is_ignored()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();

        Assert.True(engine.AddChestManual(fromHotkey: true).Applied);
        _clock.Advance(TimeSpan.FromMilliseconds(120));
        Assert.Equal(TrackingResultCode.IgnoredDebounce, engine.AddChestManual(fromHotkey: true).Code);
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(engine.AddChestManual(fromHotkey: true).Applied);

        Assert.Equal(2, engine.GetSnapshot().MatchCount);
    }

    [Fact]
    public void Ui_clicks_are_never_debounced()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();

        engine.AddChestManual(fromHotkey: false);
        engine.AddChestManual(fromHotkey: false);
        engine.AddChestManual(fromHotkey: false);

        Assert.Equal(3, engine.GetSnapshot().MatchCount);
    }

    [Fact]
    public void Remove_hotkey_is_debounced()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();
        engine.AddChestManual(false);
        engine.AddChestManual(false);

        Assert.True(engine.RemoveLastChest(fromHotkey: true).Applied);
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(TrackingResultCode.IgnoredDebounce, engine.RemoveLastChest(fromHotkey: true).Code);

        Assert.Equal(1, engine.GetSnapshot().MatchCount);
    }

    [Fact]
    public void Automatic_detections_respect_cooldown()
    {
        var engine = _db.CreateEngine(_clock, new TrackingOptions { AutomaticCooldown = TimeSpan.FromSeconds(4) },
            TrackingMode.Automatic);
        engine.StartMatch();

        Assert.True(engine.AddChestAutomatic(0.9).Applied);
        _clock.AdvanceSeconds(1.5);
        Assert.Equal(TrackingResultCode.IgnoredCooldown, engine.AddChestAutomatic(0.9).Code);
        _clock.AdvanceSeconds(3);
        Assert.True(engine.AddChestAutomatic(0.9).Applied);

        Assert.Equal(2, engine.GetSnapshot().MatchCount);
    }

    [Fact]
    public void Hybrid_manual_press_right_after_detection_is_merged_once()
    {
        var engine = _db.CreateEngine(_clock, mode: TrackingMode.Hybrid);
        engine.StartMatch();

        engine.AddChestAutomatic(0.8);
        _clock.AdvanceSeconds(1);
        Assert.Equal(TrackingResultCode.IgnoredDuplicate, engine.AddChestManual(fromHotkey: true).Code);

        // A second press is a real second chest.
        _clock.AdvanceSeconds(0.5);
        Assert.True(engine.AddChestManual(fromHotkey: true).Applied);

        Assert.Equal(2, engine.GetSnapshot().MatchCount);
    }

    [Fact]
    public void Hybrid_detection_right_after_manual_press_is_merged()
    {
        var engine = _db.CreateEngine(_clock, mode: TrackingMode.Hybrid);
        engine.StartMatch();

        engine.AddChestManual(fromHotkey: true);
        _clock.AdvanceSeconds(1);
        Assert.Equal(TrackingResultCode.IgnoredDuplicate, engine.AddChestAutomatic(0.9).Code);

        // The merge started the automatic cooldown, so the tail of the same animation is ignored too.
        _clock.AdvanceSeconds(1);
        Assert.Equal(TrackingResultCode.IgnoredCooldown, engine.AddChestAutomatic(0.9).Code);

        Assert.Equal(1, engine.GetSnapshot().MatchCount);
    }

    [Fact]
    public void Manual_mode_does_not_merge_with_old_automatic_events()
    {
        var engine = _db.CreateEngine(_clock, mode: TrackingMode.Hybrid);
        engine.StartMatch();
        engine.AddChestAutomatic(0.9);
        engine.SetMode(TrackingMode.Manual);
        _clock.AdvanceSeconds(0.5);

        Assert.True(engine.AddChestManual(fromHotkey: true).Applied);
    }

    [Fact]
    public void Detection_outside_merge_window_counts()
    {
        var engine = _db.CreateEngine(_clock, mode: TrackingMode.Hybrid);
        engine.StartMatch();

        engine.AddChestManual(fromHotkey: true);
        _clock.AdvanceSeconds(5);

        Assert.True(engine.AddChestAutomatic(0.9).Applied);
        Assert.Equal(2, engine.GetSnapshot().MatchCount);
    }

    [Fact]
    public void Clock_going_backwards_does_not_suppress_events()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();
        engine.AddChestManual(fromHotkey: true);
        _clock.AdvanceSeconds(-30);

        Assert.True(engine.AddChestManual(fromHotkey: true).Applied);
    }

    [Fact]
    public void Same_event_id_is_stored_only_once()
    {
        var engine = _db.CreateEngine(_clock);
        var match = engine.StartMatch();
        var ev = new ChestEvent { MatchId = match.Id, Timestamp = _clock.UtcNow, Kind = ChestEventKind.Add, Delta = 1 };

        Assert.True(_db.Repo.InsertEvent(ev));
        Assert.False(_db.Repo.InsertEvent(ev));
        Assert.Equal(1, _db.Repo.GetMatchTotal(match.Id));
    }

    [Fact]
    public void New_match_resets_dedup_windows()
    {
        var engine = _db.CreateEngine(_clock);
        engine.AddChestManual(fromHotkey: true);
        engine.StartMatch();

        Assert.True(engine.AddChestManual(fromHotkey: true).Applied);
    }
}
