namespace ChestTracker.Protocol;

/// <summary>
/// Wire compatibility. Bump <see cref="Version"/> on any breaking change to contracts or hub methods.
/// The client sends its version in <see cref="Header"/> (and as a query parameter for the hub);
/// the server answers 426 + <see cref="ErrorCodes.ClientOutdated"/> to clients below its minimum,
/// and the client refuses servers below <see cref="MinServerVersion"/>.
/// </summary>
public static class ProtocolInfo
{
    public const int Version = 2;
    public const int MinServerVersion = 2;
    public const string Header = "X-ChestTracker-Protocol";
    public const string QueryParameter = "protocol";
}

public sealed record ServerStatusDto(
    string ServerVersion,
    int ProtocolVersion,
    int MinClientProtocol,
    DateTimeOffset ServerTime,
    int OnlinePlayers,
    bool AccountsEnabled,
    bool GuestsEnabled);

public sealed record AccountRegisterRequest(string Username, string Password, string DisplayName);

public sealed record LoginRequest(string Username, string Password);

public sealed record ClaimAccountRequest(string Username, string Password);

/// <summary>Device credential returned by guest registration, account registration and login.</summary>
public sealed record CredentialResponse(Guid PlayerId, string Secret, string DisplayName, string? Username);

public sealed record AccountDto(Guid PlayerId, string DisplayName, string? Username, bool IsGuest, Guid? PartyId);

public sealed record PingResponse(DateTimeOffset ServerTime);

// ------------------------------------------------------------------ administration

public sealed record AdminStatsDto(
    string Version,
    TimeSpan Uptime,
    int OnlinePlayers,
    int Connections,
    int ActiveParties,
    int ActiveSharedMatches,
    long TotalPlayers,
    long TotalParties,
    long EventsLast24Hours,
    DateTimeOffset? LastBackupAt);

public sealed record AdminPartyDto(Guid PartyId, string Name, int Members, int OnlineMembers, bool SharedMatchActive, DateTimeOffset CreatedAt);

public sealed record BackupInfoDto(string FileName, long SizeBytes, DateTimeOffset CreatedAt);
