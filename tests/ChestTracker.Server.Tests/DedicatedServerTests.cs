using System.Net;
using System.Net.Http.Json;
using ChestTracker.Protocol;
using ChestTracker.Server.Data;
using ChestTracker.Server.Services;
using ChestTracker.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ChestTracker.Server.Tests;

[Collection("server")]
public class DedicatedServerTests(ServerFixture fx)
{
    private static string NewUser() => "u" + Guid.NewGuid().ToString("N")[..10];

    [SkippableFact]
    public async Task Status_is_public_and_reports_protocol()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        using var http = fx.CreateClient(protocol: null);

        var status = await http.GetFromJsonAsync<ServerStatusDto>("/api/status", SyncClient.Json);

        Assert.Equal(ProtocolInfo.Version, status!.ProtocolVersion);
        Assert.True(status.AccountsEnabled && status.GuestsEnabled);
    }

    [SkippableFact]
    public async Task Outdated_clients_get_426_on_api_and_hub()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        using var old = fx.CreateClient(protocol: null);

        var api = await old.PostAsJsonAsync("/api/auth/register", new RegisterRequest("Old"), SyncClient.Json);
        var hub = await old.PostAsync(Routes.PartyHub + "/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.UpgradeRequired, api.StatusCode);
        Assert.Equal(ErrorCodes.ClientOutdated, (await api.Content.ReadFromJsonAsync<ErrorDto>(SyncClient.Json))!.Code);
        Assert.Equal(HttpStatusCode.UpgradeRequired, hub.StatusCode);
    }

    [SkippableFact]
    public async Task Accounts_work_from_several_computers_and_reject_wrong_passwords()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        using var http = fx.CreateClient();
        var user = NewUser();

        var created = await (await http.PostAsJsonAsync("/api/auth/accounts", new AccountRegisterRequest(user, "correct horse", "Anna"), SyncClient.Json))
            .Content.ReadFromJsonAsync<CredentialResponse>(SyncClient.Json);
        var secondPc = await (await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.ToUpperInvariant(), "correct horse"), SyncClient.Json))
            .Content.ReadFromJsonAsync<CredentialResponse>(SyncClient.Json);
        var wrong = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, "wrong password"), SyncClient.Json);
        var unknown = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(NewUser(), "whatever123"), SyncClient.Json);
        var taken = await http.PostAsJsonAsync("/api/auth/accounts", new AccountRegisterRequest(user.ToUpperInvariant(), "another pass", "X"), SyncClient.Json);
        var weak = await http.PostAsJsonAsync("/api/auth/accounts", new AccountRegisterRequest(NewUser(), "short", "X"), SyncClient.Json);

        Assert.Equal(created!.PlayerId, secondPc!.PlayerId);
        Assert.NotEqual(created.Secret, secondPc.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(ErrorCodes.WeakPassword, (await weak.Content.ReadFromJsonAsync<ErrorDto>(SyncClient.Json))!.Code);

        foreach (var secret in new[] { created.Secret, secondPc.Secret })
        {
            var token = await http.PostAsJsonAsync("/api/auth/token", new TokenRequest(created.PlayerId, secret), SyncClient.Json);
            Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Guest_can_claim_an_account_and_keep_their_party()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var guest = await fx.RegisterAsync("Guest");
        var party = await guest.PostAsync<PartyStateDto>("/api/parties", new CreatePartyRequest("Mine", 2));
        var user = NewUser();

        var claimed = await guest.PostAsync<AccountDto>("/api/me/claim", new ClaimAccountRequest(user, "long enough"));
        var again = await guest.PostRawAsync("/api/me/claim", new ClaimAccountRequest(NewUser(), "long enough"));

        Assert.False(claimed.IsGuest);
        Assert.Equal(party.PartyId, claimed.PartyId);
        Assert.Equal(ErrorCodes.AlreadyAccount, again.Error!.Code);
        using var http = fx.CreateClient();
        var login = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, "long enough"), SyncClient.Json);
        Assert.Equal(guest.Id, (await login.Content.ReadFromJsonAsync<CredentialResponse>(SyncClient.Json))!.PlayerId);
    }

    [SkippableFact]
    public async Task Private_data_requires_authentication()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        using var anon = fx.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/parties/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/parties/current/leaderboard?period=Lifetime")).StatusCode);
    }

    [SkippableFact]
    public async Task Admin_endpoints_need_the_key_and_report_online_rooms()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var owner = await fx.RegisterAsync("Admin test");
        await owner.PostAsync<PartyStateDto>("/api/parties", new CreatePartyRequest("Room", 4));
        await owner.HubAsync();

        using var noKey = fx.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await noKey.GetAsync("/api/admin/stats")).StatusCode);
        // A player token is not an admin credential.
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.Http.GetAsync("/api/admin/stats")).StatusCode);

        using var admin = fx.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ServerFixture.AdminKey);
        var stats = await admin.GetFromJsonAsync<AdminStatsDto>("/api/admin/stats", SyncClient.Json);
        var rooms = await admin.GetFromJsonAsync<List<AdminPartyDto>>("/api/admin/parties", SyncClient.Json);

        Assert.True(stats!.OnlinePlayers >= 1);
        Assert.True(stats.ActiveParties >= 1);
        Assert.Contains(rooms!, r => r.Name == "Room" && r.OnlineMembers == 1);
    }

    [SkippableFact]
    public async Task Admin_api_does_not_exist_without_a_configured_key()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var noAdmin = fx.Factory.WithWebHostBuilder(b => b.UseSetting("ChestTracker:AdminApiKey", ""));
        using var http = noAdmin.CreateClient();
        http.DefaultRequestHeaders.Add("X-Admin-Key", "");

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/admin/stats")).StatusCode);
    }

    [SkippableFact]
    public async Task Hub_calls_are_rate_limited_per_player()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var strict = fx.Factory.WithWebHostBuilder(b => b.UseSetting("ChestTracker:HubCallsPerSecond", "3"));
        using var http = strict.CreateClient();
        http.DefaultRequestHeaders.Add(ProtocolInfo.Header, ProtocolInfo.Version.ToString());
        var reg = await (await http.PostAsJsonAsync("/api/auth/register", new RegisterRequest("Spammer"), SyncClient.Json))
            .Content.ReadFromJsonAsync<CredentialResponse>(SyncClient.Json);
        var token = await (await http.PostAsJsonAsync("/api/auth/token", new TokenRequest(reg!.PlayerId, reg.Secret), SyncClient.Json))
            .Content.ReadFromJsonAsync<TokenResponse>(SyncClient.Json);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(strict.Server.BaseAddress, Routes.PartyHub), o =>
            {
                o.HttpMessageHandlerFactory = _ => strict.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token!.AccessToken);
                o.Headers[ProtocolInfo.Header] = ProtocolInfo.Version.ToString();
            })
            .Build();
        await hub.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(async () =>
        {
            for (var i = 0; i < 20; i++) await hub.InvokeAsync<PingResponse>(HubMethods.Ping);
        });
        Assert.Contains(ErrorCodes.RateLimited, ex.Message);
    }

    [SkippableFact]
    public async Task Backup_restores_into_an_empty_database()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, mate) = (await fx.RegisterAsync("B-owner"), await fx.RegisterAsync("B-mate"));
        var party = await owner.PostAsync<PartyStateDto>("/api/parties", new CreatePartyRequest("Backup", 4));
        await mate.PostAsync<PartyStateDto>("/api/parties/join", new JoinPartyRequest(party.InviteCode!));
        await owner.SubmitAsync(Ev.Add(DateTimeOffset.UtcNow.AddSeconds(-30)), Ev.Add(DateTimeOffset.UtcNow.AddSeconds(-20)));

        using var admin = fx.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", ServerFixture.AdminKey);
        var backup = await (await admin.PostAsync("/api/admin/backups", null)).Content.ReadFromJsonAsync<BackupInfoDto>(SyncClient.Json);
        var file = Path.Combine(fx.BackupDirectory, backup!.FileName);
        Assert.True(File.Exists(file));

        // Restore into a brand-new database and compare.
        var target = "ct_restore_" + Guid.NewGuid().ToString("N")[..10];
        await using (var c = new NpgsqlConnection(fx.AdminConnection))
        {
            await c.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {target}", c).ExecuteNonQueryAsync();
        }

        try
        {
            await using var source = NpgsqlDataSource.Create(fx.ConnectionString(target));
            await new SchemaMigrator(source, NullLogger<SchemaMigrator>.Instance).MigrateAsync();
            var service = new BackupService(source, Options.Create(new ServerOptions()), TimeProvider.System, NullLogger<BackupService>.Instance);

            await service.RestoreAsync(file, replace: false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(file, replace: false));

            await using var check = await source.OpenConnectionAsync();
            var restoredParty = (long)(await new NpgsqlCommand($"SELECT COUNT(*) FROM party_members WHERE party_id = '{party.PartyId}'", check).ExecuteScalarAsync())!;
            var restoredEvents = (long)(await new NpgsqlCommand($"SELECT COUNT(*) FROM chest_events WHERE party_id = '{party.PartyId}'", check).ExecuteScalarAsync())!;
            var devices = (long)(await new NpgsqlCommand($"SELECT COUNT(*) FROM device_credentials WHERE player_id = '{owner.Id}'", check).ExecuteScalarAsync())!;
            Assert.Equal(2, restoredParty);
            Assert.Equal(2, restoredEvents);
            Assert.Equal(1, devices);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var c = new NpgsqlConnection(fx.AdminConnection);
            await c.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS {target} WITH (FORCE)", c).ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public void Password_hashes_verify_and_are_salted()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var a = AuthService.HashPassword("hunter2hunter2");
        var b = AuthService.HashPassword("hunter2hunter2");

        Assert.NotEqual(a, b);
        Assert.True(AuthService.VerifyPassword("hunter2hunter2", a));
        Assert.False(AuthService.VerifyPassword("hunter2hunter3", a));
        Assert.DoesNotContain("hunter2", a);
    }
}

[Collection("server")]
public class AccountSyncTests(ServerFixture fx)
{
    [SkippableFact]
    public async Task Same_account_on_two_computers_shares_identity_and_measures_ping()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var pc1 = new TestDevice(fx);
        await using var pc2 = new TestDevice(fx);
        pc1.Client.Start();
        pc2.Client.Start();
        var user = "acc" + Guid.NewGuid().ToString("N")[..8];

        await pc1.Client.RegisterAccountAsync("http://localhost/", user, "a long password", "Anna");
        await pc2.Client.LoginAsync("http://localhost/", user, "a long password");
        await Wait.UntilAsync(() => pc1.Client.Status == SyncStatus.Online && pc2.Client.Status == SyncStatus.Online, "both online");

        Assert.Equal(pc1.Client.Account!.PlayerId, pc2.Client.Account!.PlayerId);
        Assert.False(pc2.Client.Account.IsGuest);
        Assert.Equal("Anna", pc2.Client.Account.DisplayName);
        Assert.NotNull(pc1.Client.LatencyMs);

        await pc1.Client.DisconnectAsync();
        Assert.Equal(SyncStatus.Disconnected, pc1.Client.Status);
        pc1.Engine.AddChestManual(fromHotkey: false); // recorded while disconnected
        pc1.Client.Connect();
        await Wait.UntilAsync(() => pc1.Client.Status == SyncStatus.Online, "reconnected");
    }

    [SkippableFact]
    public async Task Wrong_password_and_unreachable_server_give_clear_errors()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var pc = new TestDevice(fx);
        pc.Client.Start();

        var wrong = await Assert.ThrowsAsync<SyncApiException>(() => pc.Client.LoginAsync("http://localhost/", "nobody_here", "wrong password"));
        Assert.Equal(ErrorCodes.InvalidCredentials, wrong.Code);

        // A real network client against a closed port.
        var engineOnly = new SyncClient(pc.Engine, pc.Outbox, new InMemoryAccountStore());
        var unreachable = await Assert.ThrowsAsync<SyncApiException>(() => engineOnly.CheckServerAsync("http://127.0.0.1:9"));
        Assert.Equal("unreachable", unreachable.Code);
        await engineOnly.DisposeAsync();
    }
}
