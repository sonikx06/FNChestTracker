namespace ChestTracker.Server;

public sealed class ServerOptions
{
    public const string Section = "ChestTracker";

    /// <summary>HMAC key for access tokens (≥ 32 bytes). Use this or <see cref="SigningKeyFile"/>.</summary>
    public string? SigningKey { get; set; }

    /// <summary>
    /// File holding the signing key. Created with a random key on first start when missing,
    /// so release builds ship without any secret.
    /// </summary>
    public string? SigningKeyFile { get; set; }

    public int TokenLifetimeMinutes { get; set; } = 60;

    public bool AllowGuests { get; set; } = true;
    public bool AllowAccounts { get; set; } = true;

    /// <summary>Administrator API key (X-Admin-Key header). Admin endpoints do not exist while this is empty.</summary>
    public string? AdminApiKey { get; set; }

    /// <summary>Clients below this protocol version get HTTP 426.</summary>
    public int MinClientProtocol { get; set; } = Protocol.ProtocolInfo.Version;

    /// <summary>Upper bound for a party's size; owners pick 1..MaxPartySize.</summary>
    public int MaxPartySize { get; set; } = 4;

    public double InviteTtlHours { get; set; } = 24;

    /// <summary>Two adds from the same player closer than this are stored but not verified.</summary>
    public double MinSecondsBetweenAdds { get; set; } = 1.0;

    /// <summary>Events dated further in the future than this are rejected (clock skew allowance).</summary>
    public double MaxFutureSkewMinutes { get; set; } = 5;

    /// <summary>Offline queue limit: older events are rejected.</summary>
    public double MaxEventAgeDays { get; set; } = 14;

    /// <summary>
    /// Clock-skew allowance at membership boundaries: an event counts for a party only if its timestamp lies within
    /// a membership period of the sender, widened by this many seconds.
    /// </summary>
    public double MembershipGraceSeconds { get; set; } = 10;

    /// <summary>Events this close outside a shared match window are still attributed to it.</summary>
    public double SharedMatchGraceMinutes { get; set; } = 2;

    /// <summary>Only events newer than this are broadcast as live teammate activity.</summary>
    public double LiveActivityWindowSeconds { get; set; } = 60;

    public int MaxEventsPerBatch { get; set; } = 200;

    /// <summary>Join attempts per player per minute (protects invitation codes from guessing).</summary>
    public int JoinAttemptsPerMinute { get; set; } = 10;

    /// <summary>Register/token/login requests per IP per minute.</summary>
    public int AuthRequestsPerMinute { get; set; } = 30;

    /// <summary>All other API requests per player (or IP) per minute.</summary>
    public int RequestsPerMinute { get; set; } = 300;

    /// <summary>Hub method calls per player per second (token bucket burst).</summary>
    public int HubCallsPerSecond { get; set; } = 20;

    /// <summary>Reverse proxies whose X-Forwarded-For/Proto headers are trusted (loopback is always trusted).</summary>
    public string[] TrustedProxies { get; set; } = [];

    public string BackupDirectory { get; set; } = "backups";

    /// <summary>0 disables automatic backups.</summary>
    public double BackupIntervalHours { get; set; } = 6;

    public int BackupKeep { get; set; } = 28;

    public string LogDirectory { get; set; } = "logs";

    public int LogRetentionDays { get; set; } = 14;
}
