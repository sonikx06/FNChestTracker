using Npgsql;

namespace ChestTracker.Server.Data;

/// <summary>Ordered, transactional schema migrations recorded in <c>schema_migrations</c>.</summary>
public sealed class SchemaMigrator(NpgsqlDataSource dataSource, ILogger<SchemaMigrator> logger)
{
    public static readonly IReadOnlyList<(int Version, string Sql)> Migrations =
    [
        (1, """
            CREATE TABLE players (
                id            uuid PRIMARY KEY,
                display_name  text NOT NULL,
                secret_hash   bytea NOT NULL,
                created_at    timestamptz NOT NULL,
                last_seen_at  timestamptz NULL
            );

            CREATE TABLE parties (
                id                uuid PRIMARY KEY,
                name              text NOT NULL,
                owner_id          uuid NOT NULL REFERENCES players(id),
                max_players       int NOT NULL,
                invite_code       text NULL UNIQUE,
                invite_expires_at timestamptz NULL,
                created_at        timestamptz NOT NULL
            );

            CREATE TABLE party_members (
                party_id       uuid NOT NULL REFERENCES parties(id) ON DELETE CASCADE,
                player_id      uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
                role           smallint NOT NULL,
                joined_at      timestamptz NOT NULL,
                left_at        timestamptz NULL,
                share_counts   boolean NOT NULL DEFAULT true,
                share_activity boolean NOT NULL DEFAULT true,
                PRIMARY KEY (party_id, player_id)
            );
            -- A player is an active member of at most one party.
            CREATE UNIQUE INDEX ux_members_active_player ON party_members(player_id) WHERE left_at IS NULL;

            CREATE TABLE shared_matches (
                id          uuid PRIMARY KEY,
                party_id    uuid NOT NULL REFERENCES parties(id) ON DELETE CASCADE,
                started_by  uuid NOT NULL,
                status      smallint NOT NULL,
                started_at  timestamptz NOT NULL,
                ended_at    timestamptz NULL
            );
            -- At most one active shared match per party.
            CREATE UNIQUE INDEX ux_shared_active ON shared_matches(party_id) WHERE status = 1;
            CREATE INDEX ix_shared_party ON shared_matches(party_id, started_at DESC);

            CREATE TABLE shared_match_participants (
                match_id     uuid NOT NULL REFERENCES shared_matches(id) ON DELETE CASCADE,
                player_id    uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
                response     smallint NOT NULL,
                responded_at timestamptz NULL,
                PRIMARY KEY (match_id, player_id)
            );

            CREATE TABLE chest_events (
                event_id        uuid PRIMARY KEY,
                player_id       uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
                party_id        uuid NOT NULL REFERENCES parties(id) ON DELETE CASCADE,
                shared_match_id uuid NULL REFERENCES shared_matches(id) ON DELETE SET NULL,
                local_match_id  uuid NOT NULL,
                kind            smallint NOT NULL,
                source          smallint NOT NULL,
                delta           int NOT NULL,
                undoes_event_id uuid NULL,
                confidence      double precision NULL,
                client_ts       timestamptz NOT NULL,
                server_ts       timestamptz NOT NULL,
                verified        boolean NOT NULL,
                flag            text NULL
            );
            CREATE INDEX ix_events_party_time ON chest_events(party_id, client_ts);
            CREATE INDEX ix_events_player_time ON chest_events(player_id, client_ts);
            CREATE INDEX ix_events_shared ON chest_events(shared_match_id);
            CREATE UNIQUE INDEX ux_events_undoes ON chest_events(undoes_event_id) WHERE undoes_event_id IS NOT NULL;
            """),
        (2, """
            -- Optional username/password accounts; players without a username are guests.
            ALTER TABLE players ADD COLUMN username text NULL;
            ALTER TABLE players ADD COLUMN username_normalized text NULL;
            ALTER TABLE players ADD COLUMN password_hash text NULL;
            CREATE UNIQUE INDEX ux_players_username ON players(username_normalized) WHERE username_normalized IS NOT NULL;

            -- One credential per device, so an account can sign in from several computers.
            CREATE TABLE device_credentials (
                id           uuid PRIMARY KEY,
                player_id    uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
                secret_hash  bytea NOT NULL UNIQUE,
                created_at   timestamptz NOT NULL,
                last_used_at timestamptz NULL
            );
            CREATE INDEX ix_devices_player ON device_credentials(player_id);
            INSERT INTO device_credentials (id, player_id, secret_hash, created_at)
                SELECT gen_random_uuid(), id, secret_hash, created_at FROM players;
            ALTER TABLE players ALTER COLUMN secret_hash DROP NOT NULL;
            """),
    ];

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var create = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_migrations (version int PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", conn))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        // Serialize concurrent server starts.
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(424242)", conn))
        {
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var applied = new HashSet<int>();
            await using (var read = new NpgsqlCommand("SELECT version FROM schema_migrations", conn))
            await using (var reader = await read.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct)) applied.Add(reader.GetInt32(0));
            }

            foreach (var (version, sql) in Migrations.Where(m => !applied.Contains(m.Version)).OrderBy(m => m.Version))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(sql, conn, tx))
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await using (var mark = new NpgsqlCommand("INSERT INTO schema_migrations(version) VALUES (@v)", conn, tx))
                {
                    mark.Parameters.AddWithValue("v", version);
                    await mark.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                logger.LogInformation("Applied schema migration {Version}", version);
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(424242)", conn);
            await unlock.ExecuteNonQueryAsync(ct);
        }
    }
}
