using ChestTracker.Core.Models;

namespace ChestTracker.Core.Tracking;

public enum SoundCue
{
    ChestAdded,
    ChestRemoved,
    MatchStarted,
    MatchEnded,
    Uncertain,
}

/// <summary>
/// Decides which notification sound a change of the local <see cref="TrackingEngine"/> plays. This is the only place
/// sounds for chests come from, so the chest-added sound plays only on the computer that created the event:
/// <list type="bullet">
/// <item>The engine raises <see cref="TrackingChangeKind.ChestAdded"/> only from a successful local add (hotkey, button,
/// automatic detection, approved review), after the event is stored. Party/shared-match updates from the server
/// (SignalR), outbox retries, reconnect delivery, reloads and history never add events to the local engine.</item>
/// <item>Each event id plays at most once, even if a change is reported twice.</item>
/// </list>
/// Teammates' chests arrive as server activity and are never routed here: they update counters silently.
/// </summary>
public sealed class LocalSoundRouter
{
    private readonly Action<SoundCue> _play;
    private readonly HashSet<Guid> _played = [];
    private readonly Queue<Guid> _order = new();

    public LocalSoundRouter(Action<SoundCue> play) => _play = play;

    /// <summary>Plays the cue for a local change (if any). Returns the cue played.</summary>
    public SoundCue? Handle(TrackingChangedEventArgs e)
    {
        var cue = CueFor(e);
        if (cue is null)
        {
            return null;
        }

        if (cue == SoundCue.ChestAdded && e.Event is { } ev && !Remember(ev.EventId))
        {
            return null;
        }

        _play(cue.Value);
        return cue;
    }

    /// <summary>Plays sounds for every change of <paramref name="engine"/> (tests and headless use).</summary>
    public static LocalSoundRouter Attach(TrackingEngine engine, Action<SoundCue> play)
    {
        var router = new LocalSoundRouter(play);
        engine.Changed += (_, e) =>
        {
            lock (router._played)
            {
                router.Handle(e);
            }
        };
        return router;
    }

    public static SoundCue? CueFor(TrackingChangedEventArgs e) => e.Kind switch
    {
        TrackingChangeKind.ChestAdded when e.Event is { Kind: ChestEventKind.Add } => SoundCue.ChestAdded,
        TrackingChangeKind.ChestRemoved => SoundCue.ChestRemoved,
        TrackingChangeKind.MatchStarted => SoundCue.MatchStarted,
        TrackingChangeKind.MatchEnded => SoundCue.MatchEnded,
        TrackingChangeKind.ReviewAdded => SoundCue.Uncertain,
        TrackingChangeKind.ReviewResolved when e.Event is { Kind: ChestEventKind.Add } => SoundCue.ChestAdded,
        _ => null,
    };

    private bool Remember(Guid id)
    {
        if (!_played.Add(id))
        {
            return false;
        }

        _order.Enqueue(id);
        if (_order.Count > 512)
        {
            _played.Remove(_order.Dequeue());
        }

        return true;
    }
}
