using System.Collections.Concurrent;
using ChestTracker.Core.Tracking;
using ChestTracker.Protocol;
using ChestTracker.Sync;

namespace ChestTracker.Server.Tests;

/// <summary>Two real desktop clients (local SQLite + engine + SyncClient) against the real server and PostgreSQL.</summary>
[Collection("server")]
public class EndToEndSyncTests(ServerFixture fx)
{
    private const string Url = "http://localhost/";

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
        await Wait.UntilAsync(() => a.Client.Party?.Members.Count == 2, "owner sees teammate");
        return (a, b);
    }

    [SkippableFact]
    public async Task Chests_recorded_offline_are_delivered_after_reconnect()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        var anna = a.Client.Account!.PlayerId;

        await a.GoOfflineAsync();
        a.Engine.StartMatch();
        a.OpenChest();
        a.OpenChest();
        a.OpenChest();
        a.Engine.RemoveLastChest(fromHotkey: true);
        Assert.Equal(4, a.Outbox.EnqueueNew()); // durable while offline

        await Wait.UntilAsync(() => b.Client.Party is { } p && !Member(b.Client, anna).Online, "teammate sees Anna offline");
        Assert.Equal(0, Member(b.Client, anna).Lifetime!.Reported);

        a.ComeBackOnline();
        await Wait.UntilAsync(() => Member(b.Client, anna).Lifetime?.Verified == 2, "Boris receives Anna's offline chests");
        Assert.True(Member(b.Client, anna).Online);
        Assert.Equal(0, a.Client.PendingCount);
        Assert.Equal(2, a.Engine.GetSnapshot().MatchCount);
    }

    [SkippableFact]
    public async Task Implausibly_fast_presses_count_as_reported_but_not_verified()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        var anna = a.Client.Account!.PlayerId;

        a.OpenChest();
        a.Engine.AddChestManual(fromHotkey: false); // same instant as the previous chest

        await Wait.UntilAsync(() => Member(b.Client, anna).Lifetime?.Reported == 2, "both reports synced");
        Assert.Equal(1, Member(b.Client, anna).Lifetime!.Verified);
    }

    [SkippableFact]
    public async Task Owner_match_start_is_mirrored_and_each_player_stops_only_their_own_counting()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        var activity = new ConcurrentQueue<ActivityDto>();
        b.Client.Activity += (_, x) => activity.Enqueue(x);

        a.Engine.StartMatch();   // owner presses F9
        await Wait.UntilAsync(() => b.Engine.CurrentMatch is not null, "Boris auto-joins the shared match");
        var shared = a.Client.Party?.ActiveMatch ?? b.Client.Party!.ActiveMatch!;
        Assert.Equal(shared.Id, b.Outbox.GetSharedMatch(b.Engine.CurrentMatch!.Id));

        a.OpenChest();
        b.OpenChest();
        b.OpenChest();
        await Wait.UntilAsync(() => activity.Any(x => x.Kind == EventKind.Add), "Boris sees Anna's chest live");
        await Wait.UntilAsync(() => a.Client.Party?.TeamMatch.Verified == 3, "team match total");

        a.Engine.EndMatch();     // owner dies and presses the match hotkey: only Anna stops counting
        await Wait.UntilAsync(() => a.Engine.CurrentMatch is null, "Anna stopped");
        await Task.Delay(500);
        Assert.NotNull(b.Engine.CurrentMatch);                        // Boris keeps playing in the shared match
        Assert.Equal(shared.Id, b.Client.Party!.ActiveMatch?.Id);
        Assert.Equal(TrackingResultCode.NoActiveMatch, a.Engine.AddChestManual(fromHotkey: true).Code); // Anna's adds are ignored

        b.OpenChest();                                                // Boris counts on; Anna still receives the totals
        await Wait.UntilAsync(() => a.Client.Party?.TeamMatch.Verified == 4, "Anna sees Boris's later chest");

        b.Engine.EndMatch();     // the last player stops: the shared match ends
        await Wait.UntilAsync(() => a.Client.Party?.ActiveMatch is null && b.Client.Party?.ActiveMatch is null, "shared match over");

        var history = await a.Client.GetHistoryAsync();
        var h = history.Single(x => x.Id == shared.Id);
        Assert.NotNull(h.EndedAt);
        Assert.Equal(4, h.Team.Verified);
        Assert.Equal(2, h.Participants.Count);
    }

    [SkippableFact]
    public async Task Leaving_stops_sharing_and_deleting_removes_server_data()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;
        var boris = b.Client.Account!.PlayerId;

        b.Engine.StartMatch();
        b.Engine.AddChestManual(fromHotkey: false);
        await Wait.UntilAsync(() => Member(a.Client, boris).Lifetime?.Reported == 1, "Boris's chest synced");

        await b.Client.DeleteServerDataAsync();
        Assert.Null(b.Client.Account);
        Assert.Equal(SyncStatus.NotConfigured, b.Client.Status);
        await Wait.UntilAsync(() => a.Client.Party?.Members.Count == 1, "Anna no longer sees Boris");
        Assert.Equal(0, a.Client.Party!.TeamLifetime.Reported);
        Assert.Equal(1, b.Engine.GetSnapshot().LifetimeCount); // local statistics are untouched
    }
}
