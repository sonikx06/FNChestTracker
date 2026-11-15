using ChestTracker.Core.Detection;
using ChestTracker.Core.Models;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Tracking;
using ChestTracker.Vision;

namespace ChestTracker.Tests;

/// <summary>Automatic counting stops while the player spectates (Fortnite's "SPECTATING" banner on screen).</summary>
public class SpectatorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly ModelLabels TwoClass = new(["closed_chest", "open_chest"]);
    private static readonly Box Chest = new(900, 500, 1000, 575);
    private static readonly byte[] Pixels = new byte[16 * 16 * 4];
    private readonly TempDatabase _db = new();
    private readonly FakeClock _clock = new(T0);
    private readonly MatchCountingTests.FakeSpectatorProbe _probe = new();

    public void Dispose() => _db.Dispose();

    // ------------------------------------------------------------------ banner text (real Windows OCR output)

    [Theory]
    [InlineData("6.44KWs O Pa c kets/s Packet ke tS/s SPECTATING QQWAVNE")]                     // en-US OCR, spectating clip
    [InlineData(": kets/s Icke t 1-0\", (D 2.34 KB's 44 Packets's 0% Packet Log SPECTATING QQWAVNE")]
    [InlineData("квт 6.16 квк 38 Рас“ ард Ке SPECTATING Q0wavNE—")]                              // ru OCR, same banner
    [InlineData("SPECTATlNG Player")]                                                             // l read instead of I
    [InlineData("SPECTAT1NG")]
    public void Spectator_banner_text_is_recognised(string ocr) => Assert.True(SpectatorBanner.Matches(ocr));

    [Theory]
    [InlineData("O E14Kß.•s 59 Packe cket LOS")]                                                  // normal gameplay HUD
    [InlineData("KB,'s• SE : kets/s Packets/s •cket Los 0% Packet Los s 193 sw w")]
    [InlineData("NW")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("SPECTATE")]                                                                      // too short / different word
    [InlineData("SPECTACULAR VICTORY")]
    [InlineData("CONTAINER OPENED")]
    public void Normal_hud_text_is_not_the_banner(string? ocr) => Assert.False(SpectatorBanner.Matches(ocr));

    [Theory]
    [InlineData("РЕЖИМ ЗРИТЕЛЯ", true)]
    [InlineData("РЕЖИМ ЗРИТЕПЯ", true)]
    [InlineData("SPECTATING", true)]
    [InlineData("SPECTAT1NG", true)]
    [InlineData("MySPECTATINGName", false)]
    [InlineData("chat: РЕЖИМ ЗРИТЕЛЯ", false)]
    [InlineData("НАБЛЮДЕНИЕ", false)]
    [InlineData("SPECTACULAR", false)]
    public void Only_title_lines_match(string text, bool expected) => Assert.Equal(expected, SpectatorBanner.MatchesTitleLine(text));

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void Banner_crop_is_confined_to_top_centre(int width, int height)
    {
        var r = SpectatorBanner.Region(width, height);
        Assert.Equal(width / 4, r.X);
        Assert.Equal(0, r.Y);
        Assert.True(r.Height <= height * .045);
        Assert.Equal(width / 2, r.Width);
    }

    // ------------------------------------------------------------------ state tracker

    [Fact]
    public void One_banner_sighting_stops_counting_and_disappearance_requires_control_confirmation()
    {
        var t = new GameplayStateTracker();
        Assert.Equal(GameplayState.Unknown, t.State);
        var time = T0;
        for (var i = 0; i < 17; i++, time = time.AddMilliseconds(125)) t.Observe(false, time); // 2 s
        Assert.Equal(GameplayState.Playing, t.State);           // ~2 s of normal play

        Assert.True(t.Observe(true, time));                      // eliminated: banner appears
        Assert.Equal(GameplayState.Spectating, t.State);

        // A few missed OCR reads while still spectating never re-enable counting.
        for (var i = 0; i < 5; i++) { time = time.AddMilliseconds(125); t.Observe(false, time); }
        t.Observe(true, time = time.AddMilliseconds(125));
        for (var i = 0; i < 10; i++) { time = time.AddMilliseconds(125); t.Observe(false, time); }
        Assert.Equal(GameplayState.Spectating, t.State);

        for (var i = 0; i < 17; i++) { time = time.AddMilliseconds(125); t.Observe(false, time); }
        Assert.Equal(GameplayState.Spectating, t.State); // even a long absence is not proof of local control
        t.ConfirmLocalControl();
        for (var i = 0; i < 17; i++) { time = time.AddMilliseconds(125); t.Observe(false, time); }
        Assert.Equal(GameplayState.Playing, t.State);
    }

    [Fact]
    public void Brief_hud_changes_and_failed_checks_do_not_flip_the_state()
    {
        var t = new GameplayStateTracker();
        var time = T0;
        for (var i = 0; i < 17; i++, time = time.AddMilliseconds(125)) t.Observe(false, time); // 2 s
        Assert.Equal(GameplayState.Playing, t.State);

        // Menu, map or emote: OCR sees something else or nothing usable for a moment.
        for (var i = 0; i < 8; i++, time = time.AddMilliseconds(125)) Assert.False(t.Observe(null, time));
        Assert.Equal(GameplayState.Playing, t.State);

        // The check stays unavailable for longer: counting pauses (Unknown) rather than guessing.
        for (var i = 0; i < 40; i++, time = time.AddMilliseconds(125)) t.Observe(null, time);
        Assert.Equal(GameplayState.Unknown, t.State);
        t.Reset();
        Assert.Equal(GameplayState.Unknown, t.State);
    }

    // ------------------------------------------------------------------ counting pipeline

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

    private static readonly Box OtherChest = new(300, 500, 400, 575);
    private static Detection Closed(Box? box = null) => new(0, "closed_chest", ObjectRole.ChestClosed, 0.8f, box ?? Chest);
    private static Detection Open(Box? box = null) => new(1, "open_chest", ObjectRole.ChestOpened, 0.85f, box ?? Chest);

    private (TrackingEngine Engine, MatchDetectionController C, ScriptedDetector D) Setup()
    {
        var engine = _db.CreateEngine(_clock, new TrackingOptions { ManualDebounce = TimeSpan.Zero }, TrackingMode.Automatic);
        var d = new ScriptedDetector();
        var c = new MatchDetectionController(engine, MatchCountingTests.QuickGameplay)
        {
            Pipeline = new YoloChestPipeline(d, YoloSettings.TrackingOptionsFor(new DetectionSettings())),
        };
        return (engine, c, d);
    }

    private int _frame;

    private DetectionFrameResult? Frame(TrackingEngine engine, MatchDetectionController c, ScriptedDetector d, Detection[] dets)
    {
        d.Frames.Enqueue(dets);
        if (c.Admit() is not { } ticket)
        {
            d.Frames.Clear();
            return null;
        }

        var result = c.Process(ticket, Pixels, 16, 16, 64, T0.AddMilliseconds(125 * _frame++),
            (decision, match) => DetectionCounting.Apply(engine, decision, false, true, true, match), _probe);
        if (result?.Frame is null)
        {
            d.Frames.Clear(); // the model did not run on this frame
        }

        return result;
    }

    private void Frames(TrackingEngine e, MatchDetectionController c, ScriptedDetector d, int n, Func<Detection[]> dets)
    {
        for (var i = 0; i < n; i++) Frame(e, c, d, dets());
    }

    private int AutoEvents() => _db.Repo.GetAllEvents().Count(x => x.Source == EventSource.Automatic && x.Kind == ChestEventKind.Add);

    [Fact]
    public void Playing_counts_and_spectating_a_teammate_who_opens_a_chest_counts_nothing()
    {
        var (engine, c, d) = Setup();
        engine.ToggleMatch();
        Frames(engine, c, d, 4, () => [Closed()]);
        Frames(engine, c, d, 3, () => [Open()]);
        Assert.Equal(1, AutoEvents());                                         // playing: own chest counted
        Assert.True(c.IsCounting);

        _probe.Spectating = true;                                             // eliminated, now watching a teammate
        Frames(engine, c, d, 4, () => [Closed()]);                            // the teammate walks up to a chest...
        Frames(engine, c, d, 6, () => [Open()]);                              // ...and opens it
        Assert.Equal(GameplayState.Spectating, c.GameplayState);
        Assert.False(c.IsCounting);
        Assert.Equal(1, AutoEvents());                                         // not the local player's chest
        Assert.NotNull(engine.CurrentMatch);                                   // the match is not ended by spectating
    }

    [Fact]
    public void Back_in_play_detection_resumes_with_fresh_tracks_and_spectating_never_starts_a_match()
    {
        var (engine, c, d) = Setup();
        engine.ToggleMatch();
        _probe.Spectating = true;
        Frames(engine, c, d, 3, () => [Closed()]);                            // a closed chest seen while spectating
        Assert.Equal(GameplayState.Spectating, c.GameplayState);

        Assert.True(c.ConfirmLocalControl());                                 // explicit confirmation after reboot
        _probe.Spectating = false;                                            // rebooted / back in play
        Frames(engine, c, d, 2, () => [Open()]);                              // same spot already open: no stale Closed track
        Assert.Equal(0, AutoEvents());
        Frames(engine, c, d, 3, () => [Closed(OtherChest)]);                  // the player's next chest
        Frames(engine, c, d, 3, () => [Open(OtherChest)]);
        Assert.Equal(1, AutoEvents());

        // Next match after the current one ends: starts Unknown, then counts again.
        _clock.AdvanceSeconds(5);
        Assert.Equal(MatchControlOutcome.Ended, engine.ToggleMatch().Outcome);
        _clock.AdvanceSeconds(5);
        Assert.Equal(MatchControlOutcome.Started, engine.ToggleMatch().Outcome);
        Assert.Equal(GameplayState.Unknown, c.GameplayState);
        Frames(engine, c, d, 4, () => [Closed()]);
        Frames(engine, c, d, 3, () => [Open()]);
        Assert.Equal(2, AutoEvents());

        // Spectating with no match running does nothing at all.
        _clock.AdvanceSeconds(5);
        engine.ToggleMatch();
        var matches = engine.GetSnapshot().MatchesPlayed;
        _probe.Spectating = true;
        Assert.Null(Frame(engine, c, d, [Open()]));
        _probe.Spectating = false;
        Assert.Null(Frame(engine, c, d, [Open()]));
        Assert.Equal(matches, engine.GetSnapshot().MatchesPlayed);
        Assert.Null(engine.CurrentMatch);
        Assert.False(c.ConfirmLocalControl()); // manual Stop remains authoritative
    }

    [Fact]
    public void A_result_in_flight_when_spectating_begins_is_dropped()
    {
        var (engine, c, d) = Setup();
        engine.ToggleMatch();
        Frames(engine, c, d, 4, () => [Closed()]);
        Frame(engine, c, d, [Open()]);                                        // one more open frame would count

        // While the model analyses the confirming frame, another frame shows the spectator banner.
        d.DuringInference = () => c.ObserveGameplay(true, T0.AddSeconds(30), engine.CurrentMatch!.Id);
        var result = Frame(engine, c, d, [Open()]);
        Assert.Null(result);
        Assert.Equal(0, AutoEvents());
        Assert.Equal(GameplayState.Spectating, c.GameplayState);
    }

    [Fact]
    public void Without_a_working_screen_check_nothing_is_counted()
    {
        var (engine, c, d) = Setup();
        _probe.Available = false;
        engine.ToggleMatch();
        Frames(engine, c, d, 4, () => [Closed()]);
        Frames(engine, c, d, 4, () => [Open()]);
        Assert.Equal(GameplayState.Unknown, c.GameplayState);
        Assert.Equal(0, AutoEvents());
    }
    [Fact]
    public void Spectating_cancels_pending_reviews_and_mode_switch_invalidates_inflight_frames()
    {
        var (engine, c, d) = Setup();
        engine.StartMatch();
        Frames(engine, c, d, 4, () => [Closed()]);
        var review = engine.AddPendingReview(.6);
        c.ObserveGameplay(true, T0.AddSeconds(5));
        Assert.Empty(engine.GetPendingReviews());
        Assert.False(engine.ResolveReview(review!.Id, true).Applied);
        Assert.True(c.ConfirmLocalControl());
        _probe.Spectating = false;
        Frames(engine, c, d, 4, () => [Closed()]);
        Frame(engine, c, d, [Open()]);
        d.DuringInference = () => { engine.SetMode(TrackingMode.Manual); engine.SetMode(TrackingMode.Automatic); };
        Assert.Null(Frame(engine, c, d, [Open()]));
        Assert.Equal(0, AutoEvents());
    }

    [Fact]
    public void Unreadable_frame_never_completes_a_pending_opening()
    {
        var (engine, c, d) = Setup();
        engine.StartMatch();
        Frames(engine, c, d, 4, () => [Closed()]);
        Frame(engine, c, d, [Open()]);
        _probe.Spectating = null;
        Frame(engine, c, d, [Open()]);
        _probe.Spectating = false;
        Frames(engine, c, d, 4, () => [Open()]);
        Assert.Equal(0, AutoEvents());
    }
}
