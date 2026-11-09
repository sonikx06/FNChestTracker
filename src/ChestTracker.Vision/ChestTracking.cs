using System.Globalization;
using ChestTracker.Core.Detection;

namespace ChestTracker.Vision;

/// <summary>
/// Per-chest state: Unconfirmed (UNKNOWN) → Closed → [Opening] → Counted, or → Opened (already open when first
/// seen: never counted). Opening is used only by models that have a chest_opening class.
/// </summary>
public enum ChestState
{
    /// <summary>New track, not enough frames yet (UNKNOWN).</summary>
    Unconfirmed,
    Closed,
    Opening,

    /// <summary>Open, but not counted: it was never seen closed/opening (already open when it came into view).</summary>
    Opened,

    /// <summary>The opening was decided (counted, queued for review or discarded). Never evaluated again.</summary>
    Counted,
}

/// <summary>Tracking and confirmation parameters. The defaults are the conservative values the app uses.</summary>
public sealed record ChestTrackingOptions
{
    /// <summary>Minimum IoU (against the last or the motion-predicted box) to continue a track in the next frame.</summary>
    public float MatchIoU { get; init; } = 0.3f;

    /// <summary>
    /// Without IoU overlap (fast camera turn), a detection of the same class may still continue a track when its centre
    /// is within this many track-box diagonals of the predicted centre and no other chest is a comparable candidate.
    /// </summary>
    public float MaxCenterDistance { get; init; } = 1.0f;

    /// <summary>A distance-only match is refused when another candidate is less than this many times farther.</summary>
    public float AmbiguityRatio { get; init; } = 1.6f;

    /// <summary>Largest width or height ratio between a track and a detection that can be the same chest.</summary>
    public float MaxSizeRatio { get; init; } = 2.0f;

    /// <summary>Boxes of different chest classes overlapping this much in one frame are one chest: the higher score wins.</summary>
    public float DuplicateIoU { get; init; } = 0.6f;

    /// <summary>A track survives this many frames without a detection (occlusion, loot burst).</summary>
    public int MaxMissedFrames { get; init; } = 8;

    public TimeSpan MaxMissedTime { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Detections below this confidence are ignored entirely (any class).</summary>
    public float MinDetectionConfidence { get; init; } = 0.35f;

    /// <summary>
    /// A closed-chest detection needs at least this confidence to be used. Lower than the open floor: closed evidence
    /// alone never counts (it needs <see cref="MinClosedFrames"/> frames and then a confirmed open streak), and chests are
    /// often seen closed only briefly before they are opened.
    /// </summary>
    public float MinClosedConfidence { get; init; } = 0.35f;

    /// <summary>An open-chest (or opening) detection needs at least this confidence to be used.</summary>
    public float MinOpenedConfidence { get; init; } = 0.40f;

    /// <summary>The chest must be seen closed in at least this many frames before an opening can count.</summary>
    public int MinClosedFrames { get; init; } = 2;

    /// <summary>The opening animation must be seen in at least this many frames.</summary>
    public int MinOpeningFrames { get; init; } = 2;

    /// <summary>The open chest must then be seen in at least this many frames (consecutive when there is no opening class).</summary>
    public int MinOpenedFrames { get; init; } = 2;

    /// <summary>
    /// Without an opening class: when the first <see cref="MinOpenedFrames"/> open frames are not confident enough to
    /// count, keep watching the same chest up to this many consecutive open frames and decide on the best ones (the
    /// first frames of an opening are often blurred by the lid animation and loot burst).
    /// </summary>
    public int MaxOpenedFrames { get; init; } = 4;

    /// <summary>Without an opening class: the required open frames must fall within this time of the first one.</summary>
    public TimeSpan ConfirmationWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>An opening only counts for a chest that was first seen closed (not one that came into view mid-animation).</summary>
    public bool RequireClosedBeforeOpening { get; init; } = true;

    /// <summary>
    /// False for models trained without a chest_opening class (only closed / opened). Then the same chest seen
    /// closed and afterwards opened in several frames is the full evidence and is scored normally, instead of
    /// being treated as a missed animation (review only). Set by <see cref="YoloChestPipeline"/> from the model's labels.
    /// </summary>
    public bool ModelHasOpeningClass { get; init; } = true;

    /// <summary>Closed frames in a row that cancel an opening (interaction interrupted).</summary>
    public int CancelOpeningClosedFrames { get; init; } = 3;

    public double ConfirmThreshold { get; init; } = 0.6;
    public double ReviewThreshold { get; init; } = 0.4;

    /// <summary>A new track overlapping a chest counted this recently is the same chest (track was lost and re-created).</summary>
    public TimeSpan CountedMemory { get; init; } = TimeSpan.FromSeconds(10);

    public float CountedMemoryIoU { get; init; } = 0.3f;

    public int MaxHistory { get; init; } = 64;

    public float MinConfidenceFor(ObjectRole role) => Math.Max(MinDetectionConfidence, role switch
    {
        ObjectRole.ChestClosed => MinClosedConfidence,
        ObjectRole.ChestOpening or ObjectRole.ChestOpened => MinOpenedConfidence,
        _ => MinDetectionConfidence,
    });
}

public readonly record struct FrameObservation(DateTimeOffset Time, ObjectRole Role, float Confidence, Box Box);

public sealed record StateTransition(DateTimeOffset Time, ChestState From, ChestState To, string Reason);

/// <summary>Why a chest was counted (or not): frame counts, confidences, transitions.</summary>
public sealed record ChestEvidence(
    int TrackId,
    int ClosedFrames,
    int OpeningFrames,
    int OpenedFrames,
    double MeanOpeningConfidence,
    double MeanOpenedConfidence,
    IReadOnlyList<StateTransition> Transitions)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"track={TrackId} closed={ClosedFrames} opening={OpeningFrames}@{MeanOpeningConfidence:0.00} opened={OpenedFrames}@{MeanOpenedConfidence:0.00} path={string.Join(">", Transitions.Select(t => t.To))}");
}

public sealed record ChestOpenEvent(int TrackId, DetectionOutcome Outcome, double Confidence, DateTimeOffset Timestamp, Box Box, ChestEvidence Evidence)
{
    public DetectionDecision ToDecision() => new(Outcome, Confidence, Timestamp, Evidence.OpeningFrames + Evidence.OpenedFrames);
}

/// <summary>A chest detection the tracker did not use, and why (diagnostics).</summary>
public sealed record RejectedDetection(Detection Detection, string Reason);

/// <summary>A state change of one track in this frame (diagnostics).</summary>
public sealed record TrackTransition(int TrackId, StateTransition Transition, Box Box, float Confidence);

/// <summary>One tracked chest. All state changes go through <see cref="Observe"/>.</summary>
public sealed class ChestTrack
{
    private readonly ChestTrackingOptions _o;
    private readonly List<FrameObservation> _history = [];
    private readonly List<StateTransition> _transitions = [];
    private readonly List<float> _openingConf = [];
    private readonly List<float> _openedConf = [];
    private int _closedRun;
    private int _openedRun;
    private DateTimeOffset _openedRunStart;
    private float _velocityX;
    private float _velocityY;

    internal ChestTrack(int id, FrameObservation first, ChestTrackingOptions options, bool alreadyCounted)
    {
        _o = options;
        Id = id;
        Box = first.Box;
        FirstSeen = LastSeen = first.Time;
        if (alreadyCounted)
        {
            Move(ChestState.Counted, first.Time, "same place as a chest counted moments ago");
        }
    }

    public int Id { get; }
    public ChestState State { get; private set; } = ChestState.Unconfirmed;
    public Box Box { get; private set; }
    public DateTimeOffset FirstSeen { get; }
    public DateTimeOffset LastSeen { get; private set; }
    public int MissedFrames { get; internal set; }
    public ObjectRole LastRole { get; private set; }
    public float LastConfidence { get; private set; }
    public int ClosedFrames { get; private set; }
    public int OpeningFrames => _openingConf.Count;
    public int OpenedFrames => _openedConf.Count;
    public IReadOnlyList<FrameObservation> History => _history;
    public IReadOnlyList<StateTransition> Transitions => _transitions;

    /// <summary>Where the chest is expected at <paramref name="time"/> (constant screen velocity, for camera turns).</summary>
    public Box PredictedBox(DateTimeOffset time)
    {
        var dt = (float)Math.Clamp((time - LastSeen).TotalSeconds, 0, 1);
        var dx = _velocityX * dt;
        var dy = _velocityY * dt;
        return new Box(Box.X1 + dx, Box.Y1 + dy, Box.X2 + dx, Box.Y2 + dy);
    }

    /// <summary>Applies one frame; returns an event when this frame completes an opening.</summary>
    internal ChestOpenEvent? Observe(FrameObservation obs)
    {
        if (_history.Count > 0)
        {
            var dt = (float)(obs.Time - LastSeen).TotalSeconds;
            if (dt > 0.001f)
            {
                // Smoothed screen velocity of the box centre.
                var vx = (obs.Box.CenterX - Box.CenterX) / dt;
                var vy = (obs.Box.CenterY - Box.CenterY) / dt;
                _velocityX = (0.5f * _velocityX) + (0.5f * vx);
                _velocityY = (0.5f * _velocityY) + (0.5f * vy);
            }
        }

        Box = obs.Box;
        LastSeen = obs.Time;
        LastRole = obs.Role;
        LastConfidence = obs.Confidence;
        MissedFrames = 0;
        _history.Add(obs);
        if (_history.Count > _o.MaxHistory)
        {
            _history.RemoveAt(0);
        }

        if (State == ChestState.Counted)
        {
            return null;
        }

        switch (obs.Role)
        {
            case ObjectRole.ChestClosed:
                ClosedFrames++;
                _closedRun++;
                // Closed again: an open streak was a misdetection (or the lid flickered), start over.
                _openedRun = 0;
                if (State == ChestState.Unconfirmed && ClosedFrames >= _o.MinClosedFrames)
                {
                    Move(ChestState.Closed, obs.Time, "seen closed");
                }
                else if (State == ChestState.Opening && _closedRun >= _o.CancelOpeningClosedFrames)
                {
                    _openingConf.Clear();
                    _openedConf.Clear();
                    Move(ChestState.Closed, obs.Time, "opening interrupted");
                }

                return null;

            case ObjectRole.ChestOpening:
                _closedRun = 0;
                if (State is ChestState.Closed or ChestState.Unconfirmed or ChestState.Opening)
                {
                    _openingConf.Add(obs.Confidence);
                }

                var mayOpen = State == ChestState.Closed || (State == ChestState.Unconfirmed && !_o.RequireClosedBeforeOpening);
                if (mayOpen && OpeningFrames >= _o.MinOpeningFrames)
                {
                    Move(ChestState.Opening, obs.Time, "opening animation confirmed");
                }

                return null;

            case ObjectRole.ChestOpened:
                _closedRun = 0;
                if (State == ChestState.Opening)
                {
                    _openedConf.Add(obs.Confidence);
                    if (OpenedFrames >= _o.MinOpenedFrames)
                    {
                        var conf = Confidence(closedBonus: true);
                        return Complete(obs, DetectionOutcomeFor(conf), conf, "opened after opening animation");
                    }

                    return null;
                }

                if (State == ChestState.Closed)
                {
                    if (_openedRun == 0 || obs.Time - _openedRunStart > _o.ConfirmationWindow)
                    {
                        // New open streak (or the previous one was too slow to be one opening).
                        _openedRun = 0;
                        _openedConf.Clear();
                        _openedRunStart = obs.Time;
                    }

                    _openedRun++;
                    _openedConf.Add(obs.Confidence);
                    if (_openedRun < _o.MinOpenedFrames)
                    {
                        return null;
                    }

                    if (!_o.ModelHasOpeningClass)
                    {
                        // The model cannot see the animation: closed → opened on one track is all the evidence there is.
                        // Scored on the best open frames; weak early frames wait for a few more before deciding.
                        var conf = Math.Clamp(_openedConf.OrderByDescending(c => c).Take(_o.MinOpenedFrames).Average(), 0, 1);
                        if (conf < _o.ConfirmThreshold && _openedRun < Math.Max(_o.MinOpenedFrames, _o.MaxOpenedFrames))
                        {
                            return null;
                        }

                        return Complete(obs, DetectionOutcomeFor(conf), conf, "closed → opened");
                    }

                    // Seen closed, then open, but the animation itself was not captured (low frame rate):
                    // real evidence of a transition, but weaker → only ever queued for review.
                    var weak = Confidence(closedBonus: false) * 0.8;
                    var outcome = weak >= _o.ReviewThreshold ? DetectionOutcome.Uncertain : DetectionOutcome.Discarded;
                    return Complete(obs, outcome, weak, "closed → opened without the opening animation");
                }

                if (State == ChestState.Unconfirmed)
                {
                    // Open before it was ever confirmed closed: an already-open chest. Never counted.
                    Move(ChestState.Opened, obs.Time, "already open when first seen: not counted");
                }

                return null;

            default:
                return null;
        }
    }

    private double Confidence(bool closedBonus)
    {
        var all = _openingConf.Concat(_openedConf).ToList();
        var mean = all.Count == 0 ? 0 : all.Average();
        return Math.Clamp(closedBonus && ClosedFrames > 0 ? mean : mean * 0.9, 0, 1);
    }

    private DetectionOutcome DetectionOutcomeFor(double confidence) =>
        confidence >= _o.ConfirmThreshold ? DetectionOutcome.Confirmed
        : confidence >= _o.ReviewThreshold ? DetectionOutcome.Uncertain
        : DetectionOutcome.Discarded;

    private ChestOpenEvent Complete(FrameObservation obs, DetectionOutcome outcome, double confidence, string reason)
    {
        if (State is ChestState.Opening or ChestState.Closed)
        {
            Move(ChestState.Opened, obs.Time, reason);
        }

        Move(ChestState.Counted, obs.Time, outcome.ToString().ToLowerInvariant());
        var evidence = new ChestEvidence(Id, ClosedFrames, OpeningFrames, OpenedFrames,
            _openingConf.Count == 0 ? 0 : _openingConf.Average(), _openedConf.Count == 0 ? 0 : _openedConf.Average(), _transitions.ToList());
        return new ChestOpenEvent(Id, outcome, confidence, obs.Time, obs.Box, evidence);
    }

    private void Move(ChestState to, DateTimeOffset time, string reason)
    {
        if (State == to)
        {
            return;
        }

        _transitions.Add(new StateTransition(time, State, to, reason));
        State = to;
    }
}

/// <summary>Result of one frame.</summary>
/// <param name="Transitions">State changes in this frame (diagnostic log).</param>
/// <param name="Rejected">Chest detections not used in this frame, with the reason (diagnostic log).</param>
public sealed record TrackingUpdate(
    IReadOnlyList<ChestTrack> Tracks,
    IReadOnlyList<ChestOpenEvent> Events,
    IReadOnlyList<Detection> Containers,
    int IgnoredDetections,
    IReadOnlyList<TrackTransition>? Transitions = null,
    IReadOnlyList<RejectedDetection>? Rejected = null);

/// <summary>
/// Follows chests across frames and runs each through the Unconfirmed → Closed → [Opening] → Opened → Counted state
/// machine. Association: IoU against the last and the motion-predicted box first; during fast camera turns a
/// same-class detection may continue a track by centre distance, but only when the match is unambiguous (two nearby
/// chests are never swapped) and never across a closed/open change (a closed chest and a different open chest are
/// not one opening). A chest that disappears is simply dropped: disappearance alone never counts.
/// Each track counts at most once; there is no global cooldown, so different chests count in quick succession.
/// Other containers (ammo boxes...) are reported but never tracked as chests.
/// Not thread-safe: call from one thread (the inference worker).
/// </summary>
public sealed class ChestTrackManager
{
    private readonly List<ChestTrack> _tracks = [];
    private readonly List<(Box Box, DateTimeOffset Time)> _counted = [];
    private int _nextId = 1;

    public ChestTrackManager(ChestTrackingOptions? options = null) => Options = options ?? new ChestTrackingOptions();

    public ChestTrackingOptions Options { get; set; }

    public IReadOnlyList<ChestTrack> Tracks => _tracks;

    public void Reset()
    {
        _tracks.Clear();
        _counted.Clear();
    }

    public TrackingUpdate Update(IReadOnlyList<Detection> detections, DateTimeOffset now)
    {
        var o = Options;
        var rejected = new List<RejectedDetection>();
        var chests = new List<Detection>();
        foreach (var d in detections.Where(d => d.IsChest).OrderByDescending(d => d.Confidence))
        {
            var min = o.MinConfidenceFor(d.Role);
            if (d.Confidence < min)
            {
                rejected.Add(new RejectedDetection(d, string.Create(CultureInfo.InvariantCulture, $"confidence {d.Confidence:0.000} below {min:0.000}")));
            }
            else if (chests.FirstOrDefault(k => k.Role != d.Role && k.Box.IoU(d.Box) >= o.DuplicateIoU) is { } winner)
            {
                // Same chest reported as closed and open in one frame: keep only the more confident state.
                rejected.Add(new RejectedDetection(d, $"same chest as a more confident {winner.Label}"));
            }
            else
            {
                chests.Add(d);
            }
        }

        var containers = detections.Where(d => d.Role == ObjectRole.OtherContainer && d.Confidence >= o.MinDetectionConfidence).ToList();
        var ignored = detections.Count - chests.Count - containers.Count - rejected.Count;

        var transitionsBefore = _tracks.ToDictionary(t => t, t => t.Transitions.Count);
        var (pairs, ambiguous) = Candidates(chests, now, o);
        var usedTracks = new HashSet<ChestTrack>();
        var usedDets = new HashSet<Detection>();
        var events = new List<ChestOpenEvent>();
        foreach (var (_, track, det) in pairs.OrderByDescending(p => p.Score))
        {
            if (usedTracks.Contains(track) || usedDets.Contains(det))
            {
                continue;
            }

            usedTracks.Add(track);
            usedDets.Add(det);
            if (track.Observe(new FrameObservation(now, det.Role, det.Confidence, det.Box)) is { } ev)
            {
                events.Add(ev);
            }
        }

        _counted.RemoveAll(c => now - c.Time > o.CountedMemory || c.Time > now);
        var created = new List<ChestTrack>();
        foreach (var det in chests.Where(d => !usedDets.Contains(d)))
        {
            if (ambiguous.Contains(det))
            {
                rejected.Add(new RejectedDetection(det, "ambiguous: near several tracked chests, started a new track"));
            }

            var recentlyCounted = _counted.Any(c => c.Box.IoU(det.Box) >= o.CountedMemoryIoU);
            var obs = new FrameObservation(now, det.Role, det.Confidence, det.Box);
            var track = new ChestTrack(_nextId++, obs, o, recentlyCounted);
            _tracks.Add(track);
            created.Add(track);
            if (track.Observe(obs) is { } ev)
            {
                events.Add(ev);
            }
        }

        foreach (var t in _tracks.Where(t => !usedTracks.Contains(t) && t.LastSeen != now))
        {
            t.MissedFrames++;
        }

        _tracks.RemoveAll(t => t.MissedFrames > o.MaxMissedFrames || now - t.LastSeen > o.MaxMissedTime);

        foreach (var ev in events)
        {
            _counted.Add((ev.Box, ev.Timestamp));
        }

        var transitions = new List<TrackTransition>();
        foreach (var t in usedTracks.Concat(created))
        {
            var from = transitionsBefore.TryGetValue(t, out var n) ? n : 0;
            for (var i = from; i < t.Transitions.Count; i++)
            {
                transitions.Add(new TrackTransition(t.Id, t.Transitions[i], t.Box, t.LastConfidence));
            }
        }

        return new TrackingUpdate(_tracks.ToList(), events, containers, ignored, transitions, rejected);
    }

    /// <summary>
    /// Track/detection pairs with a score (overlap matches score above 1, distance matches below 1), plus the
    /// detections whose only matches were ambiguous.
    /// </summary>
    private (List<(float Score, ChestTrack Track, Detection Det)> Pairs, HashSet<Detection> Ambiguous) Candidates(
        List<Detection> chests, DateTimeOffset now, ChestTrackingOptions o)
    {
        var pairs = new List<(float Score, ChestTrack Track, Detection Det)>();
        var distance = new List<(float Dist, ChestTrack Track, Detection Det)>();
        foreach (var t in _tracks)
        {
            var predicted = t.PredictedBox(now);
            foreach (var d in chests)
            {
                var iou = Math.Max(t.Box.IoU(d.Box), predicted.IoU(d.Box));
                if (iou >= o.MatchIoU)
                {
                    pairs.Add((1 + iou, t, d));
                    continue;
                }

                // Distance-only continuation: same class only (a closed → open change must be seen in place).
                if (t.LastRole != d.Role || !SimilarSize(t.Box, d.Box, o.MaxSizeRatio))
                {
                    continue;
                }

                var dist = CenterDistance(t, d, now);
                if (dist <= o.MaxCenterDistance)
                {
                    distance.Add((dist, t, d));
                }
            }
        }

        var ambiguous = new HashSet<Detection>();
        foreach (var c in distance)
        {
            if (pairs.Any(p => p.Score > 1 && (p.Det == c.Det || p.Track == c.Track)))
            {
                continue; // an overlap match exists for one side: the distance match is not needed
            }

            // Unambiguous only if every other chest near this detection, and every other detection near this
            // track, is clearly farther away.
            var rivals = _tracks.Where(t => t != c.Track).Select(t => CenterDistance(t, c.Det, now))
                .Concat(distance.Where(x => x.Track == c.Track && x.Det != c.Det).Select(x => x.Dist));
            if (rivals.Any(r => r < c.Dist * o.AmbiguityRatio))
            {
                ambiguous.Add(c.Det);
                continue;
            }

            pairs.Add((1 - (c.Dist / (o.MaxCenterDistance + 1)), c.Track, c.Det));
        }

        return (pairs, ambiguous);
    }

    private static float CenterDistance(ChestTrack t, Detection d, DateTimeOffset now)
    {
        var p = t.PredictedBox(now);
        var diagonal = MathF.Sqrt((t.Box.Width * t.Box.Width) + (t.Box.Height * t.Box.Height));
        return diagonal <= 0 ? float.MaxValue : MathF.Sqrt(Sq(p.CenterX - d.Box.CenterX) + Sq(p.CenterY - d.Box.CenterY)) / diagonal;
    }

    private static bool SimilarSize(Box a, Box b, float maxRatio) =>
        Ratio(a.Width, b.Width) <= maxRatio && Ratio(a.Height, b.Height) <= maxRatio;

    private static float Ratio(float a, float b) => a <= 0 || b <= 0 ? float.MaxValue : Math.Max(a, b) / Math.Min(a, b);

    private static float Sq(float v) => v * v;
}
