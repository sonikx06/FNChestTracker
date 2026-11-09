namespace ChestTracker.Protocol;

/// <summary>Wire contracts shared by the desktop client and the multiplayer server.</summary>
public static class Routes
{
    public const string PartyHub = "/hubs/party";
}

/// <summary>Server → client SignalR method names.</summary>
public static class ClientMethods
{
    public const string PartyUpdated = "PartyUpdated";
    public const string Activity = "Activity";
    public const string SharedMatchStarted = "SharedMatchStarted";
    public const string SharedMatchEnded = "SharedMatchEnded";
    public const string RemovedFromParty = "RemovedFromParty";
}

/// <summary>Client → server SignalR method names.</summary>
public static class HubMethods
{
    public const string SubmitEvents = "SubmitEvents";
    public const string StartSharedMatch = "StartSharedMatch";
    public const string RespondSharedMatch = "RespondSharedMatch";
    public const string EndSharedMatch = "EndSharedMatch";

    /// <summary>
    /// A participant stopped counting (e.g. died and pressed the match hotkey). Ends only that player's part; the
    /// shared match ends when no accepted participant is still playing (or when the owner ends it).
    /// </summary>
    public const string FinishSharedMatch = "FinishSharedMatch";
    public const string GetPartyState = "GetPartyState";
    public const string Ping = "Ping";
}

/// <summary>Same numeric values as the client's local event kinds.</summary>
public enum EventKind
{
    Add = 0,
    Undo = 1,
    Correction = 2,
}

public enum EventSourceType
{
    Manual = 0,
    Automatic = 1,
}

public enum AckStatus
{
    /// <summary>Stored now.</summary>
    Accepted,

    /// <summary>Already stored earlier (idempotent retry).</summary>
    Duplicate,

    /// <summary>Permanently refused; the client must not retry.</summary>
    Rejected,
}

public enum PartyRole
{
    Member = 0,
    Owner = 1,
}

public enum SharedMatchStatus
{
    Active = 1,
    Ended = 2,
}

public enum ParticipantResponse
{
    Pending = 0,
    Accepted = 1,
    Declined = 2,
}

public enum LeaderboardPeriod
{
    Day,
    Week,
    Lifetime,
}

/// <summary>One chest event as reported by a player's client.</summary>
public sealed record ChestEventDto(
    Guid EventId,
    Guid LocalMatchId,
    Guid? SharedMatchId,
    EventKind Kind,
    EventSourceType Source,
    int Delta,
    Guid? UndoesEventId,
    DateTimeOffset ClientTimestamp,
    double? Confidence,
    // Party the event was recorded in (null from older clients = the sender's current party). Lets events
    // recorded during a membership be delivered after leaving; the server checks the membership period.
    Guid? PartyId = null);

public sealed record EventAck(Guid EventId, AckStatus Status, bool Verified, string? Reason);

/// <summary>
/// <see cref="Reported"/> = sum of everything the player's client reported (adds, undos and manual corrections).
/// <see cref="Verified"/> = unique add/undo events that passed server validation (membership, timestamps,
/// plausible rate, valid undo target). Manual corrections never count as verified.
/// </summary>
public sealed record CountPair(int Verified, int Reported)
{
    public static CountPair Zero { get; } = new(0, 0);
}

public sealed record MemberDto(
    Guid PlayerId,
    string DisplayName,
    PartyRole Role,
    bool Online,
    bool ShareCounts,
    bool ShareActivity,
    CountPair? Match,
    CountPair? Lifetime,
    ParticipantResponse? MatchResponse,
    bool MatchFinished = false); // took part, then stopped counting (e.g. died); optional field, older clients ignore it

public sealed record SharedMatchDto(Guid Id, SharedMatchStatus Status, Guid StartedBy, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

public sealed record PartyStateDto(
    Guid PartyId,
    string Name,
    Guid OwnerId,
    int MaxPlayers,
    string? InviteCode,
    DateTimeOffset? InviteExpiresAt,
    IReadOnlyList<MemberDto> Members,
    SharedMatchDto? ActiveMatch,
    CountPair TeamMatch,
    CountPair TeamLifetime,
    DateTimeOffset ServerTime);

public sealed record ActivityDto(
    Guid PlayerId,
    string DisplayName,
    EventKind Kind,
    EventSourceType Source,
    int Delta,
    DateTimeOffset ClientTimestamp,
    Guid? SharedMatchId);

public sealed record LeaderboardEntryDto(int Rank, Guid PlayerId, string DisplayName, int Verified, int Reported);

public sealed record ParticipantStatsDto(Guid PlayerId, string DisplayName, CountPair? Counts);

public sealed record SharedMatchHistoryDto(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<ParticipantStatsDto> Participants,
    CountPair Team);

// ----------------------------------------------------------------- REST bodies

public sealed record RegisterRequest(string DisplayName);

public sealed record RegisterResponse(Guid PlayerId, string Secret);

public sealed record TokenRequest(Guid PlayerId, string Secret);

public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt);

public sealed record ProfileDto(Guid PlayerId, string DisplayName, Guid? PartyId);

public sealed record RenameRequest(string DisplayName);

public sealed record CreatePartyRequest(string Name, int MaxPlayers);

public sealed record JoinPartyRequest(string Code);

public sealed record SharingRequest(bool ShareCounts, bool ShareActivity);

public sealed record ErrorDto(string Code, string Message);

/// <summary>Stable error codes returned by the server.</summary>
public static class ErrorCodes
{
    public const string InvalidCode = "invalid_code";
    public const string PartyFull = "party_full";
    public const string NotMember = "not_member";
    public const string NotOwner = "not_owner";
    public const string NotFound = "not_found";
    public const string InvalidRequest = "invalid_request";
    public const string MatchActive = "match_active";
    public const string Unauthorized = "unauthorized";
    public const string ClientOutdated = "client_outdated";
    public const string ServerOutdated = "server_outdated";
    public const string UsernameTaken = "username_taken";
    public const string InvalidCredentials = "invalid_credentials";
    public const string WeakPassword = "weak_password";
    public const string InvalidUsername = "invalid_username";
    public const string AlreadyAccount = "already_account";
    public const string RateLimited = "rate_limited";
    public const string GuestsDisabled = "guests_disabled";
}
