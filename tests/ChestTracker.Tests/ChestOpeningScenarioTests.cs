using ChestTracker.Core.Detection;
using ChestTracker.Core.Models;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Tracking;
using ChestTracker.Vision;

namespace ChestTracker.Tests;

/// <summary>
/// The provided two-class model (closed_chest / open_chest) through the app's own tracker settings, the counting
/// step and the engine. Detections are scripted, so these test the logic, not the model's accuracy.
/// </summary>
public class ChestOpeningScenarioTests : IDisposable
{
    private static readonly ModelLabels TwoClass = new(["closed_chest", "open_chest"]);
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Box Chest = new(900, 500, 1000, 575);
    private static readonly Box Left = new(500, 500, 600, 575);
    private static readonly Box Right = new(1300, 500, 1400, 575);

    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class NoDetector(ModelLabels labels) : IObjectDetector
    {
        public ModelLabels Labels { get; } = labels;
        public IReadOnlyList<Detection> Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride) => [];
        public void Dispose() { }
    }

    /// <summary>A tracker configured exactly like the app (default settings) for the two-class model.</summary>
    private static ChestTrackManager AppTracker(DetectionSettings? settings = null) =>
        new YoloChestPipeline(new NoDetector(TwoClass), YoloSettings.TrackingOptionsFor(settings ?? new DetectionSettings())).Tracker;

    private static Detection Closed(Box? box = null, float conf = 0.8f) => new(0, "closed_chest", ObjectRole.ChestClosed, conf, box ?? Chest);

    private static Detection Open(Box? box = null, float conf = 0.8f) => new(1, "open_chest", ObjectRole.ChestOpened, conf, box ?? Chest);

    private static Box Shift(Box b, float dx) => new(b.X1 + dx, b.Y1, b.X2 + dx, b.Y2);

    /// <summary>8 FPS, the app's YOLO capture rate.</summary>
    private static List<ChestOpenEvent> Run(ChestTrackManager m, IEnumerable<Detection[]> frames, int startFrame = 0)
    {
        var events = new List<ChestOpenEvent>();
        var i = startFrame;
        foreach (var f in frames)
        {
            events.AddRange(m.Update(f, T0 + TimeSpan.FromMilliseconds(125 * i++)).Events);
        }

        return events;
    }

    private static IEnumerable<Detection[]> Repeat(int n, params Detection[] frame) => Enumerable.Repeat(frame, n);

    // ---------------------------------------------------------------- 1–8: tracking and confirmation

    [Fact]
    public void S1_a_closed_chest_that_stays_closed_is_not_counted()
    {
        var m = AppTracker();
        Assert.Empty(Run(m, Repeat(40, Closed())));
        Assert.Equal(ChestState.Closed, Assert.Single(m.Tracks).State);
    }

    [Fact]
    public void S2_a_closed_chest_that_opens_is_counted_once()
    {
        var m = AppTracker();
        var ev = Assert.Single(Run(m, Repeat(3, Closed()).Concat(Repeat(3, Open(conf: 0.85f)))));
        Assert.Equal(DetectionOutcome.Confirmed, ev.Outcome);
        Assert.Equal([ChestState.Closed, ChestState.Opened, ChestState.Counted], ev.Evidence.Transitions.Select(t => t.To));
        Assert.Equal(2, ev.Evidence.OpenedFrames); // confirmed on the second consecutive open frame
    }

    [Fact]
    public void S3_an_open_chest_that_comes_into_view_is_not_counted_even_if_it_flickers_closed()
    {
        var m = AppTracker();
        var frames = Repeat(10, Open()).Concat(Repeat(3, Closed(conf: 0.6f))).Concat(Repeat(10, Open()));
        Assert.Empty(Run(m, frames));
        Assert.Equal(ChestState.Opened, Assert.Single(m.Tracks).State);
    }

    [Fact]
    public void S4_an_opened_chest_that_stays_in_view_is_counted_only_once()
    {
        var m = AppTracker();
        var frames = Repeat(3, Closed()).Concat(Repeat(200, Open())); // 25 s looking at the looted chest
        Assert.Single(Run(m, frames));
        Assert.Equal(ChestState.Counted, Assert.Single(m.Tracks).State);
    }

    [Fact]
    public void S5_two_different_chests_opened_one_after_the_other_count_twice()
    {
        var m = AppTracker();
        var frames = Repeat(3, Closed(Left), Closed(Right))
            .Concat(Repeat(2, Open(Left), Closed(Right)))      // left chest opens
            .Concat(Repeat(2, Open(Left), Open(Right)));       // 0.25 s later the right one
        var events = Run(m, frames);
        Assert.Equal(2, events.Count);
        Assert.NotEqual(events[0].TrackId, events[1].TrackId);

        // Both reach the engine: there is no global cooldown for tracked detections.
        var engine = _db.CreateEngine(new FakeClock(T0), mode: TrackingMode.Automatic);
        engine.StartMatch();
        foreach (var ev in events)
        {
            Assert.Equal("counted", DetectionCounting.Apply(engine, ev.ToDecision(), false, true, trackedIdentity: true));
        }

        Assert.Equal(2, engine.GetSnapshot().LifetimeCount);
    }

    [Fact]
    public void S6_a_brief_detection_loss_or_a_one_frame_flicker_is_not_counted()
    {
        var m = AppTracker();
        var frames = Repeat(3, Closed())
            .Concat(Repeat(4, []))                             // 0.5 s: model misses the chest (smoke, other player)
            .Concat(Repeat(3, Closed()))
            .Concat(Repeat(1, Open(conf: 0.7f)))               // one wrong frame
            .Concat(Repeat(5, Closed()))
            .Concat(Repeat(20, []));                           // looked away: disappearing never counts
        Assert.Empty(Run(m, frames));
    }

    [Fact]
    public void S7_camera_turning_keeps_the_same_chest_identity()
    {
        var m = AppTracker();
        var frames = new List<Detection[]>();
        var box = Chest;
        for (var i = 0; i < 8; i++)
        {
            frames.Add([Closed(box)]);
            box = Shift(box, -110); // the chest slides 110 px per frame across the screen: boxes no longer overlap
        }

        for (var i = 0; i < 3; i++)
        {
            frames.Add([Open(box)]);
            box = Shift(box, -110);
        }

        var ev = Assert.Single(Run(m, frames));
        Assert.Single(m.Tracks);
        Assert.Equal(m.Tracks[0].Id, ev.TrackId);
        Assert.Equal(8, ev.Evidence.ClosedFrames);
    }

    [Fact]
    public void S8_two_nearby_chests_are_not_mixed_up()
    {
        // Side by side, almost touching.
        var a = new Box(900, 500, 1000, 575);
        var b = new Box(1010, 500, 1110, 575);
        var m = AppTracker();
        var events = Run(m, Repeat(3, Closed(a), Closed(b))
            .Concat(Repeat(3, Closed(a), Open(b))));
        var ev = Assert.Single(events);
        var opened = m.Tracks.Single(t => t.State == ChestState.Counted);
        Assert.Equal(opened.Id, ev.TrackId);
        Assert.Equal(b, opened.Box);
        Assert.Equal(ChestState.Closed, m.Tracks.Single(t => t != opened).State);

        // A detection that jumps to between two tracked chests is ambiguous: it is not attached to either.
        var m2 = AppTracker();
        Run(m2, Repeat(3, Closed(a), Closed(b)));
        var between = new Box(955, 560, 1055, 635);
        var update = m2.Update([Closed(between)], T0 + TimeSpan.FromSeconds(1));
        Assert.Contains(update.Rejected!, r => r.Reason.StartsWith("ambiguous"));
        Assert.Equal(3, m2.Tracks.Count);
    }

    [Fact]
    public void A_closed_chest_and_a_different_open_chest_nearby_are_not_one_opening()
    {
        var m = AppTracker();
        var other = Shift(Chest, 90); // no overlap, within centre distance
        var frames = Repeat(4, Closed()).Concat(Repeat(10, Open(other))); // closed chest leaves view, open one appears
        Assert.Empty(Run(m, frames));
        Assert.Contains(m.Tracks, t => t.State == ChestState.Opened);
    }

    [Fact]
    public void Low_open_probability_goes_to_review_instead_of_counting()
    {
        var m = AppTracker();
        var ev = Assert.Single(Run(m, Repeat(3, Closed()).Concat(Repeat(6, Open(conf: 0.45f)))));
        Assert.Equal(DetectionOutcome.Uncertain, ev.Outcome);
        Assert.Equal(4, ev.Evidence.OpenedFrames); // decided after at most 4 open frames
    }

    [Fact]
    public void Weak_first_open_frames_wait_for_clearer_ones_and_still_count_once()
    {
        // Lid animation and loot burst blur the first open frames; the next ones are clear.
        var m = AppTracker();
        var frames = Repeat(3, Closed()).Concat([[Open(conf: 0.45f)], [Open(conf: 0.42f)], [Open(conf: 0.80f)], [Open(conf: 0.85f)]])
            .Concat(Repeat(20, Open(conf: 0.9f)));
        var ev = Assert.Single(Run(m, frames));
        Assert.Equal(DetectionOutcome.Confirmed, ev.Outcome);
        Assert.Equal(0.625, ev.Confidence, 3);    // best two of 0.45, 0.42, 0.80: decided on the third open frame
    }

    // ---------------------------------------------------------------- 9–12: chest events

    [Fact]
    public void S9_manual_press_and_automatic_detection_of_the_same_opening_count_once()
    {
        var clock = new FakeClock(T0);
        var engine = _db.CreateEngine(clock, mode: TrackingMode.Hybrid);
        engine.StartMatch();
        var decision = new DetectionDecision(DetectionOutcome.Confirmed, 0.8, T0, 2);

        Assert.True(engine.AddChestManual(fromHotkey: true).Applied);              // player presses the hotkey
        clock.AdvanceSeconds(0.8);                                                 // model confirms the opening
        Assert.Equal("suppressed", DetectionCounting.Apply(engine, decision, false, true, trackedIdentity: true));
        Assert.Equal(1, engine.GetSnapshot().LifetimeCount);

        clock.AdvanceSeconds(10);
        Assert.Equal("counted", DetectionCounting.Apply(engine, decision with { Timestamp = clock.UtcNow }, false, true, trackedIdentity: true));
        clock.AdvanceSeconds(0.5);
        Assert.False(engine.AddChestManual(fromHotkey: true).Applied);             // press after the detection: merged
        Assert.Equal(2, engine.GetSnapshot().LifetimeCount);
    }

    [Fact]
    public void S10_an_automatic_opening_is_a_normal_chest_event_in_lifetime_match_and_history()
    {
        var clock = new FakeClock(T0);
        var engine = _db.CreateEngine(clock, mode: TrackingMode.Automatic);
        engine.StartMatch();
        var before = engine.GetSnapshot().LifetimeCount;
        var ev = Assert.Single(Run(AppTracker(), Repeat(3, Closed()).Concat(Repeat(2, Open()))));

        Assert.Equal("counted", DetectionCounting.Apply(engine, ev.ToDecision(), false, true, trackedIdentity: true));
        var snap = engine.GetSnapshot();
        Assert.Equal(before + 1, snap.LifetimeCount);
        Assert.Equal(1, snap.MatchCount);
        var stored = Assert.Single(_db.Repo.GetAllEvents(), e => e.Kind == ChestEventKind.Add);
        Assert.Equal(EventSource.Automatic, stored.Source);
        Assert.Equal(0.8, stored.Confidence!.Value, 2);
        Assert.Equal(engine.CurrentMatch!.Id, stored.MatchId);
    }

    [Fact]
    public void S12_undo_removes_the_last_event_including_an_automatic_one()
    {
        var clock = new FakeClock(T0);
        var engine = _db.CreateEngine(clock, new TrackingOptions { ManualDebounce = TimeSpan.Zero }, TrackingMode.Hybrid);
        engine.StartMatch();
        var manual = engine.AddChestManual(fromHotkey: false).Event!;
        clock.AdvanceSeconds(10);
        DetectionCounting.Apply(engine, new DetectionDecision(DetectionOutcome.Confirmed, 0.9, clock.UtcNow, 2), false, true, true);
        var auto = _db.Repo.GetAllEvents().Single(e => e.Source == EventSource.Automatic);

        var undo = engine.RemoveLastChest(fromHotkey: false).Event!;
        Assert.Equal(auto.EventId, undo.UndoesEventId);
        undo = engine.RemoveLastChest(fromHotkey: false).Event!;
        Assert.Equal(manual.EventId, undo.UndoesEventId);
        Assert.Equal(0, engine.GetSnapshot().MatchCount);
    }

    // ---------------------------------------------------------------- 17, 18: the real model

    private static string? BundledModel()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "src", "ChestTracker.App", "models", "best.onnx");
            if (File.Exists(file))
            {
                return file;
            }
        }

        return null;
    }

    [Fact]
    public void Bundled_model_loads_with_its_two_classes_mapped_to_chest_states()
    {
        var path = BundledModel();
        Assert.NotNull(path);
        using var detector = OnnxYoloDetector.Load(path!);
        Assert.Equal(640, detector.Info.InputSize);
        Assert.Equal(["closed_chest", "open_chest"], detector.Labels.Names);
        Assert.Equal([ObjectRole.ChestClosed, ObjectRole.ChestOpened], detector.Labels.Roles);
        Assert.Equal("model metadata", detector.Info.LabelSource);
        Assert.Equal(("images", "output0"), (detector.Info.InputName, detector.Info.OutputName));

        // A blank 1080p frame: runs end to end and finds no chests.
        var frame = new byte[1920 * 1080 * 4];
        Assert.Empty(detector.Detect(frame, 1920, 1080, 1920 * 4));
        using var pipeline = new YoloChestPipeline(detector, YoloSettings.TrackingOptionsFor(new DetectionSettings()));
        Assert.False(pipeline.Options.ModelHasOpeningClass);
    }

    [Fact]
    public void S17_restart_keeps_statistics_and_the_model_still_loads()
    {
        var clock = new FakeClock(T0);
        var engine = _db.CreateEngine(clock, mode: TrackingMode.Automatic);
        engine.StartMatch();
        DetectionCounting.Apply(engine, new DetectionDecision(DetectionOutcome.Confirmed, 0.9, T0, 2), false, true, true);
        engine.Shutdown();

        _db.Reopen();
        var restarted = _db.CreateEngine(clock, mode: TrackingMode.Automatic);
        Assert.Equal(1, restarted.GetSnapshot().LifetimeCount);
        using var detector = OnnxYoloDetector.Load(BundledModel()!);
        Assert.Equal(2, detector.Labels.Count);
    }

    [Fact]
    public void S18_gpu_initialisation_failure_falls_back_to_the_cpu()
    {
        using var detector = OnnxYoloDetector.Load(BundledModel()!, gpuSetup: _ => throw new InvalidOperationException("no DirectX 12 device"));
        Assert.Equal(OnnxYoloDetector.CpuProvider, detector.Info.Provider);
        Assert.False(detector.Info.UsesGpu);
        Assert.Contains("no DirectX 12 device", detector.Info.ProviderNote);
        Assert.Empty(detector.Detect(new byte[64 * 64 * 4], 64, 64, 256)); // and it still runs

        using var cpu = OnnxYoloDetector.Load(BundledModel()!, device: InferenceDevice.Cpu);
        Assert.Equal(OnnxYoloDetector.CpuProvider, cpu.Info.Provider);
        Assert.Null(cpu.Info.ProviderNote);
    }
}

/// <summary>The chest sound plays only for chests this computer records (scenarios 13–16, local side).</summary>
public class LocalSoundTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Manual_and_automatic_adds_play_once_and_duplicates_play_nothing()
    {
        var engine = _db.CreateEngine(_clock, mode: TrackingMode.Hybrid);
        var played = new List<SoundCue>();
        LocalSoundRouter.Attach(engine, played.Add);

        engine.StartMatch();
        engine.AddChestManual(fromHotkey: true);
        _clock.AdvanceSeconds(0.1);
        engine.AddChestManual(fromHotkey: true);                    // debounced double press: no event, no sound
        _clock.AdvanceSeconds(0.5);
        DetectionCounting.Apply(engine, new DetectionDecision(DetectionOutcome.Confirmed, 0.9, _clock.UtcNow, 2), false, true, true); // merged with the press
        _clock.AdvanceSeconds(10);
        DetectionCounting.Apply(engine, new DetectionDecision(DetectionOutcome.Confirmed, 0.9, _clock.UtcNow, 2), false, true, true); // a new chest

        Assert.Equal([SoundCue.MatchStarted, SoundCue.ChestAdded, SoundCue.ChestAdded], played);
        Assert.Equal(2, engine.GetSnapshot().LifetimeCount);
    }

    [Fact]
    public void Reloading_the_database_reading_history_and_the_outbox_play_nothing()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();
        var outbox = new Core.Data.SyncOutbox(_db.Db);
        outbox.BeginParty(Guid.NewGuid());
        engine.AddChestManual(fromHotkey: false);
        engine.Shutdown();

        _db.Reopen();
        var restarted = _db.CreateEngine(_clock);
        var played = new List<SoundCue>();
        LocalSoundRouter.Attach(restarted, played.Add);
        var reopenedOutbox = new Core.Data.SyncOutbox(_db.Db);
        reopenedOutbox.EnqueueNew();
        var items = reopenedOutbox.Peek(10);
        reopenedOutbox.MarkFailed(items.Select(i => i.EventId).ToArray(), "offline");  // retry later
        reopenedOutbox.Remove(items.Select(i => i.EventId).ToArray());                   // delivered
        _ = _db.Repo.GetAllEvents();
        _ = restarted.GetSnapshot();

        Assert.Empty(played);
        Assert.Equal(1, restarted.GetSnapshot().LifetimeCount);
    }

    [Fact]
    public void The_same_event_never_plays_twice()
    {
        var engine = _db.CreateEngine(_clock);
        var played = new List<SoundCue>();
        var router = new LocalSoundRouter(played.Add);
        TrackingChangedEventArgs? added = null;
        engine.Changed += (_, e) => added = e.Kind == TrackingChangeKind.ChestAdded ? e : added;
        engine.StartMatch();
        engine.AddChestManual(fromHotkey: false);

        router.Handle(added!);
        router.Handle(added!);
        Assert.Equal([SoundCue.ChestAdded], played);
    }
}
