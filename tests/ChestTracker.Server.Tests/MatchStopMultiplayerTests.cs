using ChestTracker.Core.Tracking;
using ChestTracker.Protocol;
using ChestTracker.Sync;

namespace ChestTracker.Server.Tests;

/// <summary>
/// One player stopping counting (match hotkey after dying) ends only that player's participation: the shared match,
/// the teammates' counting and the party statistics go on. Real clients, real server, real PostgreSQL.
/// </summary>
[Collection("server")]
public class MatchStopMultiplayerTests(ServerFixture fx)
{
    private const string Url = "http://localhost/";

    private static CountPair P(int n) => new(n, n);

    private static MemberDto Member(SyncClient viewer, Guid player) => viewer.Party!.Members.Single(m => m.PlayerId == player);

    private async Task<(TestDevice A, TestDevice B)> PartyOfTwoAsync()
    {
        var a = new TestDevice(fx);
        var b = new TestDevice(fx);
        a.Client.Start();
        b.Client.Start();
        await a.Client.RegisterAsync(Url, "Anna");
        await b.Client.RegisterAsync(Url, "Boris");
        await Wait.UntilAsync(() => a.Client.Status == SyncStatus.Online && b.Client.Status == SyncStatus.Online, "both online");
        var party = await a.Client.CreatePartyAsync("Duo", 4);
        await b.Client.JoinPartyAsync(party.InviteCode!);
        await Wait.UntilAsync(() => a.Client.Party?.Members.Count == 2 && b.Client.Party?.Members.Count == 2, "party of two");
        return (a, b);
    }

    /// <summary>Owner presses the match hotkey; Boris joins automatically.</summary>
    private static async Task<Guid> StartSharedAsync(TestDevice a, TestDevice b)
    {
        a.Engine.ToggleMatch();
        await Wait.UntilAsync(() => a.Client.Party?.ActiveMatch is { } m && b.Engine.CurrentMatch is { } l
                                    && b.Outbox.GetSharedMatch(l.Id) == m.Id && a.Outbox.GetSharedMatch(a.Engine.CurrentMatch!.Id) == m.Id,
            "shared match running for both");
        return a.Client.Party!.ActiveMatch!.Id;
    }

    [SkippableFact]
    public async Task Owner_stopping_after_death_leaves_the_teammate_counting_and_keeps_all_statistics()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        var anna = a.Client.Account!.PlayerId;
        var shared = await StartSharedAsync(a, b);

        a.OpenChest();
        a.OpenChest();
        b.OpenChest();
        await Wait.UntilAsync(() => b.Client.Party?.TeamMatch == P(3), "3 chests in the shared match");

        // Anna dies and presses the match hotkey.
        a.Clock.Step(2);
        Assert.Equal(MatchControlOutcome.Ended, a.Engine.ToggleMatch().Outcome);
        Assert.Null(a.Engine.CurrentMatch);
        Assert.Equal(TrackingResultCode.NoActiveMatch, a.Engine.AddChestManual(fromHotkey: true).Code);

        await Task.Delay(700);
        Assert.Equal(shared, b.Client.Party!.ActiveMatch?.Id);                 // 19. the shared match goes on
        Assert.NotNull(b.Engine.CurrentMatch);
        Assert.Equal(P(2), Member(b.Client, anna).Match);                       // 20. Anna's chests stay in the match
        await Wait.UntilAsync(() => Member(b.Client, anna).MatchFinished, "Boris sees that Anna stopped counting");
        Assert.False(Member(a.Client, b.Client.Account!.PlayerId).MatchFinished);
        Assert.Equal(P(3), a.Client.Party!.TeamLifetime);                       // 21. party lifetime unchanged

        b.OpenChest();
        b.OpenChest();                                                          // Boris keeps counting
        await Wait.UntilAsync(() => a.Client.Party?.TeamMatch == P(5) && a.Client.Party?.TeamLifetime == P(5),
            "22. Anna still receives the party totals");
        Assert.Equal(2, a.Engine.GetSnapshot().LifetimeCount);                  // personal statistics unchanged

        b.Clock.Step(2);
        b.Engine.ToggleMatch();                                                 // the last player stops
        await Wait.UntilAsync(() => a.Client.Party?.ActiveMatch is null && b.Client.Party?.ActiveMatch is null, "shared match over");
        var h = (await a.Client.GetHistoryAsync()).Single(x => x.Id == shared); // 23. history is right
        Assert.NotNull(h.EndedAt);
        Assert.Equal(P(5), h.Team);
        Assert.Equal(P(2), h.Participants.Single(p => p.PlayerId == anna).Counts);
        Assert.Equal(P(3), h.Participants.Single(p => p.PlayerId != anna).Counts);
    }

    [SkippableFact]
    public async Task A_new_local_match_while_teammates_still_play_does_not_touch_their_shared_match()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        var shared = await StartSharedAsync(a, b);
        a.OpenChest();
        b.OpenChest();
        await Wait.UntilAsync(() => a.Client.Party?.TeamMatch == P(2), "2 chests");

        // Anna (owner) died, stopped, and starts her next game while Boris is still in the old one.
        a.Clock.Step(2);
        a.Engine.ToggleMatch();
        a.Clock.Step(2);
        a.Engine.ToggleMatch();
        var annasNewMatch = a.Engine.CurrentMatch!.Id;
        a.OpenChest();

        await Task.Delay(1000);
        Assert.Equal(shared, b.Client.Party!.ActiveMatch?.Id);                 // 24. Boris's shared match untouched
        Assert.Equal(shared, b.Outbox.GetSharedMatch(b.Engine.CurrentMatch!.Id));
        Assert.Null(a.Outbox.GetSharedMatch(annasNewMatch));                    // Anna's new match is local
        await Wait.UntilAsync(() => a.Client.Party?.TeamLifetime == P(3), "Anna's new chest counts for the party lifetime");
        Assert.Equal(P(2), a.Client.Party!.TeamMatch);                          // but not for the shared match she left

        b.Clock.Step(2);
        b.Engine.ToggleMatch();
        await Wait.UntilAsync(() => b.Client.Party?.ActiveMatch is null, "old shared match ends with its last player");
        Assert.Equal(P(2), (await b.Client.GetHistoryAsync()).Single(x => x.Id == shared).Team);
        Assert.NotNull(a.Engine.CurrentMatch);                                  // Anna's own match keeps running
        Assert.Equal(1, a.Engine.GetSnapshot().MatchCount);
    }

    [SkippableFact]
    public async Task Stopping_while_offline_is_reported_after_reconnect()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        var shared = await StartSharedAsync(a, b);
        b.OpenChest();

        b.Clock.Step(2);
        b.Engine.ToggleMatch();                                                 // Boris stops (online)
        await Wait.UntilAsync(() => b.Outbox.PendingFinishes().Count == 0, "Boris's stop reached the server");
        Assert.Equal(shared, a.Client.Party!.ActiveMatch?.Id);                  // Anna still plays

        await a.GoOfflineAsync();
        a.OpenChest();
        a.Clock.Step(2);
        a.Engine.ToggleMatch();                                                 // Anna stops while offline
        Assert.Single(a.Outbox.PendingFinishes());
        await Task.Delay(500);
        Assert.Equal(shared, b.Client.Party!.ActiveMatch?.Id);                  // the server cannot know yet

        a.ComeBackOnline();
        await Wait.UntilAsync(() => b.Client.Party?.ActiveMatch is null, "offline stop delivered: shared match over");
        await Wait.UntilAsync(() => b.Client.Party?.TeamLifetime == P(2), "Anna's offline chest delivered too");
        var h = (await b.Client.GetHistoryAsync()).Single(x => x.Id == shared);
        Assert.Equal(P(2), h.Team);
        Assert.Empty(a.Outbox.PendingFinishes());
    }
}
