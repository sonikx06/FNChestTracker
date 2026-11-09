using ChestTracker.Core.Tracking;
using ChestTracker.Protocol;
using ChestTracker.Sync;
using Npgsql;

namespace ChestTracker.Server.Tests;

/// <summary>
/// Statistics audit: real desktop clients (SQLite + engine + SyncClient) against the real server and PostgreSQL.
/// "Personal" = the player's own count in the party (MemberDto), "Party match" = PartyStateDto.TeamMatch.
/// Verified and reported counts are always asserted separately.
/// </summary>
[Collection("server")]
public class StatisticsAuditTests(ServerFixture fx)
{
    private const string Url = "http://localhost/";

    private static MemberDto Member(SyncClient viewer, Guid player) => viewer.Party!.Members.Single(m => m.PlayerId == player);

    private static CountPair P(int both) => new(both, both);

    private async Task<(TestDevice A, TestDevice B)> PartyOfTwoAsync()
    {
        var a = new TestDevice(fx);
        var b = new TestDevice(fx);
        a.Client.Start();
        b.Client.Start();
        await a.Client.RegisterAsync(Url, "Anna");
        await b.Client.RegisterAsync(Url, "Boris");
        await Wait.UntilAsync(() => a.Client.Status == SyncStatus.Online && b.Client.Status == SyncStatus.Online, "both online");
        var party = await a.Client.CreatePartyAsync("Audit", 4);
        await b.Client.JoinPartyAsync(party.InviteCode!);
        await Wait.UntilAsync(() => a.Client.Party?.Members.Count == 2 && b.Client.Party?.Members.Count == 2, "both see two members");
        return (a, b);
    }

    /// <summary>Owner starts a local match → shared match; B auto-joins and links its own local match.</summary>
    private static async Task StartSharedAsync(TestDevice a, TestDevice b)
    {
        a.Engine.StartMatch();
        await Wait.UntilAsync(() => a.Client.Party?.ActiveMatch is not null
                                    && b.Client.Party?.ActiveMatch is { } m
                                    && Member(a.Client, b.Client.Account!.PlayerId).MatchResponse == ParticipantResponse.Accepted
                                    && b.Engine.CurrentMatch is { } local && b.Outbox.GetSharedMatch(local.Id) == m.Id,
            "shared match started and joined");
    }

    private static Task TeamMatchIsAsync(TestDevice a, TestDevice b, CountPair expected, string what) =>
        Wait.UntilAsync(() => a.Client.Party?.TeamMatch == expected && b.Client.Party?.TeamMatch == expected, what);

    /// <summary>Authoritative totals straight from PostgreSQL.</summary>
    private async Task<CountPair> DbTotalsAsync(Guid partyId, Guid? player = null, Guid? sharedMatch = null)
    {
        await using var c = new NpgsqlConnection(fx.DatabaseConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT COALESCE(SUM(CASE WHEN verified AND kind IN (0, 1) THEN delta END), 0)::int, COALESCE(SUM(delta), 0)::int
            FROM chest_events
            WHERE party_id = @p AND (@x::uuid IS NULL OR player_id = @x::uuid) AND (@m::uuid IS NULL OR shared_match_id = @m::uuid)
            """, c);
        cmd.Parameters.AddWithValue("p", partyId);
        cmd.Parameters.AddWithValue("x", (object?)player ?? DBNull.Value);
        cmd.Parameters.AddWithValue("m", (object?)sharedMatch ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return new CountPair(r.GetInt32(0), r.GetInt32(1));
    }

    private static void ResetSyncCursor(TestDevice d) => d.Database.Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE app_meta SET value = '0' WHERE key = 'sync.cursor'";
        return cmd.ExecuteNonQuery();
    });

    [SkippableFact]
    public async Task Required_scenario_personal_and_party_totals_through_offline_duplicates_end_and_restart()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        var anna = a.Client.Account!.PlayerId;
        var boris = b.Client.Account!.PlayerId;
        var partyId = a.Client.Party!.PartyId;

        // Initial state: 0 / 0 / 0
        await StartSharedAsync(a, b);
        var shared = a.Client.Party!.ActiveMatch!.Id;
        Assert.Equal(P(0), a.Client.Party.TeamMatch);

        // A opens 5 chests: 5 / 0 / 5
        for (var i = 0; i < 5; i++) a.OpenChest();
        await TeamMatchIsAsync(a, b, P(5), "A's 5 chests");
        Assert.Equal(P(5), Member(b.Client, anna).Match);
        Assert.Equal(P(0), Member(a.Client, boris).Match);

        // B opens 8 chests: 5 / 8 / 13
        for (var i = 0; i < 8; i++) b.OpenChest();
        await TeamMatchIsAsync(a, b, P(13), "B's 8 chests");
        Assert.Equal(P(8), Member(a.Client, boris).Match);

        // A undoes 1 chest: 4 / 8 / 12
        a.Clock.Step(2);
        Assert.True(a.Engine.RemoveLastChest(fromHotkey: false).Applied);
        await TeamMatchIsAsync(a, b, P(12), "A's undo");
        Assert.Equal(P(4), Member(b.Client, anna).Match);

        // B disconnects and opens 3 chests: 4 / 11 (locally) / 12 until synchronization
        await b.GoOfflineAsync();
        for (var i = 0; i < 3; i++) b.OpenChest();
        Assert.Equal(11, b.Engine.GetSnapshot().MatchCount);
        Assert.Equal(11, b.Engine.GetSnapshot().LifetimeCount); // local personal stats keep working offline
        await Wait.UntilAsync(() => !Member(a.Client, boris).Online, "A sees B offline");
        await Task.Delay(500);
        Assert.Equal(P(12), a.Client.Party!.TeamMatch);
        Assert.Equal(P(12), await DbTotalsAsync(partyId, sharedMatch: shared));

        // B reconnects and synchronizes: 4 / 11 / 15
        b.ComeBackOnline();
        await TeamMatchIsAsync(a, b, P(15), "B's offline chests delivered");
        Assert.Equal(P(11), Member(a.Client, boris).Match);
        Assert.Equal(0, b.Client.PendingCount);

        // Duplicate events are re-sent: 4 / 11 / 15
        ResetSyncCursor(b);
        await b.Client.FlushAsync();
        Assert.Equal(0, b.Client.PendingCount);
        Assert.Equal(P(15), await DbTotalsAsync(partyId, sharedMatch: shared));
        Assert.Equal(P(11), await DbTotalsAsync(partyId, boris));

        // Both players stop counting, the shared match ends: 4 / 11 / 15, and the result is kept in history
        a.Clock.Step(2);
        a.Engine.EndMatch();
        b.Engine.EndMatch();
        await Wait.UntilAsync(() => a.Client.Party?.ActiveMatch is null && b.Client.Party?.ActiveMatch is null && b.Engine.CurrentMatch is null,
            "match ended for both");
        var history = (await a.Client.GetHistoryAsync()).Single(h => h.Id == shared);
        Assert.Equal(P(15), history.Team);
        Assert.Equal(P(4), history.Participants.Single(p => p.PlayerId == anna).Counts);
        Assert.Equal(P(11), history.Participants.Single(p => p.PlayerId == boris).Counts);
        Assert.Equal(P(15), a.Client.Party!.TeamLifetime);

        // Server restarts: 4 / 11 / 15
        await fx.RestartAsync();
        await Wait.UntilAsync(() => a.Client.Status == SyncStatus.Online && b.Client.Status == SyncStatus.Online
                                    && a.Client.Party is not null && b.Client.Party is not null, "both reconnected after restart", 30000);
        var afterRestart = (await b.Client.GetHistoryAsync()).Single(h => h.Id == shared);
        Assert.Equal(P(15), afterRestart.Team);
        Assert.Equal(P(15), b.Client.Party!.TeamLifetime);
        Assert.Equal(P(4), Member(b.Client, anna).Lifetime);
        Assert.Equal(P(11), Member(a.Client, boris).Lifetime);

        // Local SQLite and server agree for each player.
        Assert.Equal(4, a.Engine.GetSnapshot().LifetimeCount);
        Assert.Equal(11, b.Engine.GetSnapshot().LifetimeCount);
    }

    [SkippableFact]
    public async Task Reported_and_verified_stay_separate_for_fast_presses_undo_and_corrections()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        var boris = b.Client.Account!.PlayerId;
        await StartSharedAsync(a, b);

        // 3 presses 0.3 s apart: only the first passes the 1 s plausibility rule.
        for (var i = 0; i < 3; i++)
        {
            b.Clock.Step(0.3);
            b.Engine.AddChestManual(fromHotkey: false);
        }

        await TeamMatchIsAsync(a, b, new CountPair(1, 3), "fast presses");

        // Undo of an unverified add changes reported only.
        b.Clock.Step(2);
        b.Engine.RemoveLastChest(fromHotkey: false);
        await TeamMatchIsAsync(a, b, new CountPair(1, 2), "undo of unverified add");

        // A manual correction (+5) is reported, never verified.
        b.Clock.Step(2);
        b.Engine.SetCurrentMatchCount(7);
        await TeamMatchIsAsync(a, b, new CountPair(1, 7), "manual correction");
        Assert.Equal(new CountPair(1, 7), Member(a.Client, boris).Match);
        Assert.Equal(7, b.Engine.GetSnapshot().MatchCount); // local count shows what the player set

        // Leaderboard labels both numbers.
        var board = await a.Client.GetLeaderboardAsync(LeaderboardPeriod.Lifetime);
        var row = board.Single(r => r.PlayerId == boris);
        Assert.Equal((1, 7), (row.Verified, row.Reported));
    }

    [SkippableFact]
    public async Task Chests_without_a_running_match_are_refused_and_the_first_chest_after_start_counts_in_the_shared_match()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;

        // Counting is stopped: a chest press neither counts nor starts a match (local or shared).
        a.Clock.Step(3);
        Assert.Equal(TrackingResultCode.NoActiveMatch, a.Engine.AddChestManual(fromHotkey: true).Code);
        await Task.Delay(300);
        Assert.Null(a.Client.Party?.ActiveMatch);

        // Match hotkey, then the first chest is attributed to the shared match.
        a.Engine.ToggleMatch();
        a.Clock.Step(3);
        Assert.True(a.Engine.AddChestManual(fromHotkey: true).Applied);
        await Wait.UntilAsync(() => a.Client.Party?.ActiveMatch is not null, "shared match started");
        await TeamMatchIsAsync(a, b, P(1), "first chest attributed to the shared match");
    }

    [SkippableFact]
    public async Task Consecutive_matches_keep_their_own_results()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;

        await StartSharedAsync(a, b);
        var first = a.Client.Party!.ActiveMatch!.Id;
        a.OpenChest();
        a.OpenChest();
        b.OpenChest();
        await TeamMatchIsAsync(a, b, P(3), "match 1");

        a.Clock.Step(2);
        a.Engine.EndMatch();   // both stop counting: match 1 is over
        b.Engine.EndMatch();
        await Wait.UntilAsync(() => a.Client.Party?.ActiveMatch is null, "match 1 ended");
        a.Engine.StartMatch(); // owner starts match 2 for the party
        await Wait.UntilAsync(() => a.Client.Party?.ActiveMatch is { } m && m.Id != first
                                    && b.Engine.CurrentMatch is { } l && b.Outbox.GetSharedMatch(l.Id) == m.Id, "match 2 joined");
        Assert.Equal(P(0), a.Client.Party!.TeamMatch);
        a.OpenChest();
        b.OpenChest();
        b.OpenChest();
        b.OpenChest();
        await TeamMatchIsAsync(a, b, P(4), "match 2");
        a.Clock.Step(2);
        a.Engine.EndMatch();
        b.Engine.EndMatch();
        await Wait.UntilAsync(() => a.Client.Party?.ActiveMatch is null, "match 2 ended");

        var history = await a.Client.GetHistoryAsync();
        Assert.Equal(P(3), history.Single(h => h.Id == first).Team);
        Assert.Equal(P(4), history.Single(h => h.Id != first).Team);
        Assert.Equal(P(7), a.Client.Party!.TeamLifetime);
    }

    [SkippableFact]
    public async Task Member_leaving_mid_match_does_not_change_the_match_result()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        var boris = b.Client.Account!.PlayerId;

        await StartSharedAsync(a, b);
        var shared = a.Client.Party!.ActiveMatch!.Id;
        a.OpenChest();
        b.OpenChest();
        b.OpenChest();
        b.OpenChest();
        await TeamMatchIsAsync(a, b, P(4), "before leaving");

        await b.Client.LeavePartyAsync();
        await Wait.UntilAsync(() => a.Client.Party?.Members.Count == 1, "B left");
        // B's chests in this match stay counted for the match, live and in history.
        Assert.Equal(P(4), a.Client.Party!.TeamMatch);
        a.OpenChest();
        await Wait.UntilAsync(() => a.Client.Party?.TeamMatch == P(5), "A keeps counting");

        // B's local stats are untouched by leaving.
        Assert.Equal(3, b.Engine.GetSnapshot().LifetimeCount);

        a.Clock.Step(2);
        a.Engine.EndMatch();
        await Wait.UntilAsync(() => a.Client.Party?.ActiveMatch is null, "ended");
        var h = (await a.Client.GetHistoryAsync()).Single(x => x.Id == shared);
        Assert.Equal(P(5), h.Team);
        Assert.Equal(P(3), h.Participants.Single(p => p.PlayerId == boris).Counts);
    }

    [SkippableFact]
    public async Task Rejoining_restores_contributions_without_duplicating_them()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        var boris = b.Client.Account!.PlayerId;
        var code = a.Client.Party!.InviteCode!;

        await StartSharedAsync(a, b);
        b.OpenChest();
        b.OpenChest();
        await Wait.UntilAsync(() => a.Client.Party?.TeamLifetime == P(2), "B's chests");

        await b.Client.LeavePartyAsync();
        await Wait.UntilAsync(() => a.Client.Party?.Members.Count == 1, "B left");
        // Party lifetime keeps former members' contributions.
        Assert.Equal(P(2), a.Client.Party!.TeamLifetime);

        await b.Client.JoinPartyAsync(code);
        await Wait.UntilAsync(() => a.Client.Party?.Members.Count == 2, "B rejoined");
        Assert.Equal(P(2), Member(a.Client, boris).Lifetime);
        Assert.Equal(P(2), a.Client.Party!.TeamLifetime);
    }

    [SkippableFact]
    public async Task Simultaneous_chests_from_both_players_are_all_counted_once()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        await StartSharedAsync(a, b);

        await Task.WhenAll(
            Task.Run(() => { for (var i = 0; i < 10; i++) a.OpenChest(); }),
            Task.Run(() => { for (var i = 0; i < 10; i++) b.OpenChest(); }));

        await TeamMatchIsAsync(a, b, P(20), "20 simultaneous chests");
        Assert.Equal(P(20), await DbTotalsAsync(a.Client.Party!.PartyId, sharedMatch: a.Client.Party.ActiveMatch!.Id));
    }

    [SkippableFact]
    public async Task Events_of_another_party_never_count_even_with_a_forged_match_id()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        await StartSharedAsync(a, b);
        var shared = a.Client.Party!.ActiveMatch!.Id;
        a.OpenChest();
        await TeamMatchIsAsync(a, b, P(1), "A's chest");

        // An outsider in their own party submits events that claim A's shared match.
        await using var outsider = await fx.RegisterAsync("Mallory");
        await outsider.PostAsync<PartyStateDto>("/api/parties", new CreatePartyRequest("Other", 2));
        var acks = await outsider.SubmitAsync(Ev.Add(DateTimeOffset.UtcNow, shared), Ev.Add(DateTimeOffset.UtcNow.AddSeconds(5), shared));
        Assert.All(acks, ack => Assert.Equal("outside_shared_match", ack.Reason));

        await Task.Delay(300);
        Assert.Equal(P(1), await DbTotalsAsync(a.Client.Party!.PartyId, sharedMatch: shared));
        Assert.Equal(P(1), a.Client.Party.TeamMatch);
        Assert.Equal(P(1), a.Client.Party.TeamLifetime);
    }
}
