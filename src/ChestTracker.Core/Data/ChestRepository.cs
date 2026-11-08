using ChestTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace ChestTracker.Core.Data;

/// <summary>All SQL used by the application. Thread-safe through <see cref="SqliteDatabase"/>.</summary>
public sealed class ChestRepository
{
    private const string EventColumns =
        "seq, event_id, match_id, ts, kind, source, delta, confidence, undoes_event_id, note";

    private const string MatchColumns =
        "m.id, m.session_id, m.started_at, m.ended_at, m.tracking_mode, m.recovered, m.note";

    private const string SummarySelect = $"""
        SELECT {MatchColumns},
               COALESCE(SUM(e.delta), 0),
               COALESCE(SUM(CASE WHEN e.kind = 0 AND e.source = 0 THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN e.kind = 0 AND e.source = 1 THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN e.kind = 1 THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN e.kind = 2 THEN 1 ELSE 0 END), 0)
        FROM matches m
        LEFT JOIN chest_events e ON e.match_id = m.id
        """;

    private readonly SqliteDatabase _db;

    public ChestRepository(SqliteDatabase db) => _db = db;

    public SqliteDatabase Database => _db;

    // ---------------------------------------------------------------- sessions

    public void InsertSession(SessionRecord session) => _db.Run(c => InsertSession(c, null, session, ignoreExisting: false));

    public void EndSession(Guid sessionId, DateTimeOffset endedAt) => _db.Run(c =>
    {
        using var cmd = c.Command("UPDATE sessions SET ended_at = $e WHERE id = $id AND ended_at IS NULL");
        cmd.Bind("$id", sessionId).Bind("$e", endedAt).ExecuteNonQuery();
    });

    /// <summary>Closes sessions left open by a crash, using their last match activity.</summary>
    public int CloseStaleSessions(Guid currentSessionId) => _db.Run(c =>
    {
        using var cmd = c.Command("""
            UPDATE sessions
            SET ended_at = COALESCE(
                (SELECT MAX(COALESCE(m.ended_at, m.started_at)) FROM matches m WHERE m.session_id = sessions.id),
                sessions.started_at)
            WHERE ended_at IS NULL AND id <> $current
            """);
        return cmd.Bind("$current", currentSessionId).ExecuteNonQuery();
    });

    public IReadOnlyList<SessionRecord> GetSessions() => _db.Run(c =>
    {
        using var cmd = c.Command("SELECT id, started_at, ended_at FROM sessions ORDER BY started_at");
        using var r = cmd.ExecuteReader();
        var list = new List<SessionRecord>();
        while (r.Read())
        {
            list.Add(new SessionRecord { Id = r.GetGuidValue(0), StartedAt = r.GetTime(1), EndedAt = r.GetNullableTime(2) });
        }

        return (IReadOnlyList<SessionRecord>)list;
    });

    public SessionRecord? GetSession(Guid id) => GetSessions().FirstOrDefault(s => s.Id == id);

    // ----------------------------------------------------------------- matches

    public void InsertMatch(MatchRecord match) => _db.Run(c => InsertMatch(c, null, match, ignoreExisting: false));

    public bool EndMatch(Guid matchId, DateTimeOffset endedAt, bool recovered = false) => _db.Run(c =>
    {
        using var cmd = c.Command(
            "UPDATE matches SET ended_at = $e, recovered = $r WHERE id = $id AND ended_at IS NULL");
        return cmd.Bind("$id", matchId).Bind("$e", endedAt).Bind("$r", recovered).ExecuteNonQuery() == 1;
    });

    public void UpdateMatchMode(Guid matchId, TrackingMode mode) => _db.Run(c =>
    {
        using var cmd = c.Command("UPDATE matches SET tracking_mode = $m WHERE id = $id");
        cmd.Bind("$id", matchId).Bind("$m", mode).ExecuteNonQuery();
    });

    public void ReassignMatchSession(Guid matchId, Guid sessionId) => _db.Run(c =>
    {
        using var cmd = c.Command("UPDATE matches SET session_id = $s WHERE id = $id");
        cmd.Bind("$id", matchId).Bind("$s", sessionId).ExecuteNonQuery();
    });

    public void UpdateMatchNote(Guid matchId, string? note) => _db.Run(c =>
    {
        using var cmd = c.Command("UPDATE matches SET note = $n WHERE id = $id");
        cmd.Bind("$id", matchId).Bind("$n", string.IsNullOrWhiteSpace(note) ? null : note.Trim()).ExecuteNonQuery();
    });

    /// <summary>Deletes a match; its events and reviews cascade.</summary>
    public bool DeleteMatch(Guid matchId) => _db.Run(c =>
    {
        using var cmd = c.Command("DELETE FROM matches WHERE id = $id");
        return cmd.Bind("$id", matchId).ExecuteNonQuery() == 1;
    });

    public MatchRecord? GetMatch(Guid matchId) => _db.Run(c =>
    {
        using var cmd = c.Command($"SELECT {MatchColumns} FROM matches m WHERE m.id = $id");
        using var r = cmd.Bind("$id", matchId).ExecuteReader();
        return r.Read() ? ReadMatch(r) : null;
    });

    /// <summary>Matches without an end time, newest first.</summary>
    public IReadOnlyList<MatchRecord> GetOpenMatches() => _db.Run(c =>
    {
        using var cmd = c.Command($"SELECT {MatchColumns} FROM matches m WHERE m.ended_at IS NULL ORDER BY m.started_at DESC");
        using var r = cmd.ExecuteReader();
        var list = new List<MatchRecord>();
        while (r.Read())
        {
            list.Add(ReadMatch(r));
        }

        return (IReadOnlyList<MatchRecord>)list;
    });

    public IReadOnlyList<MatchRecord> GetMatches() => _db.Run(c =>
    {
        using var cmd = c.Command($"SELECT {MatchColumns} FROM matches m ORDER BY m.started_at");
        using var r = cmd.ExecuteReader();
        var list = new List<MatchRecord>();
        while (r.Read())
        {
            list.Add(ReadMatch(r));
        }

        return (IReadOnlyList<MatchRecord>)list;
    });

    /// <summary>All matches with derived counters, newest first.</summary>
    public IReadOnlyList<MatchSummary> GetMatchSummaries() => _db.Run(c =>
    {
        using var cmd = c.Command(SummarySelect + " GROUP BY m.id ORDER BY m.started_at DESC");
        using var r = cmd.ExecuteReader();
        var list = new List<MatchSummary>();
        while (r.Read())
        {
            list.Add(ReadSummary(r));
        }

        return (IReadOnlyList<MatchSummary>)list;
    });

    public MatchSummary? GetMatchSummary(Guid matchId) => _db.Run(c =>
    {
        using var cmd = c.Command(SummarySelect + " WHERE m.id = $id GROUP BY m.id");
        using var r = cmd.Bind("$id", matchId).ExecuteReader();
        return r.Read() ? ReadSummary(r) : null;
    });

    // ------------------------------------------------------------------ events

    /// <summary>
    /// Appends an event. Returns false (and stores nothing) when an event with the same id
    /// already exists or when the referenced add event was already undone.
    /// </summary>
    public bool InsertEvent(ChestEvent e) => _db.Run(c => InsertEvent(c, null, e));

    public int GetMatchTotal(Guid matchId) => _db.Run(c =>
    {
        using var cmd = c.Command("SELECT COALESCE(SUM(delta), 0) FROM chest_events WHERE match_id = $m");
        return (int)cmd.Bind("$m", matchId).ScalarLong();
    });

    public int GetSessionTotal(Guid sessionId) => _db.Run(c =>
    {
        using var cmd = c.Command("""
            SELECT COALESCE(SUM(e.delta), 0)
            FROM chest_events e JOIN matches m ON m.id = e.match_id
            WHERE m.session_id = $s
            """);
        return (int)cmd.Bind("$s", sessionId).ScalarLong();
    });

    /// <summary>All chests ever, including lifetime adjustments that belong to no match.</summary>
    public int GetLifetimeTotal() => _db.Run(c =>
    {
        using var cmd = c.Command("SELECT COALESCE(SUM(delta), 0) FROM chest_events");
        return (int)cmd.ScalarLong();
    });

    public AggregateStats GetAggregateStats() => _db.Run(c =>
    {
        using var cmd = c.Command("""
            SELECT COUNT(*), COALESCE(SUM(t), 0), COALESCE(MAX(t), 0)
            FROM (SELECT COALESCE((SELECT SUM(e.delta) FROM chest_events e WHERE e.match_id = m.id), 0) AS t
                  FROM matches m)
            """);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new AggregateStats(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
    });

    /// <summary>Most recent add event of the match that has not been undone yet.</summary>
    public ChestEvent? GetLastUndoableAdd(Guid matchId) => _db.Run(c =>
    {
        using var cmd = c.Command($"""
            SELECT {EventColumns} FROM chest_events e
            WHERE e.match_id = $m AND e.kind = 0
              AND NOT EXISTS (SELECT 1 FROM chest_events u WHERE u.undoes_event_id = e.event_id)
            ORDER BY e.seq DESC LIMIT 1
            """);
        using var r = cmd.Bind("$m", matchId).ExecuteReader();
        return r.Read() ? ReadEvent(r) : null;
    });

    public DateTimeOffset? GetLastEventTime(Guid matchId) => _db.Run(c =>
    {
        using var cmd = c.Command("SELECT MAX(ts) FROM chest_events WHERE match_id = $m");
        var value = cmd.Bind("$m", matchId).ExecuteScalar();
        return value is null or DBNull ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds((long)value);
    });

    public IReadOnlyList<ChestEvent> GetRecentEvents(int limit) => _db.Run(c =>
    {
        using var cmd = c.Command($"SELECT {EventColumns} FROM chest_events ORDER BY seq DESC LIMIT $n");
        using var r = cmd.Bind("$n", limit).ExecuteReader();
        return ReadEvents(r);
    });

    public IReadOnlyList<ChestEvent> GetEventsForMatch(Guid matchId) => _db.Run(c =>
    {
        using var cmd = c.Command($"SELECT {EventColumns} FROM chest_events WHERE match_id = $m ORDER BY seq");
        using var r = cmd.Bind("$m", matchId).ExecuteReader();
        return ReadEvents(r);
    });

    public IReadOnlyList<ChestEvent> GetAllEvents() => _db.Run(c =>
    {
        using var cmd = c.Command($"SELECT {EventColumns} FROM chest_events ORDER BY seq");
        using var r = cmd.ExecuteReader();
        return ReadEvents(r);
    });

    // ----------------------------------------------------------------- reviews

    public void InsertReview(DetectionReview review) => _db.Run(c => InsertReview(c, null, review, ignoreExisting: false));

    public bool ResolveReview(Guid reviewId, ReviewStatus status, DateTimeOffset at, Guid? eventId) => _db.Run(c =>
    {
        using var cmd = c.Command("""
            UPDATE detection_reviews SET status = $st, resolved_at = $at, event_id = $ev
            WHERE id = $id AND status = 0
            """);
        return cmd.Bind("$id", reviewId).Bind("$st", status).Bind("$at", at).Bind("$ev", eventId).ExecuteNonQuery() == 1;
    });

    public DetectionReview? GetReview(Guid reviewId) => GetReviews("WHERE id = $id", reviewId).FirstOrDefault();

    public IReadOnlyList<DetectionReview> GetPendingReviews() => GetReviews("WHERE status = 0", null);

    public IReadOnlyList<DetectionReview> GetAllReviews() => GetReviews("", null);

    // --------------------------------------------------------------- bulk ops

    /// <summary>Deletes every statistic. Caller should back up first.</summary>
    public void DeleteAllData() => _db.InTransaction((c, tx) =>
    {
        foreach (var table in new[] { "sync_outbox", "shared_match_links", "detection_reviews", "chest_events", "matches", "sessions" })
        {
            using var cmd = c.Command($"DELETE FROM {table}", tx);
            cmd.ExecuteNonQuery();
        }

        return 0;
    });

    /// <summary>Merges exported data. Rows whose ids already exist are skipped, so importing twice is harmless.</summary>
    public ImportResult Import(IEnumerable<SessionRecord> sessions, IEnumerable<MatchRecord> matches,
        IEnumerable<ChestEvent> events, IEnumerable<DetectionReview> reviews)
    {
        return _db.InTransaction((c, tx) =>
        {
            int s = 0, m = 0, e = 0, rv = 0, skipped = 0;
            foreach (var session in sessions)
            {
                if (InsertSession(c, tx, session, ignoreExisting: true)) s++;
            }

            foreach (var match in matches)
            {
                if (InsertMatch(c, tx, match, ignoreExisting: true)) m++;
            }

            var knownMatches = new HashSet<Guid>();
            using (var ids = c.Command("SELECT id FROM matches", tx))
            using (var r = ids.ExecuteReader())
            {
                while (r.Read()) knownMatches.Add(r.GetGuidValue(0));
            }

            foreach (var ev in events.OrderBy(x => x.Sequence))
            {
                if (ev.MatchId is { } mid && !knownMatches.Contains(mid))
                {
                    skipped++;
                    continue;
                }

                if (InsertEvent(c, tx, ev)) e++;
            }

            foreach (var review in reviews)
            {
                if (review.MatchId is { } mid && !knownMatches.Contains(mid))
                {
                    skipped++;
                    continue;
                }

                if (InsertReview(c, tx, review, ignoreExisting: true)) rv++;
            }

            return new ImportResult(s, m, e, rv, skipped);
        });
    }

    // ---------------------------------------------------------------- helpers

    private static bool InsertSession(SqliteConnection c, SqliteTransaction? tx, SessionRecord s, bool ignoreExisting)
    {
        using var cmd = c.Command(
            $"INSERT {(ignoreExisting ? "OR IGNORE " : "")}INTO sessions (id, started_at, ended_at) VALUES ($id, $s, $e)", tx);
        return cmd.Bind("$id", s.Id).Bind("$s", s.StartedAt).Bind("$e", s.EndedAt).ExecuteNonQuery() == 1;
    }

    private static bool InsertMatch(SqliteConnection c, SqliteTransaction? tx, MatchRecord m, bool ignoreExisting)
    {
        using var cmd = c.Command($"""
            INSERT {(ignoreExisting ? "OR IGNORE " : "")}INTO matches (id, session_id, started_at, ended_at, tracking_mode, recovered, note)
            VALUES ($id, $s, $st, $en, $mode, $rec, $note)
            """, tx);
        return cmd.Bind("$id", m.Id).Bind("$s", m.SessionId).Bind("$st", m.StartedAt).Bind("$en", m.EndedAt)
            .Bind("$mode", m.Mode).Bind("$rec", m.Recovered).Bind("$note", m.Note).ExecuteNonQuery() == 1;
    }

    private static bool InsertEvent(SqliteConnection c, SqliteTransaction? tx, ChestEvent e)
    {
        using var cmd = c.Command("""
            INSERT OR IGNORE INTO chest_events (event_id, match_id, ts, kind, source, delta, confidence, undoes_event_id, note)
            VALUES ($id, $m, $ts, $k, $src, $d, $conf, $undo, $note)
            """, tx);
        return cmd.Bind("$id", e.EventId).Bind("$m", e.MatchId).Bind("$ts", e.Timestamp).Bind("$k", e.Kind)
            .Bind("$src", e.Source).Bind("$d", e.Delta).Bind("$conf", e.Confidence).Bind("$undo", e.UndoesEventId)
            .Bind("$note", e.Note).ExecuteNonQuery() == 1;
    }

    private static bool InsertReview(SqliteConnection c, SqliteTransaction? tx, DetectionReview r, bool ignoreExisting)
    {
        using var cmd = c.Command($"""
            INSERT {(ignoreExisting ? "OR IGNORE " : "")}INTO detection_reviews (id, match_id, ts, confidence, status, resolved_at, event_id)
            VALUES ($id, $m, $ts, $c, $st, $at, $ev)
            """, tx);
        return cmd.Bind("$id", r.Id).Bind("$m", r.MatchId).Bind("$ts", r.Timestamp).Bind("$c", r.Confidence)
            .Bind("$st", r.Status).Bind("$at", r.ResolvedAt).Bind("$ev", r.EventId).ExecuteNonQuery() == 1;
    }

    private IReadOnlyList<DetectionReview> GetReviews(string where, Guid? id) => _db.Run(c =>
    {
        using var cmd = c.Command(
            $"SELECT id, match_id, ts, confidence, status, resolved_at, event_id FROM detection_reviews {where} ORDER BY ts");
        if (id is not null)
        {
            cmd.Bind("$id", id.Value);
        }

        using var r = cmd.ExecuteReader();
        var list = new List<DetectionReview>();
        while (r.Read())
        {
            list.Add(new DetectionReview
            {
                Id = r.GetGuidValue(0),
                MatchId = r.GetNullableGuid(1),
                Timestamp = r.GetTime(2),
                Confidence = r.GetDouble(3),
                Status = (ReviewStatus)r.GetInt32(4),
                ResolvedAt = r.GetNullableTime(5),
                EventId = r.GetNullableGuid(6),
            });
        }

        return (IReadOnlyList<DetectionReview>)list;
    });

    private static MatchRecord ReadMatch(SqliteDataReader r) => new()
    {
        Id = r.GetGuidValue(0),
        SessionId = r.GetGuidValue(1),
        StartedAt = r.GetTime(2),
        EndedAt = r.GetNullableTime(3),
        Mode = (TrackingMode)r.GetInt32(4),
        Recovered = r.GetInt32(5) != 0,
        Note = r.GetNullableString(6),
    };

    private static MatchSummary ReadSummary(SqliteDataReader r) => new()
    {
        Match = ReadMatch(r),
        Chests = r.GetInt32(7),
        ManualAdds = r.GetInt32(8),
        AutomaticAdds = r.GetInt32(9),
        Undos = r.GetInt32(10),
        Corrections = r.GetInt32(11),
    };

    private static ChestEvent ReadEvent(SqliteDataReader r) => new()
    {
        Sequence = r.GetInt64(0),
        EventId = r.GetGuidValue(1),
        MatchId = r.GetNullableGuid(2),
        Timestamp = r.GetTime(3),
        Kind = (ChestEventKind)r.GetInt32(4),
        Source = (EventSource)r.GetInt32(5),
        Delta = r.GetInt32(6),
        Confidence = r.GetNullableDouble(7),
        UndoesEventId = r.GetNullableGuid(8),
        Note = r.GetNullableString(9),
    };

    private static IReadOnlyList<ChestEvent> ReadEvents(SqliteDataReader r)
    {
        var list = new List<ChestEvent>();
        while (r.Read())
        {
            list.Add(ReadEvent(r));
        }

        return list;
    }
}

public sealed record ImportResult(int Sessions, int Matches, int Events, int Reviews, int Skipped);
