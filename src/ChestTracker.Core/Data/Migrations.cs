namespace ChestTracker.Core.Data;

public sealed record Migration(int Version, string Description, string Sql);

/// <summary>
/// Ordered schema migrations tracked through <c>PRAGMA user_version</c>.
/// Append new migrations to the end; never edit a released one.
/// </summary>
public static class Migrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        new Migration(1, "Initial schema: sessions, matches, event log", """
            CREATE TABLE sessions (
                id          TEXT PRIMARY KEY NOT NULL,
                started_at  INTEGER NOT NULL,
                ended_at    INTEGER NULL
            );

            CREATE TABLE matches (
                id            TEXT PRIMARY KEY NOT NULL,
                session_id    TEXT NOT NULL,
                started_at    INTEGER NOT NULL,
                ended_at      INTEGER NULL,
                tracking_mode INTEGER NOT NULL,
                recovered     INTEGER NOT NULL DEFAULT 0,
                note          TEXT NULL
            );
            CREATE INDEX ix_matches_session ON matches(session_id);
            CREATE INDEX ix_matches_started ON matches(started_at);

            CREATE TABLE chest_events (
                seq             INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id        TEXT NOT NULL UNIQUE,
                match_id        TEXT NULL REFERENCES matches(id) ON DELETE CASCADE,
                ts              INTEGER NOT NULL,
                kind            INTEGER NOT NULL,
                source          INTEGER NOT NULL,
                delta           INTEGER NOT NULL,
                confidence      REAL NULL,
                undoes_event_id TEXT NULL,
                note            TEXT NULL
            );
            CREATE INDEX ix_events_match ON chest_events(match_id, seq);
            -- An add event can be compensated at most once.
            CREATE UNIQUE INDEX ux_events_undoes ON chest_events(undoes_event_id) WHERE undoes_event_id IS NOT NULL;
            """),

        new Migration(2, "Detection review queue and metadata", """
            CREATE TABLE detection_reviews (
                id          TEXT PRIMARY KEY NOT NULL,
                match_id    TEXT NULL REFERENCES matches(id) ON DELETE CASCADE,
                ts          INTEGER NOT NULL,
                confidence  REAL NOT NULL,
                status      INTEGER NOT NULL DEFAULT 0,
                resolved_at INTEGER NULL,
                event_id    TEXT NULL
            );
            CREATE INDEX ix_reviews_status ON detection_reviews(status);

            CREATE TABLE app_meta (
                key   TEXT PRIMARY KEY NOT NULL,
                value TEXT NULL
            );
            """),

        new Migration(3, "Multiplayer outbox and shared match links", """
            CREATE TABLE sync_outbox (
                event_id        TEXT PRIMARY KEY NOT NULL,
                seq             INTEGER NOT NULL,
                party_id        TEXT NOT NULL,
                local_match_id  TEXT NOT NULL,
                shared_match_id TEXT NULL,
                kind            INTEGER NOT NULL,
                source          INTEGER NOT NULL,
                delta           INTEGER NOT NULL,
                undoes_event_id TEXT NULL,
                ts              INTEGER NOT NULL,
                confidence      REAL NULL,
                attempts        INTEGER NOT NULL DEFAULT 0,
                last_error      TEXT NULL
            );
            CREATE INDEX ix_outbox_seq ON sync_outbox(seq);

            CREATE TABLE shared_match_links (
                local_match_id  TEXT PRIMARY KEY NOT NULL,
                shared_match_id TEXT NOT NULL
            );
            """),

        // When this player stopped counting in a linked shared match, and whether the server was told. Lets a stop
        // made while offline reach the server later. Existing links are history: nothing to send for them.
        new Migration(4, "Shared match participation end", """
            ALTER TABLE shared_match_links ADD COLUMN finished_at INTEGER NULL;
            ALTER TABLE shared_match_links ADD COLUMN finish_sent INTEGER NOT NULL DEFAULT 0;
            UPDATE shared_match_links SET finish_sent = 1;
            """),
    ];

    public static int LatestVersion => All[^1].Version;

    /// <summary>Applies all pending migrations, each in its own transaction.</summary>
    /// <returns>Versions that were applied.</returns>
    public static IReadOnlyList<int> Apply(SqliteDatabase db)
    {
        var current = db.SchemaVersion;
        if (current > LatestVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {current} is newer than this application supports ({LatestVersion}).");
        }

        var applied = new List<int>();
        foreach (var migration in All.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            db.InTransaction((c, tx) =>
            {
                using (var cmd = c.Command(migration.Sql, tx))
                {
                    cmd.ExecuteNonQuery();
                }

                using (var version = c.Command($"PRAGMA user_version = {migration.Version};", tx))
                {
                    version.ExecuteNonQuery();
                }

                return 0;
            });
            applied.Add(migration.Version);
        }

        return applied;
    }
}
