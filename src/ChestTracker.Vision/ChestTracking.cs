using System.Globalization;
using ChestTracker.Core.Detection;

namespace ChestTracker.Vision;

/// <summary>Per-chest state. Only Opening → Opened (seen in several frames each) leads to a count.</summary>
public enum ChestState
{
    /// <summary>New track, not enough frames yet.</summary>
    Unconfirmed,
    Closed,
    Opening,

    /// <summary>Open, but not counted: it was never seen closed/opening (already open when it came into view).</summary>
    Opened,

    /// <summary>The opening was decided (counted, queued for review or discarded). Never evaluated again.</summary>
    Counted,
}

public sealed record ChestTrackingOptions
{
    /// <summary>Minimum IoU to continue a track in the next frame.</summary>
    public float MatchIoU { get; init; } = 0.3f;

    /// <summary>A track survives this many frames without a detection (occlusion, loot burst).</summary>
    public int MaxMissedFrames { get; init; } = 8;

    public TimeSpan MaxMissedTime { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Detections below this confidence are ignored entirely.</summary>
    public float MinDetectionConfidence { get; init; } = 0.35f;

    public int MinClosedFrames { get; init; } = 1;

    /// <summary>The opening animation must be seen in at least this many frames.</summary>
    public int MinOpeningFrames { get; init; } = 2;

    /// <summary>After the opening, the open chest must be seen in at least this many frames.</summary>
    public int MinOpenedFrames { get; init; } = 2;

    /// <summary>An opening only counts for a chest that was first seen closed (not one that came into view mid-animation).</summary>
    public bool RequireClosedBeforeOpening { get; init; } = true;

    /// <summary>Closed frames in a row that cancel an opening (interaction interrupted).</summary>
    public int CancelOpeningClosedFrames { get; init; } = 3;

    public double ConfirmThreshold { get; init; } = 0.6;
    public double ReviewThreshold { get; init; } = 0.4;

    /// <summary>A new track overlapping a chest counted this recently is the same chest (track was lost and re-created).</summary>
    public TimeSpan CountedMemory { get; init; } = TimeSpan.FromSeconds(10);

    public float CountedMemoryIoU { get; init; } = 0.3f;

    public int MaxHistory { get; init; } = 64;
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

/// <summary>One tracked chest. All state changes go through <see cref="Observe"/>.</summary>
public sealed class ChestTrack
{
    private readonly ChestTrackingOptions _o;
    private readonly List<FrameObservation> _history = [];
    private readonly List<StateTransition> _transitions = [];
    private readonly List<float> _openingConf = [];
    private readonly List<float> _openedConf = [];
    private int _closedRun;
    private int _openedWithoutOpening;

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

    /// <summary>Applies one frame; returns an event when this frame completes an opening.</summary>
    internal ChestOpenEvent? Observe(FrameObservation obs)
    {
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
                    // Seen closed, then open, but the animation itself was not captured (low frame rate):
                    // real evidence of a transition, but weaker → only ever queued for review.
                    _openedWithoutOpening++;
                    _openedConf.Add(obs.Confidence);
                    if (_openedWithoutOpening >= _o.MinOpenedFrames)
                    {
                        var conf = Confidence(closedBonus: false) * 0.8;
                        var outcome = conf >= _o.ReviewThreshold ? DetectionOutcome.Uncertain : DetectionOutcome.Discarded;
                        return Complete(obs, outcome, conf, "closed → opened without the opening animation");
                    }

                    return null;
                }

                if (State == ChestState.Unconfirmed)
                {
                    _openedWithoutOpening++;
                    if (_openedWithoutOpening >= _o.MinOpenedFrames)
                    {
                        Move(ChestState.Opened, obs.Time, "already open when first seen: not counted");
                    }
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
public sealed record TrackingUpdate(
    IReadOnlyList<ChestTrack> Tracks,
    IReadOnlyList<ChestOpenEvent> Events,
    IReadOnlyList<Detection> Containers,
    int IgnoredDetections);

/// <summary>
/// Follows chests across frames (greedy IoU matching) and runs each through the
/// Closed → Opening → Opened → Counted state machine. A chest that disappears is simply dropped:
/// disappearance alone never counts. Other containers (ammo boxes...) are reported but never tracked as chests.
/// Not thread-safe: call from one thread (the capture worker).
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
        var chests = detections.Where(d => d.IsChest && d.Confidence >= o.MinDetectionConfidence).ToList();
        var containers = detections.Where(d => d.Role == ObjectRole.OtherContainer && d.Confidence >= o.MinDetectionConfidence).ToList();
        var ignored = detections.Count - chests.Count - containers.Count;

        // Greedy matching, best IoU first.
        var pairs = new List<(float IoU, ChestTrack Track, Detection Det)>();
        foreach (var t in _tracks)
        {
            foreach (var d in chests)
            {
                var iou = t.Box.IoU(d.Box);
                if (iou >= o.MatchIoU)
                {
                    pairs.Add((iou, t, d));
                }
            }
        }

        var usedTracks = new HashSet<ChestTrack>();
        var usedDets = new HashSet<Detection>();
        var events = new List<ChestOpenEvent>();
        foreach (var (_, track, det) in pairs.OrderByDescending(p => p.IoU))
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
        foreach (var det in chests.Where(d => !usedDets.Contains(d)))
        {
            var recentlyCounted = _counted.Any(c => c.Box.IoU(det.Box) >= o.CountedMemoryIoU);
            var obs = new FrameObservation(now, det.Role, det.Confidence, det.Box);
            var track = new ChestTrack(_nextId++, obs, o, recentlyCounted);
            _tracks.Add(track);
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

        return new TrackingUpdate(_tracks.ToList(), events, containers, ignored);
    }
}
