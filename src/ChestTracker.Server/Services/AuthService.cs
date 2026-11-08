using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ChestTracker.Protocol;
using ChestTracker.Server.Data;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace ChestTracker.Server.Services;

/// <summary>
/// Two kinds of players:
/// <list type="bullet">
/// <item>Guests: the server issues a random device secret; only SHA-256(secret) is stored.</item>
/// <item>Accounts: username + password (PBKDF2-SHA256). Logging in from any computer issues a new device secret,
/// so the password itself is never stored on clients.</item>
/// </list>
/// Device secrets are exchanged for short-lived signed access tokens. No Fortnite/Epic credentials are involved.
/// </summary>
public sealed class AuthService(NpgsqlDataSource db, IOptions<ServerOptions> options, SigningKeyProvider keys, TimeProvider clock)
{
    public const string Issuer = "chesttracker";
    public const string Audience = "chesttracker-clients";
    private const int Pbkdf2Iterations = 210_000;

    public async Task<CredentialResponse> RegisterGuestAsync(string displayName)
    {
        if (!options.Value.AllowGuests)
        {
            throw new PartyException(403, ErrorCodes.GuestsDisabled, "This server does not allow guest players.");
        }

        var name = PartyService.CleanName(displayName, ErrorCodes.InvalidRequest);
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var id = Guid.NewGuid();
        await c.ExecAsync("INSERT INTO players (id, display_name, created_at) VALUES (@id, @n, @t)", tx,
            ("id", id), ("n", name), ("t", clock.GetUtcNow()));
        var secret = await IssueDeviceAsync(c, tx, id);
        await tx.CommitAsync();
        return new CredentialResponse(id, secret, name, null);
    }

    public async Task<CredentialResponse> RegisterAccountAsync(AccountRegisterRequest r)
    {
        RequireAccounts();
        var (username, normalized) = ValidateUsername(r.Username);
        ValidatePassword(r.Password);
        var name = PartyService.CleanName(string.IsNullOrWhiteSpace(r.DisplayName) ? username : r.DisplayName, ErrorCodes.InvalidRequest);

        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var id = Guid.NewGuid();
        try
        {
            await c.ExecAsync("""
                INSERT INTO players (id, display_name, created_at, username, username_normalized, password_hash)
                VALUES (@id, @n, @t, @u, @un, @ph)
                """, tx, ("id", id), ("n", name), ("t", clock.GetUtcNow()), ("u", username), ("un", normalized),
                ("ph", HashPassword(r.Password)));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new PartyException(409, ErrorCodes.UsernameTaken, "This username is taken.");
        }

        var secret = await IssueDeviceAsync(c, tx, id);
        await tx.CommitAsync();
        return new CredentialResponse(id, secret, name, username);
    }

    /// <summary>Turns the calling guest into an account; statistics and party membership are kept.</summary>
    public async Task<AccountDto> ClaimAccountAsync(Guid playerId, ClaimAccountRequest r)
    {
        RequireAccounts();
        var (username, normalized) = ValidateUsername(r.Username);
        ValidatePassword(r.Password);
        await using var c = await db.OpenConnectionAsync();
        try
        {
            var updated = await c.ExecAsync("""
                UPDATE players SET username = @u, username_normalized = @un, password_hash = @ph
                WHERE id = @id AND username IS NULL
                """, null, ("u", username), ("un", normalized), ("ph", HashPassword(r.Password)), ("id", playerId));
            if (updated == 0)
            {
                throw new PartyException(409, ErrorCodes.AlreadyAccount, "This player already has an account.");
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new PartyException(409, ErrorCodes.UsernameTaken, "This username is taken.");
        }

        return await GetAccountAsync(playerId);
    }

    public async Task<CredentialResponse> LoginAsync(LoginRequest r)
    {
        RequireAccounts();
        var normalized = (r.Username ?? "").Trim().ToLowerInvariant();
        await using var c = await db.OpenConnectionAsync();
        var row = (await c.ListAsync("SELECT id, password_hash, display_name, username FROM players WHERE username_normalized = @u", null,
            rd => (Id: rd.GetGuid(0), Hash: rd.IsDBNull(1) ? null : rd.GetString(1), Name: rd.GetString(2), User: rd.GetString(3)),
            ("u", normalized))).FirstOrDefault();

        // Verify against a dummy hash when the user does not exist so timing does not reveal valid usernames.
        var ok = VerifyPassword(r.Password ?? "", row.Hash ?? DummyHash);
        if (row.Id == Guid.Empty || !ok)
        {
            throw new PartyException(401, ErrorCodes.InvalidCredentials, "Wrong username or password.");
        }

        await using var tx = await c.BeginTransactionAsync();
        var secret = await IssueDeviceAsync(c, tx, row.Id);
        await tx.CommitAsync();
        return new CredentialResponse(row.Id, secret, row.Name, row.User);
    }

    public async Task<AccountDto> GetAccountAsync(Guid playerId)
    {
        await using var c = await db.OpenConnectionAsync();
        var row = (await c.ListAsync("""
            SELECT p.display_name, p.username, m.party_id
            FROM players p LEFT JOIN party_members m ON m.player_id = p.id AND m.left_at IS NULL
            WHERE p.id = @id
            """, null, rd => (Name: rd.GetString(0), User: rd.IsDBNull(1) ? null : rd.GetString(1), Party: rd.NGuid(2)),
            ("id", playerId))).FirstOrDefault();
        if (row.Name is null)
        {
            throw new PartyException(404, ErrorCodes.NotFound, "Player not found.");
        }

        return new AccountDto(playerId, row.Name, row.User, row.User is null, row.Party);
    }

    public async Task<TokenResponse> IssueTokenAsync(TokenRequest request)
    {
        if (string.IsNullOrEmpty(request.Secret) || request.Secret.Length > 200)
        {
            throw Unauthorized();
        }

        await using var c = await db.OpenConnectionAsync();
        var name = await c.ScalarAsync<string>("""
            WITH d AS (
                UPDATE device_credentials SET last_used_at = @t
                WHERE player_id = @p AND secret_hash = @h
                RETURNING player_id)
            UPDATE players SET last_seen_at = @t FROM d WHERE players.id = d.player_id
            RETURNING players.display_name
            """, null, ("t", clock.GetUtcNow()), ("p", request.PlayerId), ("h", Hash(request.Secret)));
        if (name is null)
        {
            throw Unauthorized();
        }

        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(options.Value.TokenLifetimeMinutes);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = expires.UtcDateTime,
            NotBefore = now.UtcDateTime.AddMinutes(-1),
            Subject = new ClaimsIdentity([new Claim("sub", request.PlayerId.ToString()), new Claim("name", name)]),
            SigningCredentials = new SigningCredentials(keys.Key, SecurityAlgorithms.HmacSha256),
        });
        return new TokenResponse(token, expires);
    }

    private async Task<string> IssueDeviceAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid playerId)
    {
        var secret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        await c.ExecAsync("INSERT INTO device_credentials (id, player_id, secret_hash, created_at) VALUES (@id, @p, @h, @t)", tx,
            ("id", Guid.NewGuid()), ("p", playerId), ("h", Hash(secret)), ("t", clock.GetUtcNow()));
        // Keep the newest 20 devices per player.
        await c.ExecAsync("""
            DELETE FROM device_credentials WHERE player_id = @p AND id NOT IN (
                SELECT id FROM device_credentials WHERE player_id = @p ORDER BY created_at DESC LIMIT 20)
            """, tx, ("p", playerId));
        return secret;
    }

    private void RequireAccounts()
    {
        if (!options.Value.AllowAccounts)
        {
            throw new PartyException(403, ErrorCodes.InvalidRequest, "Accounts are disabled on this server.");
        }
    }

    internal static (string Username, string Normalized) ValidateUsername(string? username)
    {
        var u = (username ?? "").Trim();
        if (u.Length is < 3 or > 24 || !u.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.'))
        {
            throw new PartyException(400, ErrorCodes.InvalidUsername, "Usernames are 3–24 characters: letters, digits, _ - .");
        }

        return (u, u.ToLowerInvariant());
    }

    internal static void ValidatePassword(string? password)
    {
        if (password is null || password.Length < 8 || password.Length > 128)
        {
            throw new PartyException(400, ErrorCodes.WeakPassword, "Passwords are 8–128 characters.");
        }
    }

    internal static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    internal static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static readonly string DummyHash = HashPassword(Guid.NewGuid().ToString());

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    private static PartyException Unauthorized() => new(401, ErrorCodes.Unauthorized, "Unknown player or wrong secret.");
}

/// <summary>Loads the token signing key from configuration or a key file (created on first start).</summary>
public sealed class SigningKeyProvider
{
    public SigningKeyProvider(IOptions<ServerOptions> options, IHostEnvironment env, ILogger<SigningKeyProvider> logger)
    {
        var o = options.Value;
        byte[] bytes;
        if (!string.IsNullOrWhiteSpace(o.SigningKey))
        {
            bytes = Encoding.UTF8.GetBytes(o.SigningKey);
            if (bytes.Length < 32)
            {
                throw new InvalidOperationException("ChestTracker:SigningKey must be at least 32 bytes.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(o.SigningKeyFile))
        {
            var path = Path.GetFullPath(o.SigningKeyFile, AppContext.BaseDirectory);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                logger.LogWarning("Generated a new token signing key at {Path}. Keep it private and back it up.", path);
            }

            bytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
            if (bytes.Length < 32)
            {
                throw new InvalidOperationException($"Signing key file {path} is too short.");
            }
        }
        else if (env.IsDevelopment())
        {
            // Development only: tokens become invalid on restart; clients silently re-authenticate.
            bytes = RandomNumberGenerator.GetBytes(64);
            logger.LogWarning("No signing key configured; using an ephemeral development key.");
        }
        else
        {
            throw new InvalidOperationException("Configure ChestTracker:SigningKey or ChestTracker:SigningKeyFile.");
        }

        Key = new SymmetricSecurityKey(bytes);
    }

    public SymmetricSecurityKey Key { get; }
}
