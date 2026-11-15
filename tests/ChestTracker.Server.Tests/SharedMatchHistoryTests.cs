using System.Net;
using System.Net.Http.Json;
using ChestTracker.Protocol;
using ChestTracker.Sync;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChestTracker.Server.Tests;

[Collection("server")]
public sealed class SharedMatchHistoryTests(ServerFixture fx)
{
    private const string History = "/api/parties/current/matches";
    private const string Summary = History + "/summary";
    private static CountPair Pair(int n) => new(n, n);

    private async Task<(TestPlayer A, TestPlayer B, PartyStateDto Party)> Party()
    {
        var a = await fx.RegisterAsync("A");
        var b = await fx.RegisterAsync("Борис");
        var party = await a.PostAsync<PartyStateDto>("/api/parties", new CreatePartyRequest("History test", 4));
        await b.PostAsync<PartyStateDto>("/api/parties/join", new JoinPartyRequest(party.InviteCode!));
        return (a, b, party);
    }

    private static async Task<SharedMatchDto> Start(TestPlayer a, params TestPlayer[] participants)
    {
        var match = await (await a.HubAsync()).InvokeAsync<SharedMatchDto>(HubMethods.StartSharedMatch);
        foreach (var p in participants) await (await p.HubAsync()).InvokeAsync(HubMethods.RespondSharedMatch, match.Id, true);
        return match;
    }
    private static async Task End(TestPlayer a, SharedMatchDto m) =>
        await (await a.HubAsync()).InvokeAsync(HubMethods.EndSharedMatch, m.Id);

    private static ChestEventDto[] Adds(int count, SharedMatchDto m, int seconds = 0)
    {
        var local = Guid.NewGuid();
        return Enumerable.Range(0, count).Select(i => Ev.Add(m.StartedAt.AddSeconds(seconds + i * 2), m.Id, local)).ToArray();
    }

    [SkippableFact]
    public async Task Two_matches_keep_former_participants_and_summary_across_restart_and_retries()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b, party) = await Party();
        await using var owner = a;
        await using var mate = b;
        var first = await Start(a, b);
        var aEvents = Adds(5, first);
        var bEvents = Adds(8, first);
        Assert.All(await a.SubmitAsync(aEvents), ack => Assert.True(ack.Verified));
        Assert.All(await b.SubmitAsync(bEvents), ack => Assert.True(ack.Verified));
        await End(a, first);
        await b.PostAsync<object>("/api/parties/current/leave");
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Http.GetAsync(History)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Http.GetAsync(Summary)).StatusCode);
        Assert.Equal(Pair(13), (await a.GetAsync<List<SharedMatchHistoryDto>>(History)).Single().Team);

        await using var c = await fx.RegisterAsync("C");
        await c.PostAsync<PartyStateDto>("/api/parties/join", new JoinPartyRequest(party.InviteCode!));
        var second = await Start(a, c);
        Assert.All(await a.SubmitAsync(Adds(2, second, 20)), ack => Assert.True(ack.Verified));
        Assert.All(await c.SubmitAsync(Adds(4, second, 20)), ack => Assert.True(ack.Verified));
        await End(a, second);
        var summary = await a.GetAsync<SharedMatchSummaryDto>(Summary);
        Assert.Equal(2, summary.CompletedMatches);
        Assert.Equal(new MatchCountSummary(19, 9.5, 13), summary.Verified);
        Assert.Equal(summary.Verified, summary.Reported);
        var rows = await a.GetAsync<List<SharedMatchHistoryDto>>(History);
        Assert.Equal(2, rows.Count);
        Assert.Equal(Pair(6), rows.Single(m => m.Id == second.Id).Team);
        var old = rows.Single(m => m.Id == first.Id);
        Assert.Equal(Pair(8), old.Participants.Single(p => p.PlayerId == b.Id).Counts);
        Assert.DoesNotContain(old.Participants, p => p.PlayerId == c.Id);
        Assert.Equal(party.Name, old.PartyName);
        Assert.Equal(SharedMatchStatus.Ended, old.Status);

        await b.PostAsync<PartyStateDto>("/api/parties/join", new JoinPartyRequest(party.InviteCode!));
        Assert.All(await b.SubmitAsync(bEvents), ack => Assert.Equal(AckStatus.Duplicate, ack.Status));
        Assert.All(await a.SubmitAsync(aEvents), ack => Assert.Equal(AckStatus.Duplicate, ack.Status));
        await fx.RestartAsync();
        using var http = fx.CreateClient();
        var token = await (await http.PostAsJsonAsync("/api/auth/token", new TokenRequest(a.Id, a.Secret), SyncClient.Json))
            .Content.ReadFromJsonAsync<TokenResponse>(SyncClient.Json);
        http.DefaultRequestHeaders.Authorization = new("Bearer", token!.AccessToken);
        Assert.Equal(summary, await http.GetFromJsonAsync<SharedMatchSummaryDto>(Summary, SyncClient.Json));
        var restored = await http.GetFromJsonAsync<List<SharedMatchHistoryDto>>(History, SyncClient.Json);
        Assert.Equal(2, restored!.Count);
        Assert.Equal(Pair(13), restored.Single(m => m.Id == first.Id).Team);
    }

    [SkippableFact]
    public async Task Empty_zero_and_active_matches_use_one_completed_set_with_period_filters_and_paging()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b, _) = await Party();
        await using var owner = a;
        await using var mate = b;
        var empty = await a.GetAsync<SharedMatchSummaryDto>(Summary);
        Assert.Equal(0, empty.CompletedMatches);
        Assert.Equal(new MatchCountSummary(0, 0, 0), empty.Verified);
        var zero = await Start(a, b);
        await End(a, zero);
        var active = await Start(a, b);
        await a.SubmitAsync(Adds(2, active));
        var summary = await a.GetAsync<SharedMatchSummaryDto>(Summary);
        Assert.Equal(1, summary.CompletedMatches);
        Assert.Equal(new MatchCountSummary(0, 0, 0), summary.Reported);
        Assert.Single(await a.GetAsync<List<SharedMatchHistoryDto>>(History + "?limit=1&offset=1"));
        await End(a, active);
        summary = await a.GetAsync<SharedMatchSummaryDto>(Summary);
        Assert.Equal(2, summary.CompletedMatches);
        Assert.Equal(new MatchCountSummary(2, 1, 2), summary.Verified);
        var range = "?from=" + Uri.EscapeDataString(active.StartedAt.ToString("O"));
        Assert.Single(await a.GetAsync<List<SharedMatchHistoryDto>>(History + range));
        Assert.Equal(1, (await a.GetAsync<SharedMatchSummaryDto>(Summary + range)).CompletedMatches);
        var before = "?to=" + Uri.EscapeDataString(active.StartedAt.ToString("O"));
        Assert.Equal(Pair(0), (await a.GetAsync<List<SharedMatchHistoryDto>>(History + before)).Single().Team);
    }

    [SkippableFact]
    public async Task Late_delivery_undo_and_correction_change_original_match_with_distinct_bases()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b, party) = await Party();
        await using var owner = a;
        await using var mate = b;
        var first = await Start(a, b);
        var events = Adds(3, first);
        await a.SubmitAsync(events[0]);
        await End(a, first);
        var second = await Start(a, b);
        // Offline events retain their original timestamps and match IDs while another match is active.
        Assert.All(await a.SubmitAsync(events.Skip(1).ToArray()), ack => Assert.True(ack.Verified));
        var undo = Ev.Undo(events[0], first.StartedAt.AddSeconds(190)) with { SharedMatchId = second.Id };
        var correction = Ev.Correction(4, first.StartedAt.AddSeconds(191)) with
            { SharedMatchId = first.Id, LocalMatchId = events[0].LocalMatchId };
        // Move only this test match into the past so these are beyond its grace but not future/expired events.
        await using (var db = new Npgsql.NpgsqlConnection(fx.DatabaseConnectionString))
        {
            await db.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand("UPDATE shared_matches SET started_at=started_at-interval '10 minutes', ended_at=ended_at-interval '10 minutes' WHERE id=@id", db);
            cmd.Parameters.AddWithValue("id", first.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        undo = undo with { ClientTimestamp = DateTimeOffset.UtcNow };
        correction = correction with { ClientTimestamp = DateTimeOffset.UtcNow };
        Assert.True((await a.SubmitAsync(undo))[0].Verified);
        Assert.False((await a.SubmitAsync(correction))[0].Verified);
        var rows = await a.GetAsync<List<SharedMatchHistoryDto>>(History);
        Assert.Equal(new CountPair(2, 6), rows.Single(m => m.Id == first.Id).Team);
        Assert.Equal(Pair(0), rows.Single(m => m.Id == second.Id).Team);
        Assert.All(await a.SubmitAsync(undo, correction), ack => Assert.Equal(AckStatus.Duplicate, ack.Status));
        var summary = await a.GetAsync<SharedMatchSummaryDto>(Summary);
        Assert.Equal(new MatchCountSummary(2, 2, 2), summary.Verified);
        Assert.Equal(new MatchCountSummary(6, 6, 6), summary.Reported);
        Assert.Equal(party.PartyId, (await a.StateAsync()).PartyId);
    }

    [SkippableFact]
    public async Task Historical_visibility_and_account_deletion_apply_to_details_and_summary()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b, _) = await Party();
        await using var owner = a;
        await using var mate = b;
        var match = await Start(a, b);
        await a.SubmitAsync(Adds(1, match));
        await b.SubmitAsync(Adds(2, match));
        await End(a, match);
        (await b.Http.PutAsJsonAsync("/api/parties/current/sharing", new SharingRequest(false, false), SyncClient.Json)).EnsureSuccessStatusCode();
        var history = (await a.GetAsync<List<SharedMatchHistoryDto>>(History)).Single();
        Assert.Null(history.Participants.Single(p => p.PlayerId == b.Id).Counts);
        Assert.Equal(Pair(1), history.Team);
        Assert.Equal(1, (await a.GetAsync<SharedMatchSummaryDto>(Summary)).Reported.Total);
        (await b.Http.DeleteAsync("/api/me")).EnsureSuccessStatusCode();
        Assert.Single((await a.GetAsync<List<SharedMatchHistoryDto>>(History)).Single().Participants);
        Assert.Equal(1, (await a.GetAsync<SharedMatchSummaryDto>(Summary)).Verified.Total);
    }

    [SkippableFact]
    public async Task Summary_is_not_limited_to_history_page_and_kicking_preserves_participation()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b, party) = await Party();
        await using var owner = a;
        await using var mate = b;
        var first = await Start(a, b);
        var events = Adds(2, first);
        await b.SubmitAsync(events);
        // Repeated acceptance or a late decline must not remove an already accepted participant's history.
        await (await b.HubAsync()).InvokeAsync(HubMethods.RespondSharedMatch, first.Id, false);
        await a.PostAsync<object>($"/api/parties/current/kick/{b.Id}");
        await End(a, first);
        Assert.Equal(Pair(2), (await a.GetAsync<List<SharedMatchHistoryDto>>(History)).Single().Team);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Http.GetAsync(Summary)).StatusCode);
        await b.PostAsync<PartyStateDto>("/api/parties/join", new JoinPartyRequest(party.InviteCode!));
        Assert.All(await b.SubmitAsync(events), ack => Assert.Equal(AckStatus.Duplicate, ack.Status));
        for (var i = 0; i < 21; i++) await End(a, await Start(a, b));
        Assert.Equal(20, (await a.GetAsync<List<SharedMatchHistoryDto>>(History + "?limit=20")).Count);
        Assert.Equal(2, (await a.GetAsync<List<SharedMatchHistoryDto>>(History + "?limit=20&offset=20")).Count);
        var summary = await a.GetAsync<SharedMatchSummaryDto>(Summary);
        Assert.Equal(22, summary.CompletedMatches);
        Assert.Equal(2, summary.Verified.Total);
        Assert.Equal(2.0 / 22, summary.Verified.Average, 10);
    }

    [SkippableFact]
    public async Task Correction_to_another_partys_match_never_changes_current_party_totals()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b, _) = await Party();
        await using var owner = a;
        await using var mate = b;
        var old = await Start(a, b);
        await b.SubmitAsync(Adds(1, old));
        await End(a, old);
        await b.PostAsync<PartyStateDto>("/api/parties", new CreatePartyRequest("Other party", 4));
        var correction = Ev.Correction(4, DateTimeOffset.UtcNow) with { SharedMatchId = old.Id };
        Assert.Equal(AckStatus.Rejected, (await b.SubmitAsync(correction))[0].Status);
        Assert.Equal(Pair(0), (await b.StateAsync()).TeamLifetime);
        Assert.Equal(Pair(1), (await a.GetAsync<List<SharedMatchHistoryDto>>(History)).Single().Team);
    }
}
