namespace ChestTracker.Core.Models;

/// <summary>
/// Immutable entry of the event log. Counters are always derived as SUM(Delta),
/// so an event is never edited: undo and corrections append compensating events.
/// </summary>
public sealed record ChestEvent
{
    /// <summary>Database sequence number (0 until stored).</summary>
    public long Sequence { get; init; }

    /// <summary>Globally unique id; the database rejects a second insert with the same id.</summary>
    public Guid EventId { get; init; } = Guid.NewGuid();

    /// <summary>Owning match, or null for lifetime adjustments.</summary>
    public Guid? MatchId { get; init; }

    public DateTimeOffset Timestamp { get; init; }
    public ChestEventKind Kind { get; init; }
    public EventSource Source { get; init; }
    public int Delta { get; init; }

    /// <summary>Detector confidence (0..1) for automatic events.</summary>
    public double? Confidence { get; init; }

    /// <summary>For <see cref="ChestEventKind.Undo"/>: the event being compensated.</summary>
    public Guid? UndoesEventId { get; init; }

    public string? Note { get; init; }
}

public sealed record MatchRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public TrackingMode Mode { get; init; }

    /// <summary>True when the match was closed automatically after an unexpected shutdown.</summary>
    public bool Recovered { get; init; }

    public string? Note { get; init; }
}

public sealed record SessionRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
}

public sealed record DetectionReview
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? MatchId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public double Confidence { get; init; }
    public ReviewStatus Status { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>Event created when the review was accepted.</summary>
    public Guid? EventId { get; init; }
}

/// <summary>Match with aggregated counters derived from its events.</summary>
public sealed record MatchSummary
{
    public required MatchRecord Match { get; init; }
    public int Chests { get; init; }
    public int ManualAdds { get; init; }
    public int AutomaticAdds { get; init; }
    public int Undos { get; init; }
    public int Corrections { get; init; }

    public Guid Id => Match.Id;
    public bool IsActive => Match.EndedAt is null;

    public TimeSpan DurationAt(DateTimeOffset now)
    {
        var end = Match.EndedAt ?? now;
        var d = end - Match.StartedAt;
        return d < TimeSpan.Zero ? TimeSpan.Zero : d;
    }
}

public sealed record AggregateStats(int MatchesPlayed, int ChestsInMatches, int Record);
