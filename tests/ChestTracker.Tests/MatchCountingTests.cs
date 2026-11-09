using ChestTracker.Core.Detection;
using ChestTracker.Core.Models;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Tracking;
using ChestTracker.Vision;

namespace ChestTracker.Tests;

/// <summary>
/// The match hotkey / button toggle counting; YOLO detection is tied to the running match; the built-in model is
/// the only model. Uses the real engine, settings, controller and (where noted) the real best.onnx.
/// </summary>
public class MatchCountingTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly ModelLabels TwoClass = new(["closed_chest", "open_chest"]);
    private static readonly Box Chest = new(900, 500, 1000, 575);
    private readonly TempDatabase _db = new();
    private readonly FakeClock _clock = new(T0);

    public void Dispose() => _db.Dispose();

    private TrackingEngine Engine(TrackingMode mode = TrackingMode.Automatic) =>
        _db.CreateEngine(_clock, new TrackingOptions { ManualDebounce = TimeSpan.Zero }, mode);

    /// <summary>The match hotkey and the dashboard button both call this (MainViewModel.ToggleMatch).</summary>
    private MatchControlOutcome Press(TrackingEngine engine)
    {
        _clock.AdvanceSeconds(5);
        return engine.ToggleMatch().Outcome;
    }

    private static Detection Closed() => new(0, "closed_chest", ObjectRole.ChestClosed, 0.8f, Chest);
    private static Detection Open() => new(1, "open_chest", ObjectRole.ChestOpened, 0.85f, Chest);

    /// <summary>Returns scripted detections per Detect call; can run an action during "inference".</summary>
    private sealed class ScriptedDetector : IObjectDetector
    {
        public ModelLabels Labels => TwoClass;
        public Queue<Detection[]> Frames { get; } = new();
        public Action? DuringInference { get; set; }

        public IReadOnlyList<Detection> Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride)
        {
            DuringInference?.Invoke();
            return Frames.Count > 0 ? Frames.Dequeue() : [];
        }

        public void Dispose() { }
    }

    private static readonly byte[] Pixels = new byte[16 * 16 * 4];

    private (MatchDetectionController Controller, ScriptedDetector Detector) Detection(TrackingEngine engine)
    {
        var detector = new ScriptedDetector();
        var controller = new MatchDetectionController(engine)
        {
            Pipeline = new YoloChestPipeline(detector, YoloSettings.TrackingOptionsFor(new DetectionSettings())),
        };
        return (controller, detector);
    }

    /// <summary>One captured frame through the controller exactly like the app's inference thread does it.</summary>
    private static DetectionFrameResult? Frame(MatchDetectionController c, TrackingEngine engine, int index, Detection[] detections,
        ScriptedDetector d)
    {
        d.Frames.Enqueue(detections);
        if (c.Admit() is not { } ticket)
        {
            d.Frames.Clear();
            return null; // counting stopped: the frame is not analysed
        }

        return c.Process(ticket, Pixels, 16, 16, 64, T0.AddMilliseconds(125 * index),
            (decision, match) => DetectionCounting.Apply(engine, decision, false, true, trackedIdentity: true, expectedMatchId: match));
    }

    private static int AutoEvents(TempDatabase db) => db.Repo.GetAllEvents().Count(e => e.Source == EventSource.Automatic && e.Kind == ChestEventKind.Add);

    // ------------------------------------------------------------------ 1–6, 9, 10: hotkey and match state

    [Fact]
    public void Match_hotkey_alternates_start_and_end_and_the_button_shares_the_same_state()
    {
        var engine = Engine(TrackingMode.Hybrid);
        Assert.Equal(MatchControlOutcome.Started, Press(engine));   // 1st press (hotkey)
        var first = engine.CurrentMatch!.Id;
        Assert.Equal(MatchControlOutcome.Ended, Press(engine));     // 2nd press (button)
        Assert.Null(engine.CurrentMatch);
        Assert.Equal(MatchControlOutcome.Started, Press(engine));   // 3rd press (hotkey)
        Assert.NotEqual(first, engine.CurrentMatch!.Id);            // a new, clean match
        Assert.Equal(MatchControlOutcome.Ended, Press(engine));     // 4th press (button)
        Assert.Null(engine.CurrentMatch);

        // A double press (key bounce, double click) inside the guard never flips the state twice.
        Assert.Equal(MatchControlOutcome.Started, Press(engine));
        Assert.Equal(MatchControlOutcome.IgnoredDoubleClick, engine.ToggleMatch().Outcome);
        Assert.NotNull(engine.CurrentMatch);
        Assert.Equal(3, engine.GetSnapshot().MatchesPlayed);
    }

    [Fact]
    public void While_stopped_manual_and_automatic_chests_are_ignored_and_nothing_starts()
    {
        var engine = Engine(TrackingMode.Hybrid);
        Press(engine);
        engine.AddChestManual(fromHotkey: true);
        Press(engine);                                              // died: stop counting

        Assert.Equal(TrackingResultCode.NoActiveMatch, engine.AddChestManual(fromHotkey: true).Code);
        Assert.Equal(TrackingResultCode.NoActiveMatch, engine.AddChestManual(fromHotkey: false).Code);
        Assert.Equal("stale", DetectionCounting.Apply(engine, new DetectionDecision(DetectionOutcome.Confirmed, 0.9, _clock.UtcNow, 2), false, true, true));
        Assert.Equal("stale", DetectionCounting.Apply(engine, new DetectionDecision(DetectionOutcome.Uncertain, 0.5, _clock.UtcNow, 2), false, true, true));
        Assert.Null(engine.CurrentMatch);
        Assert.Empty(engine.GetPendingReviews());
        Assert.Equal(1, engine.GetSnapshot().LifetimeCount);
        Assert.Equal(1, engine.GetSnapshot().MatchesPlayed);
    }

    [Fact]
    public void Stopping_keeps_the_ended_match_in_history_and_lifetime_adds_up_across_matches()
    {
        var engine = Engine(TrackingMode.Hybrid);
        var counts = new[] { 3, 0, 5 };
        var matches = new List<Guid>();
        foreach (var n in counts)
        {
            Press(engine);
            matches.Add(engine.CurrentMatch!.Id);
            for (var i = 0; i < n; i++)
            {
                _clock.AdvanceSeconds(3);
                engine.AddChestManual(fromHotkey: true);
            }

            Press(engine);
        }

        Assert.Equal(counts, matches.Select(m => _db.Repo.GetMatchSummary(m)!.Chests));
        Assert.All(matches, m => Assert.NotNull(_db.Repo.GetMatch(m)!.EndedAt));
        var s = engine.GetSnapshot();
        Assert.Equal(8, s.LifetimeCount);
        Assert.Equal(3, s.MatchesPlayed);
        Assert.Equal(5, s.Record);

        // Undo and corrections of a finished match stay possible while stopped.
        Assert.Equal(TrackingResultCode.Applied, engine.SetMatchCount(matches[0], 2).Code);
        Assert.Equal(7, engine.GetSnapshot().LifetimeCount);
    }

    // ------------------------------------------------------------------ 7, 8, 17, 18: detection lifecycle

    [Fact]
    public void Detection_counts_one_opening_only_inside_a_match()
    {
        var engine = Engine();
        var (c, d) = Detection(engine);

        // No match: frames are not even analysed.
        for (var i = 0; i < 6; i++)
        {
            Assert.Null(Frame(c, engine, i, i < 3 ? [Closed()] : [Open()], d));
        }

        Assert.Equal(0, AutoEvents(_db));

        Press(engine);
        var results = new List<AppliedOpening>();
        for (var i = 10; i < 16; i++)
        {
            results.AddRange(Frame(c, engine, i, i < 13 ? [Closed()] : [Open()], d)!.Openings);
        }

        var opening = Assert.Single(results);                   // 18. closed → open = exactly one event
        Assert.Equal("counted", opening.Result);
        Assert.Equal(1, AutoEvents(_db));
        Assert.Equal(1, engine.GetSnapshot().MatchCount);
    }

    [Fact]
    public void A_frame_in_flight_when_the_player_stops_adds_nothing()
    {
        var engine = Engine();
        var (c, d) = Detection(engine);
        Press(engine);
        for (var i = 0; i < 4; i++)
        {
            Frame(c, engine, i, i < 3 ? [Closed()] : [Open()], d);  // closed, closed, closed, open: one more open frame counts
        }

        // The confirming frame is being analysed when the player presses the match hotkey.
        d.DuringInference = () => Press(engine);
        Assert.Null(Frame(c, engine, 4, [Open()], d));
        Assert.Equal(0, AutoEvents(_db));
        Assert.Null(engine.CurrentMatch);
    }

    [Fact]
    public void A_stale_ticket_from_the_previous_match_never_counts_in_the_next_one()
    {
        var engine = Engine();
        var (c, d) = Detection(engine);
        Press(engine);
        var oldTicket = c.Admit()!.Value;
        Press(engine);
        Press(engine);                                             // stopped and restarted before the old frame was analysed
        d.Frames.Enqueue([Open()]);
        Assert.Null(c.Process(oldTicket, Pixels, 16, 16, 64, T0, (_, _) => throw new InvalidOperationException("must not apply")));

        // Even an opening decided for the old match is refused by the engine (atomic match check).
        var first = _db.Repo.GetMatches().OrderBy(m => m.StartedAt).First().Id;
        Assert.Equal("stale", DetectionCounting.Apply(engine, new DetectionDecision(DetectionOutcome.Confirmed, 0.9, _clock.UtcNow, 2), false, true, true, first));
        Assert.Equal(0, AutoEvents(_db));
    }

    [Fact]
    public void Restarting_counting_starts_with_clean_chest_tracks()
    {
        var engine = Engine();
        var (c, d) = Detection(engine);
        Press(engine);
        for (var i = 0; i < 3; i++)
        {
            Frame(c, engine, i, [Closed()], d);                    // chest confirmed closed in match 1
        }

        Press(engine);                                             // stop
        Press(engine);                                             // next match, the same spot is now open (looted earlier)
        var results = new List<AppliedOpening>();
        for (var i = 10; i < 15; i++)
        {
            results.AddRange(Frame(c, engine, i, [Open()], d)!.Openings);
        }

        Assert.Empty(results);                                     // no stale Closed track: first seen open → never counted
        Assert.Equal(ChestState.Opened, Assert.Single(c.Pipeline!.Tracker.Tracks).State);
        Assert.Equal(0, AutoEvents(_db));
    }

    [Fact]
    public void Manual_mode_never_creates_automatic_events()
    {
        // In manual mode the app never starts capture (DetectionService.IsActiveMode); the engine still refuses
        // detections outside a match, and inside a match hybrid merging keeps one chest per opening.
        var engine = Engine(TrackingMode.Manual);
        Assert.Equal("stale", DetectionCounting.Apply(engine, new DetectionDecision(DetectionOutcome.Confirmed, 0.9, T0, 2), false, true, true));
        Assert.Equal(0, AutoEvents(_db));
    }

    // ------------------------------------------------------------------ 11–16: built-in model

    private static string BuiltIn()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "src", "ChestTracker.App", BuiltInModel.Folder, BuiltInModel.FileName);
            if (File.Exists(file))
            {
                return file;
            }
        }

        throw new FileNotFoundException("built-in model not found in the repository");
    }

    [Fact]
    public void Built_in_model_loads_with_closed_and_open_chest_classes()
    {
        using var detector = BuiltInModel.Load(BuiltIn());
        Assert.Equal(["closed_chest", "open_chest"], detector.Labels.Names);
        Assert.Equal([ObjectRole.ChestClosed, ObjectRole.ChestOpened], detector.Labels.Roles);
        Assert.Equal(640, detector.Info.InputSize);
    }

    [Fact]
    public void Missing_or_damaged_model_gives_a_clear_error()
    {
        var missing = Path.Combine(_db.Directory, "models", "best.onnx");
        var ex = Assert.Throws<BuiltInModelException>(() => BuiltInModel.Load(missing));
        Assert.Contains("missing", ex.Message);
        Assert.Contains("Reinstall", ex.Message);

        var damaged = Path.Combine(_db.Directory, "damaged.onnx");
        File.WriteAllBytes(damaged, File.ReadAllBytes(BuiltIn()).Take(4096).ToArray()); // truncated download
        ex = Assert.Throws<BuiltInModelException>(() => BuiltInModel.Load(damaged));
        Assert.Contains("damaged", ex.Message);
    }

    [Fact]
    public void Legacy_engine_and_model_path_settings_are_ignored_and_stay_ignored_after_restart()
    {
        var file = Path.Combine(_db.Directory, "settings.json");
        File.WriteAllText(file, """
            { "TrackingMode": "Automatic",
              "Detection": { "Engine": "Heuristic", "YoloModelPath": "C:\\\\models\\\\other.onnx", "Sensitivity": 70 },
              "Tracking": { "AutoStartMatch": true } }
            """);
        var store = new SettingsStore(file);
        var loaded = store.Load();
        Assert.Equal(DetectionEngine.Yolo, loaded.Detection.Engine);
        Assert.Equal("", loaded.Detection.YoloModelPath);
        Assert.Equal(70, loaded.Detection.Sensitivity);            // unrelated settings are kept

        store.Save(loaded);
        var again = new SettingsStore(file).Load();                // restart
        Assert.Equal(DetectionEngine.Yolo, again.Detection.Engine);
        Assert.Equal("", again.Detection.YoloModelPath);
        Assert.Equal(BuiltInModel.PathIn(AppContext.BaseDirectory), Path.Combine(AppContext.BaseDirectory, "models", "best.onnx"));
    }

    [Fact]
    public void The_app_project_publishes_the_built_in_model()
    {
        var model = BuiltIn();
        var project = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(model))!, "ChestTracker.App.csproj");
        var xml = File.ReadAllText(project);
        Assert.Contains(@"<None Include=""models\best.onnx"" CopyToOutputDirectory=""PreserveNewest"" CopyToPublishDirectory=""PreserveNewest"" />", xml);
        Assert.True(new FileInfo(model).Length > 1_000_000);
    }
}
