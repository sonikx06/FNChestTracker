using System.Reflection;
using ChestTracker.Protocol;
using ChestTracker.Server.Data;
using Npgsql;

namespace ChestTracker.Server.Services;

public static class ServerInfo
{
    public static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public static string Version { get; } =
        typeof(ServerInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ServerInfo).Assembly.GetName().Version?.ToString() ?? "0";
}

/// <summary>Read-only operational views for administrators (online players, active rooms, totals).</summary>
public sealed class AdminService(NpgsqlDataSource db, PresenceTracker presence, BackupService backups, TimeProvider clock)
{
    public async Task<AdminStatsDto> GetStatsAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        var players = await c.ScalarAsync<long>("SELECT COUNT(*) FROM players", null);
        var parties = await c.ScalarAsync<long>("SELECT COUNT(*) FROM parties", null);
        var activeMatches = await c.ScalarAsync<long>("SELECT COUNT(*) FROM shared_matches WHERE status = 1", null);
        var events = await c.ScalarAsync<long>("SELECT COUNT(*) FROM chest_events WHERE server_ts > @t", null,
            ("t", clock.GetUtcNow().AddDays(-1)));
        var online = presence.OnlinePlayers.ToArray();
        var activeParties = online.Length == 0 ? 0 : await c.ScalarAsync<long>(
            "SELECT COUNT(DISTINCT party_id) FROM party_members WHERE left_at IS NULL AND player_id = ANY(@ids)", null, ("ids", online));
        return new AdminStatsDto(ServerInfo.Version, clock.GetUtcNow() - ServerInfo.StartedAt, online.Length, presence.ConnectionCount,
            (int)activeParties, (int)activeMatches, players, parties, events, backups.LastBackupAt);
    }

    /// <summary>Parties with at least one online member ("active rooms").</summary>
    public async Task<IReadOnlyList<AdminPartyDto>> GetActivePartiesAsync()
    {
        var online = presence.OnlinePlayers.ToArray();
        if (online.Length == 0)
        {
            return [];
        }

        await using var c = await db.OpenConnectionAsync();
        return await c.ListAsync("""
            SELECT p.id, p.name, COUNT(m.player_id)::int, COUNT(m.player_id) FILTER (WHERE m.player_id = ANY(@ids))::int,
                   EXISTS (SELECT 1 FROM shared_matches s WHERE s.party_id = p.id AND s.status = 1), p.created_at
            FROM parties p JOIN party_members m ON m.party_id = p.id AND m.left_at IS NULL
            GROUP BY p.id
            HAVING COUNT(m.player_id) FILTER (WHERE m.player_id = ANY(@ids)) > 0
            ORDER BY 4 DESC, p.created_at
            """, null, r => new AdminPartyDto(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetBoolean(4), r.Ts(5)),
            ("ids", online));
    }
}
