using System.Diagnostics;
using ChestTracker.Core.Detection;
using ChestTracker.Core.Tracking;

namespace ChestTracker.Vision;

/// <summary>Identifies the counting period a frame was captured in: the local match and the tracking generation.</summary>
public readonly record struct FrameTicket(Guid MatchId, long Generation);

/// <summary>An opening found in a frame and what the engine did with it ("counted", "stale", ...).</summary>
public sealed record AppliedOpening(ChestOpenEvent Opening, string Result);

public sealed record DetectionFrameResult(PipelineFrame Frame, IReadOnlyList<AppliedOpening> Openings);

/// <summary>
/// Ties YOLO chest detection to the match state of the <see cref="TrackingEngine"/>, the single authoritative state
/// that the match button, the match hotkey and the overlay also use:
/// <list type="bullet">
/// <item>Counting is active only while a local match is running. Each match start or end begins a new generation and
/// clears all chest tracks, so the next match never sees tracks (or half-confirmed openings) of the previous one.</item>
/// <item>A frame gets a <see cref="FrameTicket"/> when it is captured. After the (slow) inference, the frame is only
/// tracked if its ticket is still current, so results of a frame that was in flight when the player stopped are
/// dropped.</item>
/// <item>Openings are stored with the ticket's match id; the engine refuses them if that match is no longer the
/// current one (checked under the engine lock), which closes the last race with Start/End Match.</item>
/// </list>
/// The model (pipeline) is loaded once and reused for every match.
/// </summary>
public sealed class MatchDetectionController : IDisposable
{
    private readonly TrackingEngine _engine;
    private readonly object _gate = new();
    private YoloChestPipeline? _pipeline;
    private Guid? _match;
    private long _generation;

    public MatchDetectionController(TrackingEngine engine)
    {
        _engine = engine;
        _match = engine.CurrentMatch?.Id;
        engine.Changed += OnEngineChanged;
    }

    /// <summary>Raised (on the thread that changed the match) when counting starts or stops.</summary>
    public event EventHandler? CountingChanged;

    /// <summary>The local match that detections are counted into, or null when counting is stopped.</summary>
    public Guid? CountingMatch
    {
        get { lock (_gate) return _match; }
    }

    public bool IsCounting => CountingMatch is not null;

    public long Generation
    {
        get { lock (_gate) return _generation; }
    }

    /// <summary>The loaded model with its tracker. Setting it starts a new generation.</summary>
    public YoloChestPipeline? Pipeline
    {
        get { lock (_gate) return _pipeline; }
        set
        {
            lock (_gate)
            {
                _pipeline = value;
                _pipeline?.Reset();
                _generation++;
            }
        }
    }

    /// <summary>Called when a frame is captured. Null when counting is stopped: the frame is not analysed at all.</summary>
    public FrameTicket? Admit()
    {
        lock (_gate)
        {
            return _match is { } m && _pipeline is not null ? new FrameTicket(m, _generation) : null;
        }
    }

    /// <summary>
    /// Runs the model on a frame and tracks its chests. Returns null when the frame belongs to a counting period that
    /// has ended (before or during inference). <paramref name="apply"/> stores an opening for the ticket's match and
    /// returns the result text.
    /// </summary>
    public DetectionFrameResult? Process(FrameTicket ticket, ReadOnlySpan<byte> bgra, int width, int height, int stride,
        DateTimeOffset timestamp, Func<DetectionDecision, Guid, string> apply)
    {
        YoloChestPipeline? pipeline;
        lock (_gate)
        {
            if (!IsCurrent(ticket))
            {
                return null;
            }

            pipeline = _pipeline;
        }

        if (pipeline is null)
        {
            return null;
        }

        var sw = Stopwatch.StartNew();
        var detections = pipeline.Detect(bgra, width, height, stride);
        PipelineFrame frame;
        lock (_gate)
        {
            // Counting stopped (or restarted) while the model was running: drop this frame entirely.
            if (!IsCurrent(ticket) || !ReferenceEquals(pipeline, _pipeline))
            {
                return null;
            }

            frame = pipeline.Track(detections, timestamp, sw.Elapsed.TotalMilliseconds);
        }

        var applied = frame.Update.Events.Select(ev => new AppliedOpening(ev, apply(ev.ToDecision(), ticket.MatchId))).ToList();
        return new DetectionFrameResult(frame, applied);
    }

    private bool IsCurrent(FrameTicket ticket) => _match == ticket.MatchId && _generation == ticket.Generation;

    private void OnEngineChanged(object? sender, TrackingChangedEventArgs e)
    {
        if (e.Kind is not (TrackingChangeKind.Initialized or TrackingChangeKind.MatchStarted or TrackingChangeKind.MatchEnded
            or TrackingChangeKind.MatchDeleted or TrackingChangeKind.DataReset or TrackingChangeKind.DataImported))
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            var current = e.Snapshot.CurrentMatch?.Id;
            changed = current != _match;
            if (changed)
            {
                _match = current;
                _generation++;
                _pipeline?.Reset(); // fresh tracks for every counting period
            }
        }

        if (changed)
        {
            CountingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() => _engine.Changed -= OnEngineChanged;
}
