using ChestTracker.Core.Models;

namespace ChestTracker.Core.Tracking;

public sealed record TrackingOptions
{
    /// <summary>Minimum interval between two accepted presses of the same counting hotkey.</summary>
    public TimeSpan ManualDebounce { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Minimum interval between two automatic detections.</summary>
    public TimeSpan AutomaticCooldown { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Hybrid/automatic: a manual hotkey add this close to an automatic add (or vice versa)
    /// is treated as the same chest.
    /// </summary>
    public TimeSpan HybridMergeWindow { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>After a crash, an unfinished match active this recently is resumed instead of closed.</summary>
    public TimeSpan ResumeWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Start/End Match requests closer than this to the previous one are ignored (double-click, double key press),
    /// so one click never starts and immediately ends a match, and two clicks never create two matches.
    /// </summary>
    public TimeSpan MatchControlGuard { get; init; } = TimeSpan.FromSeconds(1);
}

public enum MatchControlOutcome
{
    Started,
    Ended,

    /// <summary>Too soon after the previous Start/End request: nothing changed.</summary>
    IgnoredDoubleClick,

    /// <summary>End requested while no match is active.</summary>
    NoActiveMatch,
}

public sealed record MatchControlResult(MatchControlOutcome Outcome, MatchRecord? Match = null, MatchSummary? Summary = null);

public enum TrackingResultCode
{
    Applied,
    IgnoredDebounce,
    IgnoredCooldown,
    IgnoredDuplicate,
    NothingToRemove,
    NoActiveMatch,
    NotFound,
    Rejected,
}

public sealed record TrackingResult(TrackingResultCode Code, ChestEvent? Event = null)
{
    public bool Applied => Code == TrackingResultCode.Applied;
}

public enum TrackingChangeKind
{
    Initialized,
    ChestAdded,
    ChestRemoved,
    Corrected,
    MatchStarted,
    MatchEnded,
    MatchDeleted,
    ModeChanged,
    ReviewAdded,
    ReviewResolved,
    DataReset,
    DataImported,
}

public sealed record TrackingSnapshot
{
    public MatchRecord? CurrentMatch { get; init; }
    public int MatchCount { get; init; }
    public int SessionCount { get; init; }
    public int LifetimeCount { get; init; }
    public int MatchesPlayed { get; init; }
    public double AveragePerMatch { get; init; }
    public int Record { get; init; }
    public TrackingMode Mode { get; init; }
    public int PendingReviews { get; init; }
}

public sealed class TrackingChangedEventArgs(TrackingChangeKind kind, ChestEvent? chestEvent, TrackingSnapshot snapshot) : EventArgs
{
    public TrackingChangeKind Kind { get; } = kind;
    public ChestEvent? Event { get; } = chestEvent;
    public TrackingSnapshot Snapshot { get; } = snapshot;
}

public sealed record RecoveryReport(int ClosedMatches, Guid? ResumedMatchId, int ClosedSessions);
