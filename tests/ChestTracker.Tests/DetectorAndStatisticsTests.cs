using ChestTracker.Core.Detection;
using ChestTracker.Core.Models;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Statistics;

namespace ChestTracker.Tests;

public class DetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FrameFeatures Frame(double t, double gold, double bright = 80, double motion = 0.01) =>
        new(T0.AddSeconds(t), gold, bright, motion, null);

    private static List<DetectionDecision> Run(ChestDetector d, IEnumerable<FrameFeatures> frames) =>
        frames.Select(f => d.Process(f)).Where(x => x is not null).Select(x => x!).ToList();

    private static IEnumerable<FrameFeatures> Quiet(double from, double to, double step = 0.25)
    {
        for (var t = from; t < to; t += step)
        {
            yield return Frame(t, 0.01);
        }
    }

    [Fact]
    public void Golden_burst_is_confirmed_once()
    {
        var d = new ChestDetector(DetectionTuning.FromSensitivity(50, 4, 4));
        var frames = Quiet(0, 2)
            .Concat(new[] { Frame(2.0, 0.08, 120, 0.1), Frame(2.25, 0.09, 125, 0.12), Frame(2.5, 0.07, 110, 0.08) })
            .Concat(Quiet(2.75, 4));

        var decisions = Run(d, frames);

        var single = Assert.Single(decisions);
        Assert.Equal(DetectionOutcome.Confirmed, single.Outcome);
        Assert.Equal(T0.AddSeconds(2), single.Timestamp);
    }

    [Fact]
    public void Second_burst_inside_cooldown_is_ignored()
    {
        var d = new ChestDetector(DetectionTuning.FromSensitivity(50, 4, 4));
        var burst = (double t) => new[] { Frame(t, 0.08, 120, 0.1), Frame(t + 0.25, 0.09, 125, 0.1) };
        var frames = Quiet(0, 2).Concat(burst(2)).Concat(Quiet(2.5, 3)).Concat(burst(3)).Concat(Quiet(3.5, 7))
            .Concat(burst(7)).Concat(Quiet(7.5, 8));

        var decisions = Run(d, frames).Where(x => x.Outcome != DetectionOutcome.Discarded).ToList();

        Assert.Equal(2, decisions.Count);
        Assert.Equal(T0.AddSeconds(7), decisions[1].Timestamp);
    }

    [Fact]
    public void Bright_flash_without_gold_is_not_a_chest()
    {
        // Ammo boxes / explosions: bright and moving, but not golden.
        var d = new ChestDetector(DetectionTuning.FromSensitivity(100, 4, 4));
        var frames = Quiet(0, 2)
            .Concat(new[] { Frame(2, 0.012, 200, 0.3), Frame(2.25, 0.012, 210, 0.3), Frame(2.5, 0.011, 190, 0.3) })
            .Concat(Quiet(2.75, 4));

        Assert.DoesNotContain(Run(d, frames), x => x.Outcome != DetectionOutcome.Discarded);
    }

    [Fact]
    public void Weak_gold_signal_goes_to_review()
    {
        var d = new ChestDetector(DetectionTuning.FromSensitivity(50, 4, 4));
        var frames = Quiet(0, 2)
            .Concat(new[] { Frame(2, 0.05, 85), Frame(2.25, 0.055, 85), Frame(2.5, 0.05, 85) })
            .Concat(Quiet(2.75, 4));

        var decision = Assert.Single(Run(d, frames));
        Assert.Equal(DetectionOutcome.Uncertain, decision.Outcome);
    }

    [Fact]
    public void Single_frame_spike_is_discarded_at_normal_sampling()
    {
        var d = new ChestDetector(DetectionTuning.FromSensitivity(50, 4, 4));
        var frames = Quiet(0, 2).Append(Frame(2, 0.1, 130, 0.1)).Concat(Quiet(2.25, 3));

        Assert.All(Run(d, frames), x => Assert.Equal(DetectionOutcome.Discarded, x.Outcome));
    }

    [Fact]
    public void Baseline_adapts_to_golden_scenery()
    {
        // Standing next to something gold for a long time must not keep firing.
        var d = new ChestDetector(DetectionTuning.FromSensitivity(50, 1, 4));
        var frames = Enumerable.Range(0, 200).Select(i => Frame(i * 0.25, 0.08, 100));

        var counted = Run(d, frames).Count(x => x.Outcome == DetectionOutcome.Confirmed);

        Assert.True(counted <= 1, $"counted {counted}");
    }

    [Fact]
    public void Higher_sensitivity_lowers_thresholds()
    {
        var strict = DetectionTuning.FromSensitivity(0, 4, 4);
        var lenient = DetectionTuning.FromSensitivity(100, 4, 4);

        Assert.True(lenient.ConfirmThreshold < strict.ConfirmThreshold);
        Assert.True(lenient.ReviewThreshold < lenient.ConfirmThreshold);
    }
}

public class StatisticsTests
{
    private static MatchSummary M(DateTimeOffset start, int chests, Guid? session = null, TrackingMode mode = TrackingMode.Manual) => new()
    {
        Match = new MatchRecord
        {
            StartedAt = start, EndedAt = start.AddMinutes(20), SessionId = session ?? Guid.Empty, Mode = mode,
        },
        Chests = chests,
    };

    private static readonly DateTimeOffset Day1 = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Per_day_groups_and_fills_gaps()
    {
        var matches = new[] { M(Day1, 3), M(Day1.AddHours(2), 4), M(Day1.AddDays(2), 5) };

        var days = StatisticsCalculator.PerDay(matches, TimeZoneInfo.Utc);

        Assert.Equal(3, days.Count);
        Assert.Equal(7, days[0].Chests);
        Assert.Equal(0, days[1].Chests);
        Assert.Equal(5, days[2].Chests);
    }

    [Fact]
    public void Per_session_sums_matches()
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var matches = new[] { M(Day1, 2, s1), M(Day1.AddMinutes(30), 3, s1), M(Day1.AddDays(1), 9, s2) };

        var sessions = StatisticsCalculator.PerSession(matches);

        Assert.Equal(new[] { 5, 9 }, sessions.Select(s => s.Chests));
    }

    [Fact]
    public void Cumulative_and_rolling_average()
    {
        var matches = new[] { M(Day1, 2), M(Day1.AddHours(1), 4), M(Day1.AddHours(2), 6) };

        Assert.Equal(new[] { 2.0, 3.0, 4.0 }, StatisticsCalculator.CumulativeAverage(matches));
        Assert.Equal(new[] { 2.0, 3.0, 5.0 }, StatisticsCalculator.RollingAverage(new[] { 2, 4, 6 }, 2));
    }

    [Fact]
    public void Personal_bests()
    {
        var s1 = Guid.NewGuid();
        var matches = new[] { M(Day1, 2, s1), M(Day1.AddHours(1), 8, s1), M(Day1.AddDays(1), 5) };

        var bests = StatisticsCalculator.Bests(matches, TimeZoneInfo.Utc, Day1.AddDays(2));

        Assert.Equal(8, bests.BestMatch!.Chests);
        Assert.Equal(10, bests.BestDay!.Chests);
        Assert.Equal(10, bests.BestSession!.Chests);
        Assert.Equal(5.0, bests.AveragePerMatch);
        Assert.Equal(8 / 20.0, bests.BestChestsPerMinute, 3);
    }

    [Fact]
    public void Filter_by_period_mode_and_min()
    {
        var now = Day1.AddDays(10);
        var matches = new[]
        {
            M(Day1, 2), M(now.AddHours(-1), 6, mode: TrackingMode.Hybrid), M(now.AddDays(-3), 1),
        };

        Assert.Single(StatisticsCalculator.Filter(matches, new MatchFilter { Period = MatchPeriod.Today }, now, TimeZoneInfo.Utc));
        Assert.Equal(2, StatisticsCalculator.Filter(matches, new MatchFilter { Period = MatchPeriod.Last7Days }, now, TimeZoneInfo.Utc).Count);
        Assert.Single(StatisticsCalculator.Filter(matches, new MatchFilter { Mode = TrackingMode.Hybrid }, now, TimeZoneInfo.Utc));
        Assert.Equal(2, StatisticsCalculator.Filter(matches, new MatchFilter { MinChests = 2 }, now, TimeZoneInfo.Utc).Count);
        Assert.Single(StatisticsCalculator.Filter(matches, new MatchFilter { Search = "2026-05-01" }, now, TimeZoneInfo.Utc));
    }
}

public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chesttracker-settings", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Defaults_are_russian_manual_with_f_key_hotkeys()
    {
        var s = new SettingsStore(Path.Combine(_dir, "settings.json")).Load();

        Assert.Equal("ru", s.Language);
        Assert.Equal(TrackingMode.Manual, s.TrackingMode);
        Assert.Equal(0x77, s.Hotkeys.Get(HotkeyAction.AddChest).VirtualKey);
        Assert.Equal(0x76, s.Hotkeys.Get(HotkeyAction.RemoveLastChest).VirtualKey);
        Assert.Equal(0x78, s.Hotkeys.Get(HotkeyAction.StartMatch).VirtualKey);
        Assert.Equal(0x79, s.Hotkeys.Get(HotkeyAction.EndMatch).VirtualKey);
    }

    [Fact]
    public void Save_and_load_roundtrip()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var s = store.Load();
        s.Language = "en";
        s.Overlay.Left = 123;
        s.Overlay.Layout = OverlayLayout.Expanded;
        s.Hotkeys.Get(HotkeyAction.AddChest).Modifiers = HotkeyModifiers.Control;
        store.Save(s);

        var loaded = store.Load();

        Assert.Equal("en", loaded.Language);
        Assert.Equal(123, loaded.Overlay.Left);
        Assert.Equal(OverlayLayout.Expanded, loaded.Overlay.Layout);
        Assert.Equal(HotkeyModifiers.Control, loaded.Hotkeys.Get(HotkeyAction.AddChest).Modifiers);
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, "{ not json");

        var s = new SettingsStore(file).Load();

        Assert.Equal("ru", s.Language);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.bad-*"));
    }

    [Fact]
    public void Normalize_clamps_out_of_range_values()
    {
        var s = new AppSettings { Language = "de" };
        s.Overlay.Scale = 50;
        s.Overlay.TextColor = "nope";
        s.Detection.SamplingFps = 0;

        s.Normalize();

        Assert.Equal("ru", s.Language);
        Assert.Equal(3, s.Overlay.Scale);
        Assert.Equal("#FFFFFF", s.Overlay.TextColor);
        Assert.Equal(1, s.Detection.SamplingFps);
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, s.Hotkeys.Bindings.Count);
    }

    [Fact]
    public void Conflicts_detect_duplicate_bindings_only()
    {
        var bindings = new[]
        {
            new HotkeyAssignment { Action = HotkeyAction.AddChest, VirtualKey = 0x77 },
            new HotkeyAssignment { Action = HotkeyAction.StartMatch, VirtualKey = 0x77 },
            new HotkeyAssignment { Action = HotkeyAction.EndMatch, VirtualKey = 0x77, Modifiers = HotkeyModifiers.Shift },
            new HotkeyAssignment { Action = HotkeyAction.ToggleOverlay, VirtualKey = 0 },
            new HotkeyAssignment { Action = HotkeyAction.ToggleOverlayEditMode, VirtualKey = 0 },
        };

        var conflicts = HotkeyConflicts.Find(bindings);

        Assert.Equal(new HashSet<HotkeyAction> { HotkeyAction.AddChest, HotkeyAction.StartMatch }, conflicts);
    }
}
