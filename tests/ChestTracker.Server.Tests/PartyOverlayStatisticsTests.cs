using ChestTracker.Protocol;
using ChestTracker.Sync;

namespace ChestTracker.Server.Tests;

/// <summary>
/// The team numbers shown by the overlay and the Party page (both from <see cref="PartyStatistics"/>) follow the
/// server's authoritative party state through local and remote chests, undo, membership changes, reconnects and restarts.
/// </summary>
[Collection("server")]
public class PartyOverlayStatisticsTests(ServerFixture fx)
{
    private const string Url = "http://localhost/";

    private static CountPair P(int n) => new(n, n);

    /// <summary>Exactly what the overlay shows (and the Party page header).</summary>
    private static PartyStatistics? View(TestDevice d) => PartyStatistics.From(d.Client.Party, d.Client.Account?.PlayerId);

    [SkippableFact]
    public async Task Overlay_numbers_follow_local_and_remote_chests_undo_membership_reconnect_and_restart()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var a = new TestDevice(fx);
        var b = new TestDevice(fx);
        await using var _a = a;
        await using var _b = b;
        a.Client.Start();
        b.Client.Start();
        await a.Client.RegisterAsync(Url, "Anna");
        await b.Client.RegisterAsync(Url, "Boris");
        await Wait.UntilAsync(() => a.Client.Status == SyncStatus.Online && b.Client.Status == SyncStatus.Online, "online");
        var party = await a.Client.CreatePartyAsync("Duo", 4);
        await b.Client.JoinPartyAsync(party.InviteCode!);
        await Wait.UntilAsync(() => View(a)?.Members.Count == 2 && View(b)?.Members.Count == 2, "joined: both overlays list two members");

        a.Engine.ToggleMatch();
        await Wait.UntilAsync(() => View(a)?.SharedMatchActive == true && b.Engine.CurrentMatch is not null, "shared match");

        // Local chest (Anna) and remote chests (Boris) reach Anna's overlay.
        a.OpenChest();
        b.OpenChest();
        b.OpenChest();
        await Wait.UntilAsync(() => View(a)?.TeamMatch == P(3) && View(a)?.TeamLifetime == P(3), "team match 3, team total 3");
        Assert.Equal(P(1), View(a)!.MyMatch);
        Assert.Equal(P(2), View(a)!.Members.Single(m => !m.IsMe).Match);

        // Undo by the teammate updates both numbers.
        b.Engine.RemoveLastChest(fromHotkey: true);
        await Wait.UntilAsync(() => View(a)?.TeamMatch == P(2) && View(a)?.TeamLifetime == P(2), "undo");

        // Both players stop; the shared match ends: no team-match number, the team total stays.
        a.Clock.Step(2);
        a.Engine.ToggleMatch();
        b.Clock.Step(2);
        b.Engine.ToggleMatch();
        await Wait.UntilAsync(() => View(a) is { SharedMatchActive: false, TeamMatch: null } v && v.TeamLifetime == P(2), "match over");
        Assert.Equal("—", PartyStatistics.Format(View(a)!.TeamMatch));

        // Boris leaves: his chests stay in the party total (former members count); he is no longer listed.
        await b.Client.LeavePartyAsync();
        await Wait.UntilAsync(() => View(a)?.Members.Count == 1, "member left");
        Assert.Equal(P(2), View(a)!.TeamLifetime);
        Assert.Null(View(b));                                                 // Boris's overlay leaves the team layout

        // Anna reconnects after counting offline: the overlay refreshes from the server.
        await a.GoOfflineAsync();
        a.Engine.ToggleMatch();
        a.OpenChest();
        a.ComeBackOnline();
        await Wait.UntilAsync(() => View(a)?.TeamLifetime == P(3), "reconnect refreshes the total");

        // Server restart and an app restart (new client on the same database) restore the same numbers.
        await fx.RestartAsync();
        await Wait.UntilAsync(() => a.Client.Status == SyncStatus.Online && View(a)?.TeamLifetime == P(3), "after server restart", 30000);
        await a.GoOfflineAsync();
        a.ComeBackOnline();                                                   // fresh client, state loaded from the server
        await Wait.UntilAsync(() => View(a)?.TeamLifetime == P(3) && View(a)?.Members.Count == 1, "after app restart", 30000);
    }
}
