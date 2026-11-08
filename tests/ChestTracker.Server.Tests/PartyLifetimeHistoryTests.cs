using ChestTracker.Protocol;
using ChestTracker.Server.Data;
using ChestTracker.Sync;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ChestTracker.Server.Tests;

/// <summary>
/// Party lifetime = everything accepted for the party during its members' membership periods, kept after they leave.
/// </summary>
[Collection("server")]
public class PartyLifetimeHistoryTests(ServerFixture fx)
{
    private const string Url = "http://localhost/";

    private static CountPair P(int both) => new(both, both);

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private static MemberDto Member(SyncClient viewer, Guid player) => viewer.Party!.Members.Single(m => m.PlayerId == player);

    private static async Task<TestDevice> DeviceAsync(ServerFixture fx, string name)
    {
        var d = new TestDevice(fx);
        d.Client.Start();
        await d.Client.RegisterAsync(Url, name);
        await Wait.UntilAsync(() => d.Client.Status == SyncStatus.Online, name + " online");
        return d;
    }

    private static void Chests(TestDevice d, int n, double step = 3)
    {
        for (var i = 0; i < n; i++)
        {
            d.Clock.Step(step);
            if (d.Engine.CurrentMatch is null) d.Engine.StartMatch();
            Assert.True(d.Engine.AddChestManual(fromHotkey: true).Applied);
        }
    }

    private async Task<(TestPlayer Owner, PartyStateDto State)> PartyAsync(string name)
    {
        var owner = await fx.RegisterAsync(name + "-owner");
        var state = await owner.PostAsync<PartyStateDto>("/api/parties", new CreatePartyRequest(name, 4));
        return (owner, state);
    }

    private async Task<TestPlayer> JoinAsync(string inviteCode, string name)
    {
        var p = await fx.RegisterAsync(name);
        await p.PostAsync<PartyStateDto>("/api/parties/join", new JoinPartyRequest(inviteCode));
        return p;
    }

    private static ChestEventDto For(ChestEventDto e, Guid party) => e with { PartyId = party };

    /// <summary>Spaced adds (verified), timestamps from <paramref name="from"/>.</summary>
    private static ChestEventDto[] Adds(int n, DateTimeOffset from) =>
        Enumerable.Range(0, n).Select(i => Ev.Add(from.AddSeconds(2 * i))).ToArray();

    // ------------------------------------------------------------------ the example scenario (devices)

    [SkippableFact]
    public async Task Example_scenario_keeps_former_members_contributions_through_rejoin_and_restart()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var a = await DeviceAsync(fx, "Anna");
        await using var b = await DeviceAsync(fx, "Boris");
        var party = await a.Client.CreatePartyAsync("History", 4);
        await b.Client.JoinPartyAsync(party.InviteCode!);
        await Wait.UntilAsync(() => a.Client.Party?.Members.Count == 2, "B joined");

        Chests(a, 10);
        await Wait.UntilAsync(() => a.Client.Party!.TeamLifetime == P(10), "A opens 10 → 10");

        Chests(b, 20);
        await Wait.UntilAsync(() => a.Client.Party!.TeamLifetime == P(30) && b.Client.Party!.TeamLifetime == P(30), "B opens 20 → 30");

        await b.Client.LeavePartyAsync();
        await Wait.UntilAsync(() => a.Client.Party!.Members.Count == 1, "B left");
        Assert.Equal(P(30), a.Client.Party!.TeamLifetime);                       // B leaves → 30

        Chests(a, 5);
        await Wait.UntilAsync(() => a.Client.Party!.TeamLifetime == P(35), "A opens 5 → 35");

        Chests(b, 10);                                                           // B outside the party
        await Task.Delay(500);
        Assert.Equal(P(35), a.Client.Party!.TeamLifetime);
        Assert.Equal(30, b.Engine.GetSnapshot().LifetimeCount);                 // personal lifetime unaffected

        await b.Client.JoinPartyAsync(a.Client.Party!.InviteCode!);
        await Wait.UntilAsync(() => a.Client.Party!.Members.Count == 2, "B rejoined");
        Assert.Equal(P(35), a.Client.Party!.TeamLifetime);                       // rejoin → 35, no duplicates
        Assert.Equal(P(20), Member(a.Client, b.Client.Account!.PlayerId).Lifetime);

        Chests(b, 5);
        await Wait.UntilAsync(() => a.Client.Party!.TeamLifetime == P(40) && b.Client.Party!.TeamLifetime == P(40), "B opens 5 → 40");

        await b.Client.LeavePartyAsync();
        await Wait.UntilAsync(() => a.Client.Party!.Members.Count == 1, "B left again");
        Assert.Equal(P(40), a.Client.Party!.TeamLifetime);

        await fx.RestartAsync();
        await Wait.UntilAsync(() => a.Client.Status == SyncStatus.Online && a.Client.Party is not null, "A reconnected", 30000);
        Assert.Equal(P(40), a.Client.Party!.TeamLifetime);                       // restart → 40
        Assert.Equal(35, b.Engine.GetSnapshot().LifetimeCount);
    }

    // ------------------------------------------------------------------ leave / kick / rejoin (raw events)

    [SkippableFact]
    public async Task Contributions_stay_after_leaving_and_after_being_kicked()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await PartyAsync("LeaveKick");
        var leaver = await JoinAsync(state.InviteCode!, "Leaver");
        var kicked = await JoinAsync(state.InviteCode!, "Kicked");

        await owner.SubmitAsync(Adds(1, Now));
        await leaver.SubmitAsync(Adds(2, Now));
        await kicked.SubmitAsync(Adds(3, Now));
        Assert.Equal(P(6), (await owner.StateAsync()).TeamLifetime);

        await leaver.PostAsync<object>("/api/parties/current/leave");
        Assert.Equal(P(6), (await owner.StateAsync()).TeamLifetime);

        var kick = await owner.PostRawAsync($"/api/parties/current/kick/{kicked.Id}");
        Assert.Equal(200, kick.Status);
        var after = await owner.StateAsync();
        Assert.Single(after.Members);
        Assert.Equal(P(6), after.TeamLifetime);
        Assert.Equal(P(1), after.Members.Single().Lifetime); // the current member's own counter is still personal
    }

    [SkippableFact]
    public async Task Chests_after_leaving_or_before_joining_never_count_and_rejoining_does_not_duplicate()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await PartyAsync("Boundaries");
        var mate = await JoinAsync(state.InviteCode!, "Mate");
        var during = Adds(2, Now);
        await mate.SubmitAsync(during);
        await mate.PostAsync<object>("/api/parties/current/leave");

        // After leaving: claimed for the old party, or with no party at all → rejected.
        var outside = await mate.SubmitAsync(For(Ev.Add(Now.AddSeconds(30)), state.PartyId), Ev.Add(Now.AddSeconds(35)));
        Assert.All(outside, ack => Assert.Equal(AckStatus.Rejected, ack.Status));
        Assert.All(outside, ack => Assert.Equal(ErrorCodes.NotMember, ack.Reason));

        // Rejoin: the old chests are not brought in again, a re-sent old event is a duplicate.
        await mate.PostAsync<PartyStateDto>("/api/parties/join", new JoinPartyRequest(state.InviteCode!));
        Assert.Equal(P(2), (await owner.StateAsync()).TeamLifetime);
        var resent = await mate.SubmitAsync(during);
        Assert.All(resent, ack => Assert.Equal(AckStatus.Duplicate, ack.Status));

        // Chests dated between the two memberships still do not count.
        var gap = await mate.SubmitAsync(Ev.Add(Now.AddSeconds(-1).AddMinutes(-0.5)));
        Assert.Equal("outside_membership", gap[0].Reason);

        await mate.SubmitAsync(Adds(3, Now.AddSeconds(10)));  // ≥ 1 s from earlier adds, so all verified
        Assert.Equal(P(5), (await owner.StateAsync()).TeamLifetime);

        // Joining another party does not bring chests from the first one.
        var (other, otherState) = await PartyAsync("Other");
        await mate.PostAsync<PartyStateDto>("/api/parties/join", new JoinPartyRequest(otherState.InviteCode!));
        var before = await mate.SubmitAsync(Ev.Add(Now.AddMinutes(-1)));
        Assert.Equal("outside_membership", before[0].Reason);
        Assert.Equal(P(0), (await other.StateAsync()).TeamLifetime);
        Assert.Equal(P(5), (await owner.StateAsync()).TeamLifetime);
    }

    [SkippableFact]
    public async Task Several_members_leave_and_rejoin_without_changing_or_duplicating_totals()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await PartyAsync("Churn");
        var m1 = await JoinAsync(state.InviteCode!, "M1");
        var m2 = await JoinAsync(state.InviteCode!, "M2");
        await owner.SubmitAsync(Adds(2, Now));
        await m1.SubmitAsync(Adds(3, Now));
        await m2.SubmitAsync(Adds(4, Now));
        Assert.Equal(P(9), (await owner.StateAsync()).TeamLifetime);

        for (var round = 0; round < 2; round++)
        {
            await m1.PostAsync<object>("/api/parties/current/leave");
            Assert.Equal(200, (await owner.PostRawAsync($"/api/parties/current/kick/{m2.Id}")).Status);
            Assert.Equal(P(9 + (round * 2)), (await owner.StateAsync()).TeamLifetime);
            await m1.PostAsync<PartyStateDto>("/api/parties/join", new JoinPartyRequest(state.InviteCode!));
            await m2.PostAsync<PartyStateDto>("/api/parties/join", new JoinPartyRequest(state.InviteCode!));
            Assert.Equal(P(9 + (round * 2)), (await owner.StateAsync()).TeamLifetime);
            await m1.SubmitAsync(Ev.Add(Now.AddSeconds(10 + (round * 5))));
            await m2.SubmitAsync(Ev.Add(Now.AddSeconds(10 + (round * 5))));
        }

        Assert.Equal(P(13), (await owner.StateAsync()).TeamLifetime);
        await using var c = new NpgsqlConnection(fx.DatabaseConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM party_membership_periods WHERE party_id = @p AND player_id = @x", c);
        cmd.Parameters.AddWithValue("p", state.PartyId);
        cmd.Parameters.AddWithValue("x", m1.Id);
        Assert.Equal(3L, (long)(await cmd.ExecuteScalarAsync())!); // three separate membership periods
    }

    [SkippableFact]
    public async Task Reported_verified_and_historical_undo_follow_the_membership()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await PartyAsync("Counts");
        var mate = await JoinAsync(state.InviteCode!, "Counter");
        var t = Now;
        var a1 = Ev.Add(t);
        var a2 = Ev.Add(t.AddMilliseconds(300));   // too fast: reported only
        var a3 = Ev.Add(t.AddSeconds(3));
        var corr = Ev.Correction(+4, t.AddSeconds(4)); // manual: reported only
        await mate.SubmitAsync(a1, a2, a3, corr);
        Assert.Equal(new CountPair(2, 7), (await owner.StateAsync()).TeamLifetime);

        // An undo recorded during the membership (t+5 s) arrives after leaving: still applied to the party.
        var undoDuring = For(Ev.Undo(a3, t.AddSeconds(5)), state.PartyId);
        await mate.PostAsync<object>("/api/parties/current/leave");
        var late = await mate.SubmitAsync(undoDuring);
        Assert.Equal(AckStatus.Accepted, late[0].Status);
        Assert.Equal(new CountPair(1, 6), (await owner.StateAsync()).TeamLifetime);

        // An undo recorded after leaving never changes the former party.
        var undoAfter = For(Ev.Undo(a1, Now.AddSeconds(30)), state.PartyId);
        var refused = await mate.SubmitAsync(undoAfter);
        Assert.Equal(AckStatus.Rejected, refused[0].Status);
        Assert.Equal(new CountPair(1, 6), (await owner.StateAsync()).TeamLifetime);

        // Duplicates of everything: nothing changes.
        var dupes = await mate.SubmitAsync(a1, a2, a3, corr, undoDuring);
        Assert.All(dupes, ack => Assert.Equal(AckStatus.Duplicate, ack.Status));
        Assert.Equal(new CountPair(1, 6), (await owner.StateAsync()).TeamLifetime);
    }

    // ------------------------------------------------------------------ offline delivery after removal (devices)

    [SkippableFact]
    public async Task Offline_chests_from_the_membership_are_delivered_after_a_kick_and_later_ones_are_refused()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var a = await DeviceAsync(fx, "Owner");
        await using var b = await DeviceAsync(fx, "Offline");
        var party = await a.Client.CreatePartyAsync("Offline", 4);
        await b.Client.JoinPartyAsync(party.InviteCode!);
        await Wait.UntilAsync(() => a.Client.Party?.Members.Count == 2, "B joined");
        var boris = b.Client.Account!.PlayerId;

        Chests(b, 2, step: 1.5);
        await Wait.UntilAsync(() => a.Client.Party!.TeamLifetime == P(2), "online chests");

        await b.GoOfflineAsync();
        Chests(b, 3, step: 1.5);           // recorded during the membership, not sent yet
        await Task.Delay(TimeSpan.FromSeconds(9)); // the kick happens after these timestamps
        await a.Client.KickAsync(boris);
        await Wait.UntilAsync(() => a.Client.Party!.Members.Count == 1, "B kicked");
        b.Clock.Step(30);                  // well past the kick (and its clock-skew allowance)
        Chests(b, 2, step: 1.5);           // after the kick
        Assert.Equal(P(2), a.Client.Party!.TeamLifetime);

        b.ComeBackOnline();
        await Wait.UntilAsync(() => b.Client.Status == SyncStatus.Online && b.Client.PendingCount == 0, "B delivered its queue");
        await Wait.UntilAsync(() => a.Client.Party!.TeamLifetime == P(5), "3 membership chests counted (now " + a.Client.Party!.TeamLifetime + ")");
        await Task.Delay(500);
        Assert.Equal(P(5), a.Client.Party!.TeamLifetime);
        Assert.Null(b.Client.Party);
        Assert.Equal(7, b.Engine.GetSnapshot().LifetimeCount); // personal stats keep everything
    }

    // ------------------------------------------------------------------ current match rules unchanged

    [SkippableFact]
    public async Task Current_match_totals_still_follow_shared_match_participation()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await PartyAsync("Match");
        var mate = await JoinAsync(state.InviteCode!, "Player");
        await owner.SubmitAsync(Adds(5, Now));          // before the match: lifetime only

        var ownerHub = await owner.HubAsync();
        var mateHub = await mate.HubAsync();
        var match = await ownerHub.InvokeAsync<SharedMatchDto>(HubMethods.StartSharedMatch);
        await mateHub.InvokeAsync(HubMethods.RespondSharedMatch, match.Id, true);
        await owner.SubmitAsync(Ev.Add(Now.AddSeconds(20), match.Id));
        await mate.SubmitAsync(Ev.Add(Now.AddSeconds(1), match.Id), Ev.Add(Now.AddSeconds(3), match.Id));

        await mate.PostAsync<object>("/api/parties/current/leave");
        var s = await owner.StateAsync();
        Assert.Equal(P(3), s.TeamMatch);                // participants, including the one who left
        Assert.Equal(P(8), s.TeamLifetime);             // 5 + 1 + 2
    }

    // ------------------------------------------------------------------ migration

    [SkippableFact]
    public async Task Migration_backfills_periods_from_existing_members_and_keeps_every_event()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var database = "ct_mig_" + Guid.NewGuid().ToString("N")[..10];
        await using (var admin = new NpgsqlConnection(fx.AdminConnection))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {database}", admin).ExecuteNonQueryAsync();
        }

        var cs = fx.ConnectionString(database);
        try
        {
            await using var c = new NpgsqlConnection(cs);
            await c.OpenAsync();
            // Schema as installed by the previous version (migrations 1 and 2), with data.
            await new NpgsqlCommand("CREATE TABLE schema_migrations (version int PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", c).ExecuteNonQueryAsync();
            foreach (var (version, sql) in SchemaMigrator.Migrations.Where(m => m.Version <= 2))
            {
                await new NpgsqlCommand(sql, c).ExecuteNonQueryAsync();
                await new NpgsqlCommand($"INSERT INTO schema_migrations(version) VALUES ({version})", c).ExecuteNonQueryAsync();
            }

            var (owner, current, former, party) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await new NpgsqlCommand($"""
                INSERT INTO players (id, display_name, created_at) VALUES
                  ('{owner}', 'O', now() - interval '3 days'), ('{current}', 'C', now() - interval '3 days'), ('{former}', 'F', now() - interval '3 days');
                INSERT INTO parties (id, name, owner_id, max_players, created_at) VALUES ('{party}', 'Old', '{owner}', 4, now() - interval '3 days');
                INSERT INTO party_members (party_id, player_id, role, joined_at, left_at) VALUES
                  ('{party}', '{owner}', 1, now() - interval '3 days', NULL),
                  ('{party}', '{current}', 0, now() - interval '2 days', NULL),
                  ('{party}', '{former}', 0, now() - interval '2 days', now() - interval '1 day');
                INSERT INTO chest_events (event_id, player_id, party_id, local_match_id, kind, source, delta, client_ts, server_ts, verified)
                  SELECT gen_random_uuid(), p, '{party}', gen_random_uuid(), 0, 0, 1, now() - interval '36 hours', now(), true
                  FROM (VALUES ('{owner}'::uuid), ('{current}'::uuid), ('{former}'::uuid), ('{former}'::uuid)) v(p);
                """, c).ExecuteNonQueryAsync();

            await using var source = NpgsqlDataSource.Create(cs);
            await new SchemaMigrator(source, NullLogger<SchemaMigrator>.Instance).MigrateAsync();

            await using var periods = new NpgsqlCommand($"""
                SELECT COUNT(*) FILTER (WHERE ended_at IS NULL), COUNT(*) FILTER (WHERE ended_at IS NOT NULL AND end_reason = 'unknown'),
                       BOOL_AND(reconstructed), (SELECT COUNT(*) FROM chest_events)
                FROM party_membership_periods WHERE party_id = '{party}'
                """, c);
            await using var r = await periods.ExecuteReaderAsync();
            await r.ReadAsync();
            Assert.Equal(2L, r.GetInt64(0));  // owner + current member: open periods
            Assert.Equal(1L, r.GetInt64(1));  // former member: closed period, reason unknown
            Assert.True(r.GetBoolean(2));     // marked as reconstructed
            Assert.Equal(4L, r.GetInt64(3));  // no event touched or duplicated
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(fx.AdminConnection);
            await admin.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin).ExecuteNonQueryAsync();
        }
    }
}
