using System.Globalization;
using ChestTracker.Core.Models;
using Microsoft.Data.Sqlite;

namespace ChestTracker.Core.Data;

public sealed record OutboxItem(
    Guid EventId,
    long Sequence,
    Guid PartyId,
    Guid LocalMatchId,
    Guid? SharedMatchId,
    ChestEventKind Kind,
    EventSource Source,
    int Delta,
    Guid? UndoesEventId,
    DateTimeOffset Timestamp,
    double? Confidence,
    int Attempts);

/// <summary>
/// Durable queue of local chest events waiting to be sent to the party server.
/// The local event log stays the source of truth: <see cref="EnqueueNew"/> copies every event whose
/// sequence number is above a stored cursor, so nothing is lost if the app closes before sending,
/// and events recorded offline are delivered after reconnecting. Delivery is idempotent on the
/// server (event ids), so re-sending after a crash is safe.
/// </summary>
public sealed class SyncOutbox(SqliteDatabase db)
{
    private const string PartyKey = "sync.party";
    private const string CursorKey = "sync.cursor";

    public Guid? PartyId => Get(PartyKey) is { } v ? Guid.Parse(v) : null;

    public long Cursor => Get(CursorKey) is { } v ? long.Parse(v, CultureInfo.InvariantCulture) : 0;

    public int Count => db.Run(c => (int)c.Command("SELECT COUNT(*) FROM sync_outbox").ScalarLong());

    /// <summary>
    /// Starts syncing for a party. Only events recorded from now on are shared. Unsent events of a previous party stay
    /// queued with that party's id (the server accepts them only if they were recorded during the membership).
    /// </summary>
    public void BeginParty(Guid partyId)
    {
        if (PartyId == partyId)
        {
            return;
        }

        db.InTransaction((c, tx) =>
        {
            EnqueueCore(c, tx); // events of the party being left are queued under that party first
            var max = c.Command("SELECT COALESCE(MAX(seq), 0) FROM chest_events", tx).ScalarLong();
            Set(c, tx, PartyKey, partyId.ToString("D"));
            Set(c, tx, CursorKey, max.ToString(CultureInfo.InvariantCulture));
            return 0;
        });
    }

    /// <summary>
    /// Left or removed from the party: queues what was recorded up to now under that party and stops queuing new events.
    /// The queue is kept, so events recorded during the membership are still delivered (the server checks their time).
    /// </summary>
    public void LeaveParty() => db.InTransaction((c, tx) =>
    {
        EnqueueCore(c, tx);
        using var meta = c.Command("DELETE FROM app_meta WHERE key IN ($a, $b)", tx);
        meta.Bind("$a", PartyKey).Bind("$b", CursorKey).ExecuteNonQuery();
        return 0;
    });

    /// <summary>
    /// Forgets everything queued (sign-out, switching account or server): one identity's events are never delivered
    /// as another's.
    /// </summary>
    public void EndParty() => db.InTransaction((c, tx) =>
    {
        using (var clear = c.Command("DELETE FROM sync_outbox", tx)) clear.ExecuteNonQuery();
        using (var meta = c.Command("DELETE FROM app_meta WHERE key IN ($a, $b)", tx))
        {
            meta.Bind("$a", PartyKey).Bind("$b", CursorKey).ExecuteNonQuery();
        }

        return 0;
    });

    /// <summary>Copies new local events (match events only) into the outbox. Returns how many were queued.</summary>
    public int EnqueueNew() => db.InTransaction(EnqueueCore);

    private static int EnqueueCore(SqliteConnection c, SqliteTransaction tx)
    {
        var party = GetValue(c, tx, PartyKey);
        if (party is null)
        {
            return 0;
        }

        var cursor = long.Parse(GetValue(c, tx, CursorKey) ?? "0", CultureInfo.InvariantCulture);
        using var insert = c.Command("""
            INSERT OR IGNORE INTO sync_outbox (event_id, seq, party_id, local_match_id, shared_match_id, kind, source, delta,
                                               undoes_event_id, ts, confidence)
            SELECT e.event_id, e.seq, $party, e.match_id, l.shared_match_id, e.kind, e.source, e.delta,
                   e.undoes_event_id, e.ts, e.confidence
            FROM chest_events e
            LEFT JOIN shared_match_links l ON l.local_match_id = e.match_id
            WHERE e.seq > $cursor AND e.match_id IS NOT NULL
            """, tx);
        var queued = insert.Bind("$party", party).Bind("$cursor", cursor).ExecuteNonQuery();

        var max = c.Command("SELECT COALESCE(MAX(seq), 0) FROM chest_events", tx).ScalarLong();
        if (max > cursor)
        {
            Set(c, tx, CursorKey, max.ToString(CultureInfo.InvariantCulture));
        }

        return queued;
    }

    /// <summary>Oldest pending events first (sequence order keeps undo targets before undos).</summary>
    public IReadOnlyList<OutboxItem> Peek(int max) => db.Run(c =>
    {
        using var cmd = c.Command("""
            SELECT event_id, seq, party_id, local_match_id, shared_match_id, kind, source, delta, undoes_event_id, ts, confidence, attempts
            FROM sync_outbox ORDER BY seq LIMIT $n
            """);
        using var r = cmd.Bind("$n", max).ExecuteReader();
        var list = new List<OutboxItem>();
        while (r.Read())
        {
            list.Add(new OutboxItem(r.GetGuidValue(0), r.GetInt64(1), r.GetGuidValue(2), r.GetGuidValue(3), r.GetNullableGuid(4),
                (ChestEventKind)r.GetInt32(5), (EventSource)r.GetInt32(6), r.GetInt32(7), r.GetNullableGuid(8), r.GetTime(9),
                r.GetNullableDouble(10), r.GetInt32(11)));
        }

        return (IReadOnlyList<OutboxItem>)list;
    });

    public void Remove(IEnumerable<Guid> eventIds) => db.InTransaction((c, tx) =>
    {
        foreach (var id in eventIds)
        {
            using var cmd = c.Command("DELETE FROM sync_outbox WHERE event_id = $id", tx);
            cmd.Bind("$id", id).ExecuteNonQuery();
        }

        return 0;
    });

    public void MarkFailed(IEnumerable<Guid> eventIds, string error) => db.InTransaction((c, tx) =>
    {
        foreach (var id in eventIds)
        {
            using var cmd = c.Command("UPDATE sync_outbox SET attempts = attempts + 1, last_error = $e WHERE event_id = $id", tx);
            cmd.Bind("$id", id).Bind("$e", error).ExecuteNonQuery();
        }

        return 0;
    });

    public void LinkMatch(Guid localMatchId, Guid sharedMatchId) => db.Run(c =>
    {
        using var cmd = c.Command("INSERT OR REPLACE INTO shared_match_links (local_match_id, shared_match_id) VALUES ($l, $s)");
        cmd.Bind("$l", localMatchId).Bind("$s", sharedMatchId).ExecuteNonQuery();
        // Events of this match that are still queued get the shared id too.
        using var update = c.Command("UPDATE sync_outbox SET shared_match_id = $s WHERE local_match_id = $l");
        update.Bind("$l", localMatchId).Bind("$s", sharedMatchId).ExecuteNonQuery();
    });

    /// <summary>
    /// The player stopped counting in a local match. If it is linked to a shared match, the end of this player's
    /// participation is recorded so it can be sent to the server (now or after a reconnect).
    /// </summary>
    public void MarkFinished(Guid localMatchId, DateTimeOffset at) => db.Run(c =>
    {
        using var cmd = c.Command("UPDATE shared_match_links SET finished_at = $t WHERE local_match_id = $l AND finished_at IS NULL");
        return cmd.Bind("$t", at).Bind("$l", localMatchId).ExecuteNonQuery();
    });

    /// <summary>Shared matches whose end of participation has not reached the server yet.</summary>
    public IReadOnlyList<(Guid SharedMatchId, DateTimeOffset FinishedAt)> PendingFinishes() => db.Run(c =>
    {
        // Linked local matches that ended while sync was not attached (app closed, crash recovery) finished too.
        using (var ended = c.Command("""
            UPDATE shared_match_links SET finished_at = (SELECT m.ended_at FROM matches m WHERE m.id = shared_match_links.local_match_id)
            WHERE finished_at IS NULL AND finish_sent = 0
              AND EXISTS (SELECT 1 FROM matches m WHERE m.id = shared_match_links.local_match_id AND m.ended_at IS NOT NULL)
            """))
        {
            ended.ExecuteNonQuery();
        }

        using var cmd = c.Command("SELECT shared_match_id, MAX(finished_at) FROM shared_match_links WHERE finished_at IS NOT NULL AND finish_sent = 0 GROUP BY shared_match_id");
        using var r = cmd.ExecuteReader();
        var list = new List<(Guid, DateTimeOffset)>();
        while (r.Read())
        {
            list.Add((Guid.Parse(r.GetString(0)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1))));
        }

        return (IReadOnlyList<(Guid, DateTimeOffset)>)list;
    });

    /// <summary>The server knows this player is done with the shared match (or the match is over anyway).</summary>
    public void FinishSent(Guid sharedMatchId, DateTimeOffset at) => db.Run(c =>
    {
        using var cmd = c.Command("UPDATE shared_match_links SET finished_at = COALESCE(finished_at, $t), finish_sent = 1 WHERE shared_match_id = $s");
        return cmd.Bind("$t", at).Bind("$s", sharedMatchId).ExecuteNonQuery();
    });

    public Guid? GetSharedMatch(Guid localMatchId) => db.Run(c =>
    {
        using var cmd = c.Command("SELECT shared_match_id FROM shared_match_links WHERE local_match_id = $l");
        return cmd.Bind("$l", localMatchId).ExecuteScalar() is string s ? Guid.Parse(s) : (Guid?)null;
    });

    private string? Get(string key) => db.Run(c => GetValue(c, null, key));

    private static string? GetValue(SqliteConnection c, SqliteTransaction? tx, string key)
    {
        using var cmd = c.Command("SELECT value FROM app_meta WHERE key = $k", tx);
        return cmd.Bind("$k", key).ExecuteScalar() as string;
    }

    private static void Set(SqliteConnection c, SqliteTransaction? tx, string key, string value)
    {
        using var cmd = c.Command("INSERT OR REPLACE INTO app_meta (key, value) VALUES ($k, $v)", tx);
        cmd.Bind("$k", key).Bind("$v", value).ExecuteNonQuery();
    }
}
