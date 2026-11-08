using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ChestTracker.Core.Data;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Core.Models;
using ChestTracker.Core.Tracking;
using ChestTracker.Protocol;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ChestTracker.Sync;

public sealed class SyncAccount
{
    public required string ServerUrl { get; init; }
    public required Guid PlayerId { get; init; }
    public required string Secret { get; init; }
    public string DisplayName { get; set; } = "";

    /// <summary>Null for guests.</summary>
    public string? Username { get; set; }

    public bool IsGuest => Username is null;
}

/// <summary>
/// The signed-in identity as confirmed by the server through an authenticated request (GET /api/me with a
/// token issued for the stored credential). Never built from cached or locally typed values.
/// </summary>
public sealed record VerifiedIdentity(string ServerUrl, Guid PlayerId, string DisplayName, string? Username, bool IsGuest);

/// <summary>Persists the device account (the app encrypts the secret with DPAPI).</summary>
public interface ISyncAccountStore
{
    SyncAccount? Load();

    void Save(SyncAccount? account);
}

public sealed class InMemoryAccountStore : ISyncAccountStore
{
    private SyncAccount? _account;

    public SyncAccount? Load() => _account;

    public void Save(SyncAccount? account) => _account = account;
}

public sealed class SyncPreferences
{
    /// <summary>Owner: starting a local match also starts a shared match for the party.</summary>
    public bool AutoShareMatches { get; set; } = true;

    /// <summary>Member: automatically join shared matches started by the owner.</summary>
    public bool AutoJoinSharedMatches { get; set; } = true;

    /// <summary>Connect automatically at start-up (cleared when the user presses Disconnect).</summary>
    public bool AutoConnect { get; set; } = true;
}

public enum SyncStatus
{
    NotConfigured,
    Disconnected,
    Connecting,
    Online,
    Offline,
    Incompatible,
}

public sealed class SyncApiException(string code, string message, int status) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed class SharedMatchEventArgs(SharedMatchDto match, bool joined) : EventArgs
{
    public SharedMatchDto Match { get; } = match;
    public bool Joined { get; } = joined;
}

/// <summary>
/// Client side of party synchronization. Local tracking never depends on it: events always go to the local
/// SQLite log first, then a durable outbox delivers them when the server is reachable (with automatic reconnect
/// and token refresh). Shared match start/end from the party owner are mirrored into the local engine.
/// </summary>
public sealed class SyncClient : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TrackingEngine _engine;
    private readonly SyncOutbox _outbox;
    private readonly ISyncAccountStore _accounts;
    private readonly Func<SyncPreferences> _preferences;
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly Channel<Func<Task>> _matchOps = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private HubConnection? _hub;
    private HttpClient? _http;
    private SyncAccount? _account;
    private string? _token;
    private DateTimeOffset _tokenExpires;
    private Task? _connectLoop;
    private Timer? _retryTimer;
    private Timer? _pingTimer;
    private volatile bool _manualDisconnect;
    private int _suppressEcho;
    private Guid? _lastLocalMatch;
    private bool _started;

    /// <param name="handlerFactory">Test hook: routes HTTP and SignalR (long polling) through a custom handler.</param>
    public SyncClient(TrackingEngine engine, SyncOutbox outbox, ISyncAccountStore accounts, Func<SyncPreferences>? preferences = null,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        _engine = engine;
        _outbox = outbox;
        _accounts = accounts;
        _preferences = preferences ?? (() => new SyncPreferences());
        _handlerFactory = handlerFactory;
        _ = Task.Run(ProcessMatchOpsAsync);
    }

    /// <summary>Raised on a background thread whenever status, party or pending count changes.</summary>
    public event EventHandler? StateChanged;

    public event EventHandler<ActivityDto>? Activity;

    public event EventHandler<SharedMatchEventArgs>? SharedMatchStarted;

    public event EventHandler<SharedMatchDto>? SharedMatchEnded;

    public event EventHandler? RemovedFromParty;

    public SyncStatus Status { get; private set; } = SyncStatus.NotConfigured;

    public PartyStateDto? Party { get; private set; }

    public SyncAccount? Account => _account;

    /// <summary>
    /// Server-confirmed identity of the current account, or null until the server has answered an authenticated
    /// request in this run (the cached <see cref="SyncAccount.DisplayName"/> is not proof of anything).
    /// Cleared when the account is switched, detached or signed out.
    /// </summary>
    public VerifiedIdentity? VerifiedIdentity =>
        _verified is { } v && _account is { } a && v.PlayerId == a.PlayerId
        && string.Equals(v.ServerUrl, a.ServerUrl, StringComparison.OrdinalIgnoreCase)
            ? v
            : null;

    private VerifiedIdentity? _verified;

    public string? LastError { get; private set; }

    /// <summary>Stable code of <see cref="LastError"/> (e.g. unreachable, tls_error, client_outdated).</summary>
    public string? LastErrorCode { get; private set; }

    public int PendingCount
    {
        get
        {
            try
            {
                return _outbox.Count;
            }
            catch (ObjectDisposedException)
            {
                return 0; // late UI refresh during shutdown
            }
        }
    }

    public bool IsOwner => Party is { } p && _account is { } a && p.OwnerId == a.PlayerId;

    /// <summary>Round-trip time of the last hub ping, null when offline.</summary>
    public int? LatencyMs { get; private set; }

    /// <summary>Status reported by the server at the last successful check.</summary>
    public ServerStatusDto? ServerStatus { get; private set; }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _account = _accounts.Load();
        _lastLocalMatch = _engine.CurrentMatch?.Id;
        _engine.Changed += OnEngineChanged;
        _retryTimer = new Timer(_ => _ = FlushAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        _pingTimer = new Timer(_ => _ = PingAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        if (_account is null)
        {
            SetStatus(SyncStatus.NotConfigured);
        }
        else if (_preferences().AutoConnect)
        {
            BeginConnect();
        }
        else
        {
            SetStatus(SyncStatus.Disconnected);
        }
    }

    // ================================================================ account

    /// <summary>Checks that a server is reachable and speaks a compatible protocol.</summary>
    public async Task<ServerStatusDto> CheckServerAsync(string serverUrl)
    {
        var url = NormalizeUrl(serverUrl);
        using var http = CreateHttp(url);
        var status = await Call(() => http.GetAsync("api/status", _cts.Token), r => ReadAsync<ServerStatusDto>(r));
        if (status.ProtocolVersion < ProtocolInfo.MinServerVersion)
        {
            throw new SyncApiException(ErrorCodes.ServerOutdated,
                $"The server is too old (protocol {status.ProtocolVersion}, this app needs {ProtocolInfo.MinServerVersion}).", 0);
        }

        if (status.MinClientProtocol > ProtocolInfo.Version)
        {
            throw new SyncApiException(ErrorCodes.ClientOutdated,
                $"This app is too old for the server (protocol {ProtocolInfo.Version}, server needs {status.MinClientProtocol}).", 426);
        }

        ServerStatus = status;
        return status;
    }

    /// <summary>Joins a server as a guest (only a display name).</summary>
    public Task RegisterAsync(string serverUrl, string displayName) =>
        SignInAsync(serverUrl, http => http.PostAsJsonAsync("api/auth/register", new RegisterRequest(displayName), Json, _cts.Token));

    /// <summary>Creates a username/password account; usable from any computer.</summary>
    public Task RegisterAccountAsync(string serverUrl, string username, string password, string displayName) =>
        SignInAsync(serverUrl, http => http.PostAsJsonAsync("api/auth/accounts", new AccountRegisterRequest(username, password, displayName), Json, _cts.Token));

    /// <summary>Logs in to an existing account. The password is sent once over the connection and never stored.</summary>
    public Task LoginAsync(string serverUrl, string username, string password) =>
        SignInAsync(serverUrl, http => http.PostAsJsonAsync("api/auth/login", new LoginRequest(username, password), Json, _cts.Token));

    /// <summary>Turns the current guest into an account (keeps statistics and party).</summary>
    public async Task ClaimAccountAsync(string username, string password)
    {
        var account = await SendAsync<AccountDto>(HttpMethod.Post, "api/me/claim", new ClaimAccountRequest(username, password));
        if (_account is { } a)
        {
            a.Username = account.Username;
            _accounts.Save(a);
            Verify(a, account);
        }

        Raise();
    }

    private async Task SignInAsync(string serverUrl, Func<HttpClient, Task<HttpResponseMessage>> request)
    {
        var url = NormalizeUrl(serverUrl);
        await CheckServerAsync(url);
        using var http = CreateHttp(url);
        var credential = await Call(() => request(http), r => ReadAsync<CredentialResponse>(r));
        await ResetConnectionAsync();
        if (_account is not null && (_account.ServerUrl != url || _account.PlayerId != credential.PlayerId))
        {
            _outbox.EndParty(); // never deliver one identity's queue as another
            Party = null;
        }

        _verified = null;
        _account = new SyncAccount
        {
            ServerUrl = url, PlayerId = credential.PlayerId, Secret = credential.Secret, DisplayName = credential.DisplayName,
            Username = credential.Username,
        };
        _accounts.Save(_account);
        _manualDisconnect = false;
        BeginConnect();
    }

    /// <summary>Switches to a previously saved credential (another server or account) and connects.</summary>
    public async Task UseAccountAsync(SyncAccount account)
    {
        await ResetConnectionAsync();
        if (_account is null || _account.ServerUrl != account.ServerUrl || _account.PlayerId != account.PlayerId)
        {
            _outbox.EndParty();
            Party = null;
        }

        if (_account is null || _account.ServerUrl != account.ServerUrl || _account.PlayerId != account.PlayerId)
        {
            _verified = null;
        }

        _account = account;
        _accounts.Save(account);
        LatencyMs = null;
        _manualDisconnect = false;
        BeginConnect();
    }

    /// <summary>
    /// Leaves the current server without forgetting its saved credential (e.g. before signing in to another server).
    /// </summary>
    public async Task DetachAsync()
    {
        await ResetConnectionAsync();
        _outbox.EndParty();
        _account = null;
        _verified = null;
        Party = null;
        LatencyMs = null;
        LastError = null;
        LastErrorCode = null;
        SetStatus(SyncStatus.NotConfigured);
    }

    /// <summary>Connects with the saved credentials (after <see cref="DisconnectAsync"/> or a failed start).</summary>
    public void Connect()
    {
        _manualDisconnect = false;
        if (_account is null)
        {
            SetStatus(SyncStatus.NotConfigured);
            return;
        }

        BeginConnect();
    }

    /// <summary>Goes offline on purpose. Events keep queuing locally and are sent after the next connect.</summary>
    public async Task DisconnectAsync()
    {
        _manualDisconnect = true;
        await ResetConnectionAsync();
        LatencyMs = null;
        SetStatus(_account is null ? SyncStatus.NotConfigured : SyncStatus.Disconnected);
    }

    /// <summary>Forgets the account on this device only (server data stays).</summary>
    public async Task SignOutAsync()
    {
        await ResetConnectionAsync();
        _outbox.EndParty();
        _account = null;
        _verified = null;
        _accounts.Save(null);
        Party = null;
        LatencyMs = null;
        SetStatus(SyncStatus.NotConfigured);
    }

    private async Task PingAsync()
    {
        if (_hub is not { State: HubConnectionState.Connected } hub)
        {
            return;
        }

        try
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await hub.InvokeAsync<PingResponse>(HubMethods.Ping, _cts.Token);
            LatencyMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Raise();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LatencyMs = null;
        }
    }

    /// <summary>Turns transport failures into clear, stable error codes.</summary>
    private static async Task<T> Call<T>(Func<Task<HttpResponseMessage>> send, Func<HttpResponseMessage, Task<T>> read)
    {
        HttpResponseMessage response;
        try
        {
            response = await send();
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new SyncApiException("tls_error", "Secure connection failed: the server's HTTPS certificate is not trusted.", 0);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncApiException("unreachable", "The server cannot be reached: " + ex.Message, 0);
        }
        catch (TaskCanceledException)
        {
            throw new SyncApiException("timeout", "The server did not answer in time.", 0);
        }

        return await read(response);
    }

    /// <summary>Deletes the player and all of their reported events on the server, then signs out.</summary>
    public async Task DeleteServerDataAsync()
    {
        await SendAsync<JsonElement>(HttpMethod.Delete, "api/me");
        await SignOutAsync();
    }

    public async Task RenameAsync(string displayName)
    {
        var profile = await SendAsync<ProfileDto>(HttpMethod.Put, "api/me", new RenameRequest(displayName));
        if (_account is { } a)
        {
            a.DisplayName = profile.DisplayName;
            _accounts.Save(a);
            if (profile.PlayerId == a.PlayerId)
            {
                _verified = new VerifiedIdentity(a.ServerUrl, a.PlayerId, profile.DisplayName, a.Username, a.IsGuest);
            }
        }

        Raise();
    }

    // ================================================================= party

    public async Task<PartyStateDto> CreatePartyAsync(string name, int maxPlayers)
    {
        var state = await SendAsync<PartyStateDto>(HttpMethod.Post, "api/parties", new CreatePartyRequest(name, maxPlayers));
        ApplyParty(state);
        return state;
    }

    public async Task<PartyStateDto> JoinPartyAsync(string code)
    {
        var state = await SendAsync<PartyStateDto>(HttpMethod.Post, "api/parties/join", new JoinPartyRequest(code));
        ApplyParty(state);
        return state;
    }

    public async Task LeavePartyAsync()
    {
        await SendAsync<JsonElement>(HttpMethod.Post, "api/parties/current/leave");
        ApplyParty(null);
    }

    public async Task RegenerateInviteAsync() => ApplyParty(await SendAsync<PartyStateDto>(HttpMethod.Post, "api/parties/current/invite"));

    public Task KickAsync(Guid playerId) => SendAsync<JsonElement>(HttpMethod.Post, $"api/parties/current/kick/{playerId}");

    public Task TransferOwnershipAsync(Guid playerId) => SendAsync<JsonElement>(HttpMethod.Post, $"api/parties/current/transfer/{playerId}");

    public Task SetSharingAsync(bool shareCounts, bool shareActivity) =>
        SendAsync<JsonElement>(HttpMethod.Put, "api/parties/current/sharing", new SharingRequest(shareCounts, shareActivity));

    public async Task RefreshPartyAsync()
    {
        try
        {
            ApplyParty(await SendAsync<PartyStateDto>(HttpMethod.Get, "api/parties/current"));
        }
        catch (SyncApiException ex) when (ex.Code == ErrorCodes.NotMember)
        {
            ApplyParty(null);
        }
    }

    public Task<List<LeaderboardEntryDto>> GetLeaderboardAsync(LeaderboardPeriod period)
    {
        var offset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes;
        return SendAsync<List<LeaderboardEntryDto>>(HttpMethod.Get, $"api/parties/current/leaderboard?period={period}&tzOffsetMinutes={offset}");
    }

    public Task<List<SharedMatchHistoryDto>> GetHistoryAsync(int limit = 20) =>
        SendAsync<List<SharedMatchHistoryDto>>(HttpMethod.Get, $"api/parties/current/matches?limit={limit}");

    // ========================================================= shared matches

    /// <summary>Owner: starts a shared match and links the current local match (starting one if needed).</summary>
    public async Task<SharedMatchDto> StartSharedMatchAsync()
    {
        var hub = RequireOnline();
        var match = await hub.InvokeAsync<SharedMatchDto>(HubMethods.StartSharedMatch, _cts.Token);
        if (_engine.CurrentMatch is not { } local)
        {
            local = WithoutEcho(() => _engine.StartMatch());
        }

        _outbox.LinkMatch(local.Id, match.Id);
        return match;
    }

    public async Task EndSharedMatchAsync()
    {
        if (Party?.ActiveMatch is { } m)
        {
            await RequireOnline().InvokeAsync(HubMethods.EndSharedMatch, m.Id, _cts.Token);
        }
    }

    public async Task RespondSharedMatchAsync(Guid matchId, bool accept)
    {
        await RequireOnline().InvokeAsync(HubMethods.RespondSharedMatch, matchId, accept, _cts.Token);
        if (accept && Party?.ActiveMatch is { } m && m.Id == matchId)
        {
            JoinLocally(m);
        }
    }

    // ================================================================== flush

    /// <summary>Queues new local events and sends pending ones while online. Safe to call any time.</summary>
    public async Task FlushAsync()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        await _flushLock.WaitAsync();
        try
        {
            _outbox.EnqueueNew();
            while (_hub is { State: HubConnectionState.Connected } hub && Party is not null && !_cts.IsCancellationRequested)
            {
                var items = _outbox.Peek(100);
                if (items.Count == 0)
                {
                    break;
                }

                List<EventAck> acks;
                try
                {
                    acks = await hub.InvokeAsync<List<EventAck>>(HubMethods.SubmitEvents, items.Select(ToDto).ToList(), _cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _outbox.MarkFailed(items.Select(i => i.EventId), ex.Message);
                    LastError = ex.Message;
                    break;
                }

                // Accepted, duplicate and rejected are all final; only transport failures are retried.
                _outbox.Remove(acks.Select(a => a.EventId));
                foreach (var rejected in acks.Where(a => a.Status == AckStatus.Rejected))
                {
                    Log.Warn($"Server rejected event {rejected.EventId}: {rejected.Reason}");
                }

                if (acks.Count == 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Sync flush failed", ex);
            LastError = ex.Message;
        }
        finally
        {
            _flushLock.Release();
        }

        Raise();
    }

    // ============================================================= connection

    private void BeginConnect()
    {
        if (_account is null || _manualDisconnect || _cts.IsCancellationRequested || _connectLoop is { IsCompleted: false })
        {
            return;
        }

        _connectLoop = Task.Run(ConnectLoopAsync);
    }

    private async Task ConnectLoopAsync()
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!_cts.IsCancellationRequested && _account is { } account && !_manualDisconnect)
        {
            try
            {
                SetStatus(SyncStatus.Connecting);
                await CheckServerAsync(account.ServerUrl);
                var hub = EnsureHub();
                if (hub.State == HubConnectionState.Disconnected)
                {
                    SetStatus(SyncStatus.Connecting);
                    await hub.StartAsync(_cts.Token);
                }

                await OnConnectedAsync();
                return;
            }
            catch (SyncApiException ex) when (ex.Code is ErrorCodes.ClientOutdated or ErrorCodes.ServerOutdated)
            {
                // Retrying cannot help until one side is updated.
                LastError = ex.Message;
                LastErrorCode = ex.Code;
                SetStatus(SyncStatus.Incompatible);
                return;
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                LastError = ex.Message;
                LastErrorCode = (ex as SyncApiException)?.Code ?? "unreachable";
                _token = null;
                SetStatus(SyncStatus.Offline);
                if (ex is SyncApiException { Status: 401 })
                {
                    // The server no longer knows this device (account deleted or different server database).
                    Log.Warn("Sync account rejected by server");
                }

                try
                {
                    await Task.Delay(delay, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }

    private HubConnection EnsureHub()
    {
        if (_hub is not null)
        {
            return _hub;
        }

        var account = _account ?? throw new InvalidOperationException("Not registered.");
        _http ??= CreateHttp(account.ServerUrl);
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(account.ServerUrl), $"{Routes.PartyHub}?{ProtocolInfo.QueryParameter}={ProtocolInfo.Version}"), o =>
            {
                o.AccessTokenProvider = async () => await GetTokenAsync();
                o.Headers[ProtocolInfo.Header] = ProtocolInfo.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (_handlerFactory is not null)
                {
                    o.HttpMessageHandlerFactory = _ => _handlerFactory();
                    o.Transports = HttpTransportType.LongPolling;
                }
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        hub.On<PartyStateDto>(ClientMethods.PartyUpdated, state => ApplyParty(state));
        hub.On<ActivityDto>(ClientMethods.Activity, a => Activity?.Invoke(this, a));
        hub.On<SharedMatchDto>(ClientMethods.SharedMatchStarted, m => _matchOps.Writer.TryWrite(() => HandleSharedStartedAsync(m)));
        hub.On<SharedMatchDto>(ClientMethods.SharedMatchEnded, m => _matchOps.Writer.TryWrite(() => HandleSharedEndedAsync(m)));
        hub.On<Guid>(ClientMethods.RemovedFromParty, _ =>
        {
            ApplyParty(null);
            RemovedFromParty?.Invoke(this, EventArgs.Empty);
        });
        hub.Reconnecting += _ =>
        {
            // The server may have restarted with a new signing key: fetch a fresh token on reconnect.
            _token = null;
            SetStatus(SyncStatus.Offline);
            return Task.CompletedTask;
        };
        hub.Reconnected += async _ => await OnConnectedAsync();
        hub.Closed += _ =>
        {
            _token = null;
            LatencyMs = null;
            if (_manualDisconnect || !ReferenceEquals(_hub, hub))
            {
                return Task.CompletedTask; // closed on purpose
            }

            SetStatus(_account is null ? SyncStatus.NotConfigured : SyncStatus.Offline);
            BeginConnect();
            return Task.CompletedTask;
        };
        _hub = hub;
        return hub;
    }

    private async Task OnConnectedAsync()
    {
        if (_hub is not { } hub)
        {
            return;
        }

        var state = await hub.InvokeAsync<PartyStateDto?>(HubMethods.GetPartyState, _cts.Token);
        LastError = null;
        SetStatus(SyncStatus.Online);
        ApplyParty(state);
        await PingAsync();
        await RefreshAccountAsync();
        await FlushAsync();
    }

    /// <summary>Picks up name/username changes made on another computer.</summary>
    private async Task RefreshAccountAsync()
    {
        try
        {
            var me = await SendAsync<AccountDto>(HttpMethod.Get, "api/me");
            if (_account is { } a)
            {
                if (a.DisplayName != me.DisplayName || a.Username != me.Username)
                {
                    a.DisplayName = me.DisplayName;
                    a.Username = me.Username;
                    _accounts.Save(a);
                }

                Verify(a, me);
                Raise();
            }
        }
        catch (Exception ex) when (ex is SyncApiException or HttpRequestException)
        {
            Log.Warn("Could not refresh account", ex);
        }
    }

    /// <summary>Records what the server answered for the authenticated account (ignored if it is someone else).</summary>
    private void Verify(SyncAccount account, AccountDto me)
    {
        if (me.PlayerId != account.PlayerId)
        {
            Log.Warn("Server returned a profile for another player; nickname not trusted");
            _verified = null;
            return;
        }

        _verified = new VerifiedIdentity(account.ServerUrl, me.PlayerId, me.DisplayName, me.Username, me.IsGuest);
    }

    private async Task ResetConnectionAsync()
    {
        var hub = _hub;
        _hub = null;
        _token = null;
        if (hub is not null)
        {
            await hub.DisposeAsync();
        }

        _http?.Dispose();
        _http = null;
    }

    private async Task<string?> GetTokenAsync()
    {
        await _tokenLock.WaitAsync();
        try
        {
            if (_token is not null && _tokenExpires - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2))
            {
                return _token;
            }

            var account = _account ?? throw new InvalidOperationException("Not registered.");
            _http ??= CreateHttp(account.ServerUrl);
            var response = await _http.PostAsJsonAsync("api/auth/token", new TokenRequest(account.PlayerId, account.Secret), Json, _cts.Token);
            var token = await ReadAsync<TokenResponse>(response);
            _token = token.AccessToken;
            _tokenExpires = token.ExpiresAt;
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null)
    {
        var account = _account ?? throw new SyncApiException("not_registered", "Connect to a server first.", 0);
        _http ??= CreateHttp(account.ServerUrl);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync());
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            }

            var response = await _http.SendAsync(request, _cts.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _token = null;
                continue;
            }

            return await ReadAsync<T>(response);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                ErrorDto? error = null;
                try
                {
                    error = await response.Content.ReadFromJsonAsync<ErrorDto>(Json);
                }
                catch (JsonException)
                {
                }

                throw new SyncApiException(error?.Code ?? ((int)response.StatusCode).ToString(),
                    error?.Message ?? response.ReasonPhrase ?? "Request failed", (int)response.StatusCode);
            }

            return (await response.Content.ReadFromJsonAsync<T>(Json))!;
        }
    }

    private HttpClient CreateHttp(string serverUrl)
    {
        var http = new HttpClient(_handlerFactory?.Invoke() ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = new Uri(serverUrl),
            Timeout = TimeSpan.FromSeconds(15),
        };
        http.DefaultRequestHeaders.Add(ProtocolInfo.Header, ProtocolInfo.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return http;
    }

    /// <summary>True for plain http to anything but this computer or a private network (traffic would be readable).</summary>
    public static bool IsInsecure(string? serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        if (uri.IsLoopback) return false;
        if (System.Net.IPAddress.TryParse(uri.Host, out var ip))
        {
            var b = ip.GetAddressBytes();
            var isPrivate = b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168));
            return !isPrivate;
        }

        return !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeUrl(string url)
    {
        var trimmed = (url ?? "").Trim();
        if (trimmed.Length == 0)
        {
            throw new SyncApiException("invalid_url", "Enter a server address.", 0);
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        var uri = new Uri(trimmed.TrimEnd('/') + "/");
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new SyncApiException("invalid_url", "Use an http(s) server address.", 0);
        }

        return uri.ToString();
    }

    private HubConnection RequireOnline() =>
        _hub is { State: HubConnectionState.Connected } hub ? hub : throw new SyncApiException("offline", "The party server is not reachable.", 0);

    // ======================================================= local integration

    private void ApplyParty(PartyStateDto? state)
    {
        Party = state;
        if (state is null)
        {
            if (_outbox.PartyId is not null) _outbox.EndParty();
        }
        else if (_outbox.PartyId != state.PartyId)
        {
            _outbox.BeginParty(state.PartyId);
        }

        Raise();
    }

    private void OnEngineChanged(object? sender, TrackingChangedEventArgs e)
    {
        var previous = _lastLocalMatch;
        var current = e.Snapshot.CurrentMatch?.Id;
        _lastLocalMatch = current;

        if (Volatile.Read(ref _suppressEcho) == 0 && _preferences().AutoShareMatches)
        {
            // Owner's own F9/F10 drive the shared match for the whole party.
            if (e.Kind == TrackingChangeKind.MatchEnded && previous is { } ended)
            {
                _matchOps.Writer.TryWrite(async () =>
                {
                    if (IsOwner && Party?.ActiveMatch is { } active && _outbox.GetSharedMatch(ended) == active.Id && IsConnected)
                    {
                        await _hub!.InvokeAsync(HubMethods.EndSharedMatch, active.Id, _cts.Token);
                    }
                });
            }
            else if (e.Kind == TrackingChangeKind.MatchStarted && current is { } started)
            {
                _matchOps.Writer.TryWrite(async () =>
                {
                    if (IsOwner && Party is { Members.Count: > 1 } && IsConnected && _outbox.GetSharedMatch(started) is null)
                    {
                        if (Party.ActiveMatch is { } old)
                        {
                            await _hub!.InvokeAsync(HubMethods.EndSharedMatch, old.Id, _cts.Token);
                        }

                        var match = await _hub!.InvokeAsync<SharedMatchDto>(HubMethods.StartSharedMatch, _cts.Token);
                        _outbox.LinkMatch(started, match.Id);
                    }
                });
            }
        }

        if (e.Kind is TrackingChangeKind.ChestAdded or TrackingChangeKind.ChestRemoved or TrackingChangeKind.Corrected
            or TrackingChangeKind.ReviewResolved)
        {
            _ = FlushAsync();
        }
    }

    private bool IsConnected => _hub is { State: HubConnectionState.Connected };

    private async Task HandleSharedStartedAsync(SharedMatchDto match)
    {
        if (_account is null)
        {
            return;
        }

        if (match.StartedBy == _account.PlayerId)
        {
            return; // linked by whoever started it on this device
        }

        if (_preferences().AutoJoinSharedMatches && IsConnected)
        {
            await _hub!.InvokeAsync(HubMethods.RespondSharedMatch, match.Id, true, _cts.Token);
            JoinLocally(match);
            SharedMatchStarted?.Invoke(this, new SharedMatchEventArgs(match, joined: true));
        }
        else
        {
            SharedMatchStarted?.Invoke(this, new SharedMatchEventArgs(match, joined: false));
        }
    }

    private Task HandleSharedEndedAsync(SharedMatchDto match)
    {
        if (_engine.CurrentMatch is { } local && _outbox.GetSharedMatch(local.Id) == match.Id)
        {
            WithoutEcho(() => _engine.EndMatch());
        }

        SharedMatchEnded?.Invoke(this, match);
        return Task.CompletedTask;
    }

    private void JoinLocally(SharedMatchDto match)
    {
        if (_engine.CurrentMatch is { } current && _outbox.GetSharedMatch(current.Id) == match.Id)
        {
            return;
        }

        var local = WithoutEcho(() => _engine.StartMatch());
        _outbox.LinkMatch(local.Id, match.Id);
    }

    private T WithoutEcho<T>(Func<T> action)
    {
        Interlocked.Increment(ref _suppressEcho);
        try
        {
            return action();
        }
        finally
        {
            Interlocked.Decrement(ref _suppressEcho);
        }
    }

    private async Task ProcessMatchOpsAsync()
    {
        try
        {
            await foreach (var op in _matchOps.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    await op();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn("Shared match operation failed", ex);
                    LastError = ex.Message;
                    Raise();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static ChestEventDto ToDto(OutboxItem i) => new(
        i.EventId, i.LocalMatchId, i.SharedMatchId, (EventKind)(int)i.Kind,
        i.Source == EventSource.Automatic ? EventSourceType.Automatic : EventSourceType.Manual,
        i.Delta, i.UndoesEventId, i.Timestamp, i.Confidence);

    private void SetStatus(SyncStatus status)
    {
        if (Status != status)
        {
            Status = status;
            Raise();
        }
    }

    private void Raise()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("Sync state listener failed", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _engine.Changed -= OnEngineChanged;
        _cts.Cancel();
        _matchOps.Writer.TryComplete();
        _retryTimer?.Dispose();
        _pingTimer?.Dispose();
        await ResetConnectionAsync();
    }

    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) =>
            TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(context.PreviousRetryCount, 5))));
    }
}
