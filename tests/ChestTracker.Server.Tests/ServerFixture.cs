using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChestTracker.Core.Abstractions;
using ChestTracker.Core.Data;
using ChestTracker.Core.Tracking;
using ChestTracker.Protocol;
using ChestTracker.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ChestTracker.Server.Tests;

/// <summary>
/// Runs the real server (in-memory TestServer) against a real, throw-away PostgreSQL database.
/// The connection comes from CHESTTRACKER_TEST_PG or tools/pg-dev-connection.txt; tests are skipped without one.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private string? _adminConnection;
    private string? _database;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public bool Available { get; private set; }

    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        _adminConnection = FindConnectionString();
        if (_adminConnection is null)
        {
            SkipReason = "No PostgreSQL configured (set CHESTTRACKER_TEST_PG or create tools/pg-dev-connection.txt).";
            return;
        }

        _database = "ct_test_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await using var admin = new NpgsqlConnection(_adminConnection);
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {_database}", admin);
            await create.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            SkipReason = "PostgreSQL not reachable: " + ex.Message;
            return;
        }

        DatabaseConnectionString = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _database }.ToString();
        Factory = CreateFactory();
        _ = Factory.Server; // starts the host and runs migrations
        Available = true;
    }

    /// <summary>Connection string of this fixture's test database (for direct consistency checks).</summary>
    public string DatabaseConnectionString { get; private set; } = "";

    /// <summary>
    /// Moves the start of a player's current membership period into the past, for tests that submit backdated
    /// events to exercise other rules (event age, leaderboard periods). Events count only inside a membership period.
    /// </summary>
    public async Task BackdateMembershipAsync(Guid playerId, TimeSpan by)
    {
        await using var c = new NpgsqlConnection(DatabaseConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE party_membership_periods SET started_at = started_at - @by WHERE player_id = @x AND ended_at IS NULL", c);
        cmd.Parameters.AddWithValue("by", by);
        cmd.Parameters.AddWithValue("x", playerId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Stops the server and starts a new instance on the same database (a server restart).</summary>
    public async Task RestartAsync()
    {
        var old = Factory;
        Factory = CreateFactory();
        _ = Factory.Server;
        await old.DisposeAsync();
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        var cs = DatabaseConnectionString;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Postgres", cs);
            b.UseSetting("ChestTracker:SigningKey", "test-signing-key-test-signing-key-0123456789");
            b.UseSetting("ChestTracker:MaxPartySize", "4");
            b.UseSetting("ChestTracker:AuthRequestsPerMinute", "100000");
            b.UseSetting("ChestTracker:JoinAttemptsPerMinute", "100000");
            b.UseSetting("ChestTracker:RequestsPerMinute", "100000");
            b.UseSetting("ChestTracker:HubCallsPerSecond", "1000");
            b.UseSetting("ChestTracker:AdminApiKey", AdminKey);
            b.UseSetting("ChestTracker:BackupIntervalHours", "0");
            b.UseSetting("ChestTracker:BackupDirectory", BackupDirectory);
        });
    }

    public async Task DisposeAsync()
    {
        if (Available)
        {
            await Factory.DisposeAsync();
        }

        if (_adminConnection is not null && _database is not null)
        {
            NpgsqlConnection.ClearAllPools();
            try
            {
                await using var admin = new NpgsqlConnection(_adminConnection);
                await admin.OpenAsync();
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
            }
            catch
            {
                // Best effort.
            }
        }
    }

    public const string AdminKey = "test-admin-key";

    public string BackupDirectory { get; } = Path.Combine(Path.GetTempPath(), "ct-server-backups-" + Guid.NewGuid().ToString("N"));

    public string ConnectionString(string database) => new NpgsqlConnectionStringBuilder(_adminConnection) { Database = database }.ToString();

    public string? AdminConnection => _adminConnection;

    public HttpMessageHandler CreateHandler() => Factory.Server.CreateHandler();

    /// <summary>An HTTP client that identifies as the current protocol version (like the real app).</summary>
    public HttpClient CreateClient(int? protocol = ProtocolInfo.Version)
    {
        var http = Factory.CreateClient();
        if (protocol is { } p)
        {
            http.DefaultRequestHeaders.Add(ProtocolInfo.Header, p.ToString());
        }

        return http;
    }

    public async Task<TestPlayer> RegisterAsync(string name)
    {
        var http = CreateClient();
        var reg = await (await http.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name), SyncClient.Json))
            .Content.ReadFromJsonAsync<CredentialResponse>(SyncClient.Json);
        var token = await (await http.PostAsJsonAsync("/api/auth/token", new TokenRequest(reg!.PlayerId, reg.Secret), SyncClient.Json))
            .Content.ReadFromJsonAsync<TokenResponse>(SyncClient.Json);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return new TestPlayer(this, reg.PlayerId, reg.Secret, token.AccessToken, http);
    }

    private static string? FindConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("CHESTTRACKER_TEST_PG");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "tools", "pg-dev-connection.txt");
            if (File.Exists(file))
            {
                return File.ReadAllText(file).Trim();
            }
        }

        return null;
    }
}

[CollectionDefinition("server")]
public sealed class ServerCollection : ICollectionFixture<ServerFixture>;

public sealed class TestPlayer(ServerFixture fixture, Guid id, string secret, string token, HttpClient http) : IAsyncDisposable
{
    private HubConnection? _hub;

    public Guid Id { get; } = id;
    public string Secret { get; } = secret;
    public HttpClient Http { get; } = http;

    public async Task<HubConnection> HubAsync()
    {
        if (_hub is not null) return _hub;
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(fixture.Factory.Server.BaseAddress, Routes.PartyHub), o =>
            {
                o.HttpMessageHandlerFactory = _ => fixture.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.Headers[ProtocolInfo.Header] = ProtocolInfo.Version.ToString();
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        await _hub.StartAsync();
        return _hub;
    }

    public async Task<T> PostAsync<T>(string path, object? body = null)
    {
        var response = await Http.PostAsJsonAsync(path, body ?? new { }, SyncClient.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(SyncClient.Json))!;
    }

    public async Task<(int Status, ErrorDto? Error)> PostRawAsync(string path, object? body = null)
    {
        var response = await Http.PostAsJsonAsync(path, body ?? new { }, SyncClient.Json);
        ErrorDto? error = null;
        if (!response.IsSuccessStatusCode)
        {
            try { error = await response.Content.ReadFromJsonAsync<ErrorDto>(SyncClient.Json); } catch (JsonException) { }
        }

        return ((int)response.StatusCode, error);
    }

    public async Task<T> GetAsync<T>(string path) => (await Http.GetFromJsonAsync<T>(path, SyncClient.Json))!;

    public async Task<PartyStateDto> StateAsync() => await GetAsync<PartyStateDto>("/api/parties/current");

    public async Task<List<EventAck>> SubmitAsync(params ChestEventDto[] events) =>
        await (await HubAsync()).InvokeAsync<List<EventAck>>(HubMethods.SubmitEvents, events.ToList());

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
        Http.Dispose();
    }
}

public static class Ev
{
    public static ChestEventDto Add(DateTimeOffset at, Guid? shared = null, Guid? match = null, EventSourceType source = EventSourceType.Manual) =>
        new(Guid.NewGuid(), match ?? Guid.NewGuid(), shared, EventKind.Add, source, 1, null, at, null);

    public static ChestEventDto Undo(ChestEventDto target, DateTimeOffset at) =>
        new(Guid.NewGuid(), target.LocalMatchId, target.SharedMatchId, EventKind.Undo, EventSourceType.Manual, -1, target.EventId, at, null);

    public static ChestEventDto Correction(int delta, DateTimeOffset at) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, EventKind.Correction, EventSourceType.Manual, delta, null, at, null);
}

/// <summary>A desktop client: real local SQLite + tracking engine + sync client.</summary>
public sealed class TestDevice : IAsyncDisposable
{
    private readonly ServerFixture _fixture;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ct-device-" + Guid.NewGuid().ToString("N"));

    public TestDevice(ServerFixture fixture, ISyncAccountStore? accounts = null)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_dir);
        var (db, _) = DatabaseBootstrapper.Open(Path.Combine(_dir, "chests.db"), new BackupService(Path.Combine(_dir, "backups")));
        Database = db;
        Engine = new TrackingEngine(new ChestRepository(db), Clock, new TrackingOptions { ManualDebounce = TimeSpan.Zero });
        Engine.Initialize();
        Outbox = new SyncOutbox(db);
        Accounts = accounts ?? new InMemoryAccountStore();
        Client = NewClient();
    }

    /// <summary>Real time plus a manual offset, so tests can space chests realistically (в‰Ґ 1 s apart).</summary>
    public SteppingClock Clock { get; } = new();
    public SqliteDatabase Database { get; }
    public TrackingEngine Engine { get; }

    /// <summary>Opens a chest a few "seconds" after the previous one (starting a match first if none is running).</summary>
    public void OpenChest()
    {
        if (Engine.CurrentMatch is null)
        {
            Engine.StartMatch();
        }

        Clock.Step(3);
        Engine.AddChestManual(fromHotkey: true);
    }
    public SyncOutbox Outbox { get; }
    public ISyncAccountStore Accounts { get; }
    public SyncClient Client { get; private set; }

    public SyncClient NewClient() => new(Engine, Outbox, Accounts, () => new SyncPreferences(), _fixture.CreateHandler);

    /// <summary>Simulates going offline: the sync client is disposed, local tracking continues.</summary>
    public async Task GoOfflineAsync() => await Client.DisposeAsync();

    public void ComeBackOnline()
    {
        Client = NewClient();
        Client.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        Database.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

public sealed class SteppingClock : IClock
{
    private TimeSpan _offset;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + _offset;

    public void Step(double seconds) => _offset += TimeSpan.FromSeconds(seconds);
}

public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(50);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(100);
        }
    }
}

