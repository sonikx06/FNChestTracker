using System.Net;
using System.Net.Http.Json;
using ChestTracker.Protocol;
using ChestTracker.Sync;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChestTracker.Server.Tests;

[Collection("server")]
public class PartyServerTests(ServerFixture fx)
{
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private async Task<(TestPlayer Owner, PartyStateDto State)> NewPartyAsync(int max = 4)
    {
        var owner = await fx.RegisterAsync("Owner");
        var state = await owner.PostAsync<PartyStateDto>("/api/parties", new CreatePartyRequest("Squad", max));
        return (owner, state);
    }

    private async Task<TestPlayer> JoinAsync(PartyStateDto state, string name = "Mate")
    {
        var p = await fx.RegisterAsync(name);
        await p.PostAsync<PartyStateDto>("/api/parties/join", new JoinPartyRequest(state.InviteCode!));
        return p;
    }

    [SkippableFact]
    public async Task Register_issues_token_and_wrong_secret_is_refused()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var p = await fx.RegisterAsync("Alice");
        var me = await p.GetAsync<ProfileDto>("/api/me");
        Assert.Equal("Alice", me.DisplayName);

        using var anon = fx.CreateClient();
        var bad = await anon.PostAsJsonAsync("/api/auth/token", new TokenRequest(p.Id, "wrong"), SyncClient.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/me")).StatusCode);
    }

    [SkippableFact]
    public async Task Join_by_code_accepts_formatting_and_enforces_capacity()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await NewPartyAsync(max: 2);
        Assert.Matches("^[A-Z2-9]{8}$", state.InviteCode);

        var mate = await fx.RegisterAsync("Mate");
        var formatted = InviteCodes.Format(state.InviteCode!).ToLowerInvariant();
        var joined = await mate.PostAsync<PartyStateDto>("/api/parties/join", new JoinPartyRequest(formatted));
        Assert.Equal(2, joined.Members.Count);

        var third = await fx.RegisterAsync("Third");
        var full = await third.PostRawAsync("/api/parties/join", new JoinPartyRequest(state.InviteCode!));
        Assert.Equal(409, full.Status);
        Assert.Equal(ErrorCodes.PartyFull, full.Error!.Code);

        var wrong = await third.PostRawAsync("/api/parties/join", new JoinPartyRequest("ZZZZ-ZZZZ"));
        Assert.Equal(404, wrong.Status);
        Assert.Equal(ErrorCodes.InvalidCode, wrong.Error!.Code);
    }

    [SkippableFact]
    public async Task Regenerated_invite_invalidates_the_old_code()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await NewPartyAsync();
        var renewed = await owner.PostAsync<PartyStateDto>("/api/parties/current/invite");
        Assert.NotEqual(state.InviteCode, renewed.InviteCode);

        var p = await fx.RegisterAsync("Late");
        Assert.Equal(404, (await p.PostRawAsync("/api/parties/join", new JoinPartyRequest(state.InviteCode!))).Status);
        var mate = await JoinAsync(renewed);
        Assert.Equal(403, (await mate.PostRawAsync("/api/parties/current/invite")).Status);
    }

    [SkippableFact]
    public async Task Event_processing_is_idempotent_and_ids_belong_to_one_player()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await NewPartyAsync();
        var mate = await JoinAsync(state);
        var e1 = Ev.Add(Now.AddSeconds(-10));
        var e2 = Ev.Add(Now.AddSeconds(-5));

        var first = await owner.SubmitAsync(e1, e2);
        var retry = await owner.SubmitAsync(e1, e2);
        var stolen = await mate.SubmitAsync(e1);

        Assert.All(first, a => Assert.Equal(AckStatus.Accepted, a.Status));
        Assert.All(retry, a => Assert.Equal(AckStatus.Duplicate, a.Status));
        Assert.Equal(AckStatus.Rejected, stolen[0].Status);
        Assert.Equal("id_conflict", stolen[0].Reason);
        var s = await owner.StateAsync();
        Assert.Equal(new CountPair(2, 2), s.Members.Single(m => m.PlayerId == owner.Id).Lifetime);
        Assert.Equal(new CountPair(2, 2), s.TeamLifetime);
    }

    [SkippableFact]
    public async Task Events_from_non_members_are_rejected()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var loner = await fx.RegisterAsync("Loner");
        var acks = await loner.SubmitAsync(Ev.Add(Now));
        Assert.Equal(AckStatus.Rejected, acks[0].Status);
        Assert.Equal(ErrorCodes.NotMember, acks[0].Reason);
    }

    [SkippableFact]
    public async Task Reported_and_verified_counts_differ_for_implausible_events_and_corrections()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, _) = await NewPartyAsync();
        var t = Now.AddMinutes(-2);
        var a1 = Ev.Add(t);
        var a2 = Ev.Add(t.AddMilliseconds(300));     // too fast after a1: stored, not verified
        var a3 = Ev.Add(t.AddSeconds(5));
        var undo = Ev.Undo(a3, t.AddSeconds(6));      // removes a verified chest from both counts
        var corr = Ev.Correction(+3, t.AddSeconds(7)); // manual edit: reported only

        var acks = await owner.SubmitAsync(a1, a2, a3, undo, corr);

        Assert.All(acks, a => Assert.Equal(AckStatus.Accepted, a.Status));
        Assert.False(acks[1].Verified);
        Assert.Equal("rate_limited", acks[1].Reason);
        Assert.Equal("manual_correction", acks[4].Reason);
        var me = (await owner.StateAsync()).Members.Single();
        Assert.Equal(1, me.Lifetime!.Verified);  // a1
        Assert.Equal(5, me.Lifetime.Reported);   // 1 + 1 + 1 - 1 + 3

        var again = await owner.SubmitAsync(Ev.Undo(a3, t.AddSeconds(8)));
        Assert.Equal("already_undone", again[0].Reason);
    }

    [SkippableFact]
    public async Task Timestamps_outside_the_allowed_range_are_rejected()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, _) = await NewPartyAsync();
        var acks = await owner.SubmitAsync(Ev.Add(Now.AddHours(1)), Ev.Add(Now.AddDays(-30)), Ev.Add(Now.AddDays(-3)));

        Assert.Equal("timestamp_in_future", acks[0].Reason);
        Assert.Equal("timestamp_too_old", acks[1].Reason);
        Assert.Equal(AckStatus.Accepted, acks[2].Status);   // offline queue from 3 days ago is fine
    }

    [SkippableFact]
    public async Task Shared_match_attributes_events_by_their_own_time_even_when_delivered_late()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await NewPartyAsync();
        var mate = await JoinAsync(state);
        var ownerHub = await owner.HubAsync();
        var mateHub = await mate.HubAsync();

        var match = await ownerHub.InvokeAsync<SharedMatchDto>(HubMethods.StartSharedMatch);
        var again = await ownerHub.InvokeAsync<SharedMatchDto>(HubMethods.StartSharedMatch);
        Assert.Equal(match.Id, again.Id);
        await Assert.ThrowsAsync<HubException>(() => mateHub.InvokeAsync<SharedMatchDto>(HubMethods.StartSharedMatch));

        // Not yet accepted: attribution refused.
        var early = await mate.SubmitAsync(Ev.Add(Now, match.Id));
        Assert.Equal("outside_shared_match", early[0].Reason);

        await mateHub.InvokeAsync(HubMethods.RespondSharedMatch, match.Id, true);
        var duringMatch = Now;
        await owner.SubmitAsync(Ev.Add(duringMatch, match.Id));
        await ownerHub.InvokeAsync(HubMethods.EndSharedMatch, match.Id);

        // Mate was offline: events recorded during the match arrive after it ended.
        var late = await mate.SubmitAsync(Ev.Add(duringMatch.AddSeconds(2), match.Id), Ev.Add(Now.AddMinutes(3), match.Id));
        Assert.Null(late[0].Reason);
        Assert.Equal("outside_shared_match", late[1].Reason);

        var history = await owner.GetAsync<List<SharedMatchHistoryDto>>("/api/parties/current/matches");
        var h = history.Single(x => x.Id == match.Id);
        Assert.NotNull(h.EndedAt);
        Assert.Equal(2, h.Team.Verified);
        Assert.Equal(1, h.Participants.Single(p => p.PlayerId == mate.Id).Counts!.Verified);
    }

    [SkippableFact]
    public async Task Owner_leaving_hands_over_and_last_member_leaving_deletes_party()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await NewPartyAsync();
        var mate = await JoinAsync(state);

        await owner.PostAsync<object>("/api/parties/current/leave");
        var s = await mate.StateAsync();
        Assert.Equal(mate.Id, s.OwnerId);
        Assert.Single(s.Members);

        await mate.PostAsync<object>("/api/parties/current/leave");
        var newcomer = await fx.RegisterAsync("New");
        Assert.Equal(404, (await newcomer.PostRawAsync("/api/parties/join", new JoinPartyRequest(s.InviteCode!))).Status);
    }

    [SkippableFact]
    public async Task Owner_controls_kick_and_transfer()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await NewPartyAsync();
        var mate = await JoinAsync(state);
        var other = await JoinAsync(state, "Other");

        Assert.Equal(403, (await mate.PostRawAsync($"/api/parties/current/kick/{other.Id}")).Status);
        await owner.PostAsync<object>($"/api/parties/current/kick/{other.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Http.GetAsync("/api/parties/current")).StatusCode);
        Assert.DoesNotContain((await owner.StateAsync()).Members, m => m.PlayerId == other.Id);

        await owner.PostAsync<object>($"/api/parties/current/transfer/{mate.Id}");
        var s = await mate.StateAsync();
        Assert.Equal(mate.Id, s.OwnerId);
        Assert.Equal(PartyRole.Owner, s.Members.Single(m => m.PlayerId == mate.Id).Role);
    }

    [SkippableFact]
    public async Task Hidden_statistics_are_not_shared()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await NewPartyAsync();
        var mate = await JoinAsync(state);
        await owner.SubmitAsync(Ev.Add(Now.AddSeconds(-20)));
        await mate.SubmitAsync(Ev.Add(Now.AddSeconds(-20)), Ev.Add(Now.AddSeconds(-10)));

        var resp = await mate.Http.PutAsJsonAsync("/api/parties/current/sharing", new SharingRequest(false, false), SyncClient.Json);
        resp.EnsureSuccessStatusCode();

        var s = await owner.StateAsync();
        Assert.Null(s.Members.Single(m => m.PlayerId == mate.Id).Lifetime);
        Assert.Equal(1, s.TeamLifetime.Verified);
        var board = await owner.GetAsync<List<LeaderboardEntryDto>>("/api/parties/current/leaderboard?period=Lifetime");
        Assert.DoesNotContain(board, e => e.PlayerId == mate.Id);
    }

    [SkippableFact]
    public async Task Leaderboard_ranks_by_verified_chests_per_period()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await NewPartyAsync();
        var mate = await JoinAsync(state);
        await owner.SubmitAsync(Ev.Add(Now.AddDays(-3)), Ev.Add(Now.AddDays(-3).AddSeconds(5)), Ev.Add(Now.AddDays(-3).AddSeconds(10)));
        await mate.SubmitAsync(Ev.Add(Now.AddMinutes(-1)), Ev.Add(Now.AddSeconds(-20)));

        var lifetime = await owner.GetAsync<List<LeaderboardEntryDto>>("/api/parties/current/leaderboard?period=Lifetime");
        var today = await owner.GetAsync<List<LeaderboardEntryDto>>("/api/parties/current/leaderboard?period=Day&tzOffsetMinutes=0");

        Assert.Equal(owner.Id, lifetime[0].PlayerId);
        Assert.Equal(3, lifetime[0].Verified);
        Assert.Equal(mate.Id, today[0].PlayerId);
        Assert.Equal(2, today[0].Verified);
        Assert.Equal(0, today.Single(e => e.PlayerId == owner.Id).Verified);
    }

    [SkippableFact]
    public async Task Deleting_account_removes_player_and_their_events()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await NewPartyAsync();
        var mate = await JoinAsync(state);
        await mate.SubmitAsync(Ev.Add(Now.AddSeconds(-5)));

        var del = await mate.Http.DeleteAsync("/api/me");
        del.EnsureSuccessStatusCode();

        var s = await owner.StateAsync();
        Assert.Single(s.Members);
        Assert.Equal(0, s.TeamLifetime.Reported);
        using var anon = fx.CreateClient();
        var token = await anon.PostAsJsonAsync("/api/auth/token", new TokenRequest(mate.Id, mate.Secret), SyncClient.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, token.StatusCode);
    }

    [SkippableFact]
    public async Task Presence_follows_hub_connections()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (owner, state) = await NewPartyAsync();
        var mate = await JoinAsync(state);
        Assert.False((await owner.StateAsync()).Members.Single(m => m.PlayerId == mate.Id).Online);

        var hub = await mate.HubAsync();
        Assert.True((await owner.StateAsync()).Members.Single(m => m.PlayerId == mate.Id).Online);

        await hub.StopAsync();
        await Wait.UntilAsync(async () => !(await owner.StateAsync()).Members.Single(m => m.PlayerId == mate.Id).Online, "mate offline");
    }
}

