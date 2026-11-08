using System.Collections.Concurrent;

namespace ChestTracker.Server.Services;

/// <summary>
/// In-memory map of online players to their SignalR connections (single server instance;
/// a scale-out deployment would need a shared backplane such as Redis).
/// </summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _connections = new();

    /// <returns>True when this is the player's first connection (became online).</returns>
    public bool Add(Guid playerId, string connectionId)
    {
        var set = _connections.GetOrAdd(playerId, _ => []);
        lock (set)
        {
            set.Add(connectionId);
            return set.Count == 1;
        }
    }

    /// <returns>True when the player has no connections left (went offline).</returns>
    public bool Remove(Guid playerId, string connectionId)
    {
        if (!_connections.TryGetValue(playerId, out var set))
        {
            return false;
        }

        lock (set)
        {
            set.Remove(connectionId);
            if (set.Count > 0)
            {
                return false;
            }

            _connections.TryRemove(new KeyValuePair<Guid, HashSet<string>>(playerId, set));
            return true;
        }
    }

    public int OnlinePlayerCount => _connections.Count(kv => kv.Value.Count > 0);

    public int ConnectionCount => _connections.Values.Sum(s => { lock (s) return s.Count; });

    public IReadOnlyCollection<Guid> OnlinePlayers => _connections.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();

    public bool IsOnline(Guid playerId) =>
        _connections.TryGetValue(playerId, out var set) && set.Count > 0;

    public IReadOnlyList<string> Connections(Guid playerId)
    {
        if (!_connections.TryGetValue(playerId, out var set))
        {
            return [];
        }

        lock (set)
        {
            return set.ToList();
        }
    }
}

public sealed class PartyException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
