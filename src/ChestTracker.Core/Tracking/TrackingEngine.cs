using ChestTracker.Core.Abstractions;
using ChestTracker.Core.Data;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Core.Models;

namespace ChestTracker.Core.Tracking;

/// <summary>
/// The single entry point for every change to chest statistics. Writes go straight to the
/// event log (one durable SQLite write per action), counters are derived from it.
/// Thread-safe: hotkeys (UI thread) and the detector (worker thread) may call concurrently.
/// <see cref="Changed"/> is raised on the calling thread, outside the internal lock.
/// </summary>
public sealed class TrackingEngine
{
    private const int MaxCount = 9999;

    private readonly object _sync = new();
    private readonly ChestRepository _repo;
    private readonly IClock _clock;
    private readonly DeduplicationPolicy _dedup = new();
    private TrackingOptions _options;
    private TrackingMode _mode;
    private bool _initialized;
    private DateTimeOffset? _lastMatchControl;

    public TrackingEngine(ChestRepository repository, IClock clock, TrackingOptions? options = null,
        TrackingMode mode = TrackingMode.Manual)
    {
        _repo = repository;
        _clock = clock;
        _options = options ?? new TrackingOptions();
        _mode = mode;
    }

    public event EventHandler<TrackingChangedEventArgs>? Changed;

    public ChestRepository Repository => _repo;

    public Guid SessionId { get; private set; }

    public MatchRecord? CurrentMatch
    {
        get { lock (_sync) return _currentMatch; }
    }

    private MatchRecord? _currentMatch;

    public TrackingMode Mode
    {
        get { lock (_sync) return _mode; }
    }

    public TrackingOptions Options
    {
        get { lock (_sync) return _options; }
        set { lock (_sync) _options = value ?? new TrackingOptions(); }
    }

    /// <summary>Starts a new session and repairs state left by an unexpected shutdown.</summary>
    public RecoveryReport Initialize()
    {
        RecoveryReport report;
        lock (_sync)
        {
            if (_initialized)
            {
                throw new InvalidOperationException("Engine already initialized.");
            }

            var now = _clock.UtcNow;
            var session = new SessionRecord { StartedAt = now };
            _repo.InsertSession(session);
            SessionId = session.Id;

            var closed = 0;
            MatchRecord? resumed = null;
            foreach (var open in _repo.GetOpenMatches())
            {
                var lastActivity = _repo.GetLastEventTime(open.Id) ?? open.StartedAt;
                if (lastActivity < open.StartedAt)
                {
                    lastActivity = open.StartedAt;
                }

                if (resumed is null && _options.ResumeWindow > TimeSpan.Zero && now - lastActivity <= _options.ResumeWindow)
                {
                    _repo.ReassignMatchSession(open.Id, SessionId);
                    resumed = open with { SessionId = SessionId };
                    continue;
                }

                _repo.EndMatch(open.Id, lastActivity, recovered: true);
                closed++;
            }

            var closedSessions = _repo.CloseStaleSessions(SessionId);
            _currentMatch = resumed;
            _initialized = true;
            report = new RecoveryReport(closed, resumed?.Id, closedSessions);
        }

        if (report.ClosedMatches > 0 || report.ResumedMatchId is not null)
        {
            Log.Info($"Recovery: closed {report.ClosedMatches} unfinished match(es), resumed {report.ResumedMatchId?.ToString() ?? "none"}");
        }

        Raise(TrackingChangeKind.Initialized, null);
        return report;
    }

    public void SetMode(TrackingMode mode)
    {
        lock (_sync)
        {
            if (_mode == mode)
            {
                return;
            }

            _mode = mode;
            _dedup.Reset();
            if (_currentMatch is { } match && match.Mode != mode)
            {
                // A match that already has counted chests under another mode is recorded as hybrid.
                var hasEvents = _repo.GetLastEventTime(match.Id) is not null;
                var newMode = hasEvents ? TrackingMode.Hybrid : mode;
                _repo.UpdateMatchMode(match.Id, newMode);
                _currentMatch = match with { Mode = newMode };
            }
        }

        Raise(TrackingChangeKind.ModeChanged, null);
    }

    public MatchRecord StartMatch()
    {
        MatchRecord match;
        bool endedPrevious;
        lock (_sync)
        {
            EnsureInitialized();
            var now = _clock.UtcNow;
            endedPrevious = EndMatchCore(now) is not null;
            match = StartMatchCore(now);
        }

        if (endedPrevious)
        {
            Raise(TrackingChangeKind.MatchEnded, null);
        }

        Raise(TrackingChangeKind.MatchStarted, null);
        return match;
    }

    public MatchSummary? EndMatch()
    {
        MatchSummary? summary;
        lock (_sync)
        {
            EnsureInitialized();
            summary = EndMatchCore(_clock.UtcNow);
        }

        if (summary is not null)
        {
            Raise(TrackingChangeKind.MatchEnded, null);
        }

        return summary;
    }

    /// <summary>The single Start/End Match button: ends the active match, or starts one when none is active.</summary>
    public MatchControlResult ToggleMatch() => ControlMatch(start: null);

    /// <summary>Start-match hotkey/command: starts a new match (ending the active one), ignoring a double press.</summary>
    public MatchControlResult RequestStartMatch() => ControlMatch(start: true);

    /// <summary>End-match hotkey/command, ignoring a double press.</summary>
    public MatchControlResult RequestEndMatch() => ControlMatch(start: false);

    private MatchControlResult ControlMatch(bool? start)
    {
        MatchControlResult result;
        bool ended, started;
        lock (_sync)
        {
            EnsureInitialized();
            var now = _clock.UtcNow;
            if (_lastMatchControl is { } last && now >= last && now - last < _options.MatchControlGuard)
            {
                return new MatchControlResult(MatchControlOutcome.IgnoredDoubleClick, _currentMatch);
            }

            if (!(start ?? _currentMatch is null))
            {
                var summary = EndMatchCore(now);
                if (summary is null)
                {
                    return new MatchControlResult(MatchControlOutcome.NoActiveMatch);
                }

                ended = true;
                started = false;
                result = new MatchControlResult(MatchControlOutcome.Ended, summary.Match, summary);
            }
            else
            {
                var previous = EndMatchCore(now);
                ended = previous is not null;
                started = true;
                result = new MatchControlResult(MatchControlOutcome.Started, StartMatchCore(now), previous);
            }

            _lastMatchControl = now;
        }

        if (ended)
        {
            Raise(TrackingChangeKind.MatchEnded, null);
        }

        if (started)
        {
            Raise(TrackingChangeKind.MatchStarted, null);
        }

        return result;
    }

    public TrackingResult AddChestManual(bool fromHotkey) =>
        AddChest(EventSource.Manual, null, null, fromHotkey, bypassDedup: false, matchId: null);

    /// <param name="trackedIdentity">
    /// True for the YOLO tracker, which emits at most one opening per tracked chest: the global automatic cooldown
    /// (meant for the heuristic detector seeing one animation twice) is skipped, the manual/automatic merge is kept.
    /// </param>
    /// <param name="expectedMatchId">
    /// The local match the detection belongs to. If that match is no longer the current one (it was ended, or a new
    /// one started while the frame was being analysed), nothing is stored: a stale detection never lands in the wrong
    /// match. Checked under the engine lock, so it is atomic with Start/End Match.
    /// </param>
    public TrackingResult AddChestAutomatic(double confidence, DateTimeOffset? detectedAt = null, bool trackedIdentity = false,
        Guid? expectedMatchId = null) =>
        AddChest(EventSource.Automatic, Math.Clamp(confidence, 0, 1), detectedAt, fromHotkey: false, bypassDedup: false, matchId: null,
            trackedIdentity, expectedMatchId);

    /// <summary>Removes the last chest of the current match by appending an undo event.</summary>
    public TrackingResult RemoveLastChest(bool fromHotkey)
    {
        ChestEvent ev;
        lock (_sync)
        {
            EnsureInitialized();
            var now = _clock.UtcNow;
            if (fromHotkey)
            {
                if (!_dedup.CheckRemoveHotkey(now, _options))
                {
                    return new TrackingResult(TrackingResultCode.IgnoredDebounce);
                }

                _dedup.RecordRemoveHotkey(now);
            }

            if (_currentMatch is not { } match)
            {
                return new TrackingResult(TrackingResultCode.NoActiveMatch);
            }

            if (_repo.GetMatchTotal(match.Id) <= 0)
            {
                return new TrackingResult(TrackingResultCode.NothingToRemove);
            }

            var last = _repo.GetLastUndoableAdd(match.Id);
            ev = last is not null
                ? new ChestEvent
                {
                    MatchId = match.Id, Timestamp = now, Kind = ChestEventKind.Undo, Source = EventSource.Manual,
                    Delta = -1, UndoesEventId = last.EventId,
                }
                : new ChestEvent
                {
                    // Count is positive only because of a correction: compensate with another correction.
                    MatchId = match.Id, Timestamp = now, Kind = ChestEventKind.Correction, Source = EventSource.Manual,
                    Delta = -1,
                };

            if (!_repo.InsertEvent(ev))
            {
                return new TrackingResult(TrackingResultCode.Rejected);
            }
        }

        Raise(TrackingChangeKind.ChestRemoved, ev);
        return new TrackingResult(TrackingResultCode.Applied, ev);
    }

    /// <summary>Sets the chest count of the current match (manual edit / reset).</summary>
    public TrackingResult SetCurrentMatchCount(int value)
    {
        Guid id;
        lock (_sync)
        {
            if (_currentMatch is null)
            {
                return new TrackingResult(TrackingResultCode.NoActiveMatch);
            }

            id = _currentMatch.Id;
        }

        return SetMatchCount(id, value);
    }

    public TrackingResult ResetCurrentMatch() => SetCurrentMatchCount(0);

    /// <summary>Sets the chest count of any match by appending a correction event.</summary>
    public TrackingResult SetMatchCount(Guid matchId, int value)
    {
        ChestEvent ev;
        lock (_sync)
        {
            EnsureInitialized();
            if (_repo.GetMatch(matchId) is null)
            {
                return new TrackingResult(TrackingResultCode.NotFound);
            }

            value = Math.Clamp(value, 0, MaxCount);
            var delta = value - _repo.GetMatchTotal(matchId);
            if (delta == 0)
            {
                return new TrackingResult(TrackingResultCode.Applied);
            }

            ev = new ChestEvent
            {
                MatchId = matchId, Timestamp = _clock.UtcNow, Kind = ChestEventKind.Correction,
                Source = EventSource.Manual, Delta = delta, Note = $"set={value}",
            };
            _repo.InsertEvent(ev);
        }

        Raise(TrackingChangeKind.Corrected, ev);
        return new TrackingResult(TrackingResultCode.Applied, ev);
    }

    /// <summary>Adds chests that were opened before tracking started (not tied to a match).</summary>
    public TrackingResult AdjustLifetime(int delta)
    {
        ChestEvent ev;
        lock (_sync)
        {
            EnsureInitialized();
            var lifetime = _repo.GetLifetimeTotal();
            delta = Math.Max(delta, -lifetime);
            if (delta == 0)
            {
                return new TrackingResult(TrackingResultCode.Applied);
            }

            ev = new ChestEvent
            {
                MatchId = null, Timestamp = _clock.UtcNow, Kind = ChestEventKind.Correction,
                Source = EventSource.Manual, Delta = delta, Note = "lifetime",
            };
            _repo.InsertEvent(ev);
        }

        Raise(TrackingChangeKind.Corrected, ev);
        return new TrackingResult(TrackingResultCode.Applied, ev);
    }

    public bool DeleteMatch(Guid matchId)
    {
        bool deleted;
        lock (_sync)
        {
            EnsureInitialized();
            deleted = _repo.DeleteMatch(matchId);
            if (deleted && _currentMatch?.Id == matchId)
            {
                _currentMatch = null;
                _dedup.Reset();
            }
        }

        if (deleted)
        {
            Raise(TrackingChangeKind.MatchDeleted, null);
        }

        return deleted;
    }

    /// <returns>
    /// The queued review, or null when no match is running (counting stopped) or <paramref name="expectedMatchId"/>
    /// is no longer the current match.
    /// </returns>
    public DetectionReview? AddPendingReview(double confidence, DateTimeOffset? at = null, Guid? expectedMatchId = null)
    {
        DetectionReview review;
        lock (_sync)
        {
            EnsureInitialized();
            if (_currentMatch is null || (expectedMatchId is { } expected && _currentMatch.Id != expected))
            {
                return null;
            }

            review = new DetectionReview
            {
                MatchId = _currentMatch?.Id,
                Timestamp = at ?? _clock.UtcNow,
                Confidence = Math.Clamp(confidence, 0, 1),
                Status = ReviewStatus.Pending,
            };
            _repo.InsertReview(review);
        }

        Raise(TrackingChangeKind.ReviewAdded, null);
        return review;
    }

    /// <summary>Accepting a review is an explicit user decision, so it bypasses cooldown and merge rules.</summary>
    public TrackingResult ResolveReview(Guid reviewId, bool accept)
    {
        TrackingResult result;
        lock (_sync)
        {
            EnsureInitialized();
            var review = _repo.GetReview(reviewId);
            if (review is null || review.Status != ReviewStatus.Pending)
            {
                return new TrackingResult(TrackingResultCode.NotFound);
            }

            var now = _clock.UtcNow;
            if (!accept)
            {
                _repo.ResolveReview(reviewId, ReviewStatus.Rejected, now, null);
                result = new TrackingResult(TrackingResultCode.Applied);
            }
            else
            {
                // The review belongs to the match it was detected in (also after that match ended).
                var matchId = review.MatchId is { } mid && _repo.GetMatch(mid) is not null ? mid : _currentMatch?.Id;
                if (matchId is null)
                {
                    return new TrackingResult(TrackingResultCode.NoActiveMatch);
                }

                var ev = new ChestEvent
                {
                    MatchId = matchId, Timestamp = review.Timestamp, Kind = ChestEventKind.Add,
                    Source = EventSource.Automatic, Delta = 1, Confidence = review.Confidence, Note = "reviewed",
                };
                _repo.InsertEvent(ev);
                _repo.ResolveReview(reviewId, ReviewStatus.Accepted, now, ev.EventId);
                result = new TrackingResult(TrackingResultCode.Applied, ev);
            }
        }

        Raise(TrackingChangeKind.ReviewResolved, result.Event);
        return result;
    }

    public IReadOnlyList<DetectionReview> GetPendingReviews() => _repo.GetPendingReviews();

    /// <summary>Deletes all statistics. The current session continues.</summary>
    public void ResetAllData()
    {
        lock (_sync)
        {
            EnsureInitialized();
            var session = _repo.GetSession(SessionId) ?? new SessionRecord { Id = SessionId, StartedAt = _clock.UtcNow };
            _repo.DeleteAllData();
            _repo.InsertSession(session with { EndedAt = null });
            _currentMatch = null;
            _dedup.Reset();
        }

        Raise(TrackingChangeKind.DataReset, null);
    }

    /// <summary>Notifies listeners after data was imported directly through the repository.</summary>
    public void NotifyImported() => Raise(TrackingChangeKind.DataImported, null);

    /// <summary>Ends the current match and the session (application exit).</summary>
    public void Shutdown()
    {
        lock (_sync)
        {
            if (!_initialized)
            {
                return;
            }

            var now = _clock.UtcNow;
            EndMatchCore(now);
            _repo.EndSession(SessionId, now);
            _initialized = false;
        }
    }

    public TrackingSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var aggregate = _repo.GetAggregateStats();
            return new TrackingSnapshot
            {
                CurrentMatch = _currentMatch,
                MatchCount = _currentMatch is null ? 0 : _repo.GetMatchTotal(_currentMatch.Id),
                SessionCount = _initialized ? _repo.GetSessionTotal(SessionId) : 0,
                LifetimeCount = _repo.GetLifetimeTotal(),
                MatchesPlayed = aggregate.MatchesPlayed,
                AveragePerMatch = aggregate.MatchesPlayed == 0 ? 0 : (double)aggregate.ChestsInMatches / aggregate.MatchesPlayed,
                Record = aggregate.Record,
                Mode = _mode,
                PendingReviews = _repo.GetPendingReviews().Count,
            };
        }
    }

    /// <summary>
    /// Counting happens only inside a running match: without one (counting stopped, e.g. after the player died and
    /// pressed the match hotkey) an add is refused and no match is started implicitly.
    /// </summary>
    private TrackingResult AddChest(EventSource source, double? confidence, DateTimeOffset? at, bool fromHotkey,
        bool bypassDedup, Guid? matchId, bool trackedIdentity = false, Guid? expectedMatchId = null)
    {
        ChestEvent ev;
        lock (_sync)
        {
            EnsureInitialized();
            var now = _clock.UtcNow;
            if (matchId is null && _currentMatch is null)
            {
                return new TrackingResult(TrackingResultCode.NoActiveMatch);
            }

            if (expectedMatchId is { } expected && _currentMatch?.Id != expected)
            {
                return new TrackingResult(TrackingResultCode.NoActiveMatch);
            }

            if (!bypassDedup)
            {
                var code = _dedup.CheckAdd(source, fromHotkey, _mode, now, _options, trackedIdentity);
                if (code != TrackingResultCode.Applied)
                {
                    return new TrackingResult(code);
                }
            }

            var target = matchId ?? _currentMatch!.Id;
            if (_repo.GetMatchTotal(target) >= MaxCount)
            {
                return new TrackingResult(TrackingResultCode.Rejected);
            }

            ev = new ChestEvent
            {
                MatchId = target, Timestamp = at ?? now, Kind = ChestEventKind.Add, Source = source, Delta = 1,
                Confidence = confidence,
            };
            if (!_repo.InsertEvent(ev))
            {
                return new TrackingResult(TrackingResultCode.Rejected);
            }

            _dedup.RecordAdd(source, fromHotkey, now);
        }

        Raise(TrackingChangeKind.ChestAdded, ev);
        return new TrackingResult(TrackingResultCode.Applied, ev);
    }

    private MatchRecord StartMatchCore(DateTimeOffset now)
    {
        var match = new MatchRecord { SessionId = SessionId, StartedAt = now, Mode = _mode };
        _repo.InsertMatch(match);
        _currentMatch = match;
        _dedup.Reset();
        return match;
    }

    private MatchSummary? EndMatchCore(DateTimeOffset now)
    {
        if (_currentMatch is not { } match)
        {
            return null;
        }

        _repo.EndMatch(match.Id, now < match.StartedAt ? match.StartedAt : now);
        _currentMatch = null;
        _dedup.Reset();
        return _repo.GetMatchSummary(match.Id);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Call Initialize() first.");
        }
    }

    private void Raise(TrackingChangeKind kind, ChestEvent? ev)
    {
        var handler = Changed;
        if (handler is null)
        {
            return;
        }

        TrackingSnapshot snapshot;
        try
        {
            snapshot = GetSnapshot();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to build tracking snapshot", ex);
            return;
        }

        handler(this, new TrackingChangedEventArgs(kind, ev, snapshot));
    }
}
