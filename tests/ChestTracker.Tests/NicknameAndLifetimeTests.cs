using ChestTracker.Core.Data;
using ChestTracker.Core.Presence;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Tracking;
using ChestTracker.Sync;

namespace ChestTracker.Tests;

/// <summary>Fix 2: Discord nickname priority.</summary>
public class NicknamePriorityTests
{
    [Fact]
    public void Server_name_wins_then_local_then_default()
    {
        Assert.Equal(new ResolvedNickname("Anna", NicknameSource.Server), NicknameResolver.Resolve("Anna", "Local"));
        Assert.Equal(new ResolvedNickname("Local", NicknameSource.Local), NicknameResolver.Resolve(null, "Local"));
        Assert.Equal(new ResolvedNickname("Local", NicknameSource.Local), NicknameResolver.Resolve("   ", " Local "));
        Assert.Equal(new ResolvedNickname("Player", NicknameSource.Default), NicknameResolver.Resolve(null, ""));
        Assert.Equal(new ResolvedNickname("Player", NicknameSource.Default), NicknameResolver.Resolve(null));
    }

    [Fact]
    public void Names_are_cleaned_and_limited()
    {
        Assert.Equal("Bad", NicknameResolver.Resolve("B\u0000a\nd").Name);
        Assert.Equal(NicknameResolver.MaxLength, NicknameResolver.Resolve(new string('x', 80)).Name.Length);
        // A surrogate pair is never split at the limit.
        var emoji = new string('x', 31) + "😀";
        Assert.Equal(new string('x', 31), NicknameResolver.Resolve(emoji).Name);
    }

    [Fact]
    public void Presence_state_uses_the_resolved_nickname()
    {
        var s = new DiscordSettings { Enabled = true, ShowNickname = true };
        var ctx = new PresenceContext { MatchCount = 3, MatchStartedAt = DateTimeOffset.UtcNow, Nickname = NicknameResolver.Resolve(null, null).Name };
        Assert.Equal("Player • 3 chests this match", PresenceBuilder.Build(ctx, s, new PresenceTexts())!.State);
    }

    [Fact]
    public async Task Cached_account_name_is_not_an_authenticated_identity()
    {
        using var db = new TempDatabase();
        var engine = db.CreateEngine(new FakeClock());
        var store = new InMemoryAccountStore();
        store.Save(new SyncAccount
        {
            ServerUrl = "http://127.0.0.1:9/", PlayerId = Guid.NewGuid(), Secret = "s", DisplayName = "CachedOrTampered",
        });
        var client = new SyncClient(engine, new SyncOutbox(db.Db), store, () => new SyncPreferences { AutoConnect = false });
        try
        {
            client.Start();
            Assert.Equal("CachedOrTampered", client.Account!.DisplayName);
            Assert.Null(client.VerifiedIdentity); // no authenticated server answer yet
            Assert.Equal(NicknameSource.Local, NicknameResolver.Resolve(client.VerifiedIdentity?.DisplayName, "Me").Source);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }
}

/// <summary>Fix 4: all-time statistics replace the session counter.</summary>
public class AllTimeStatisticsTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _db.Dispose();

    private TrackingEngine Engine() => _db.CreateEngine(_clock, new TrackingOptions { ManualDebounce = TimeSpan.Zero });

    private void Chests(TrackingEngine e, int n)
    {
        for (var i = 0; i < n; i++)
        {
            _clock.AdvanceSeconds(5);
            Assert.True(e.AddChestManual(fromHotkey: true).Applied);
        }
    }

    [Fact]
    public void All_time_and_total_matches_persist_across_restarts()
    {
        var e = Engine();
        e.StartMatch();
        Chests(e, 3);
        e.EndMatch();
        _clock.AdvanceSeconds(60);
        e.StartMatch();
        Chests(e, 2);
        e.Shutdown();

        _db.Reopen();
        _clock.Advance(TimeSpan.FromHours(1));
        var s = Engine().GetSnapshot();
        Assert.Equal(5, s.LifetimeCount);
        Assert.Equal(2, s.MatchesPlayed);
        Assert.Equal(0, s.MatchCount);
        Assert.Equal(0, s.SessionCount); // new session: the old counter would have shown 0 here
    }

    [Fact]
    public void Corrections_change_match_and_all_time_together()
    {
        var e = Engine();
        var first = e.StartMatch();
        Chests(e, 4);
        e.EndMatch();
        e.StartMatch();
        Chests(e, 2);

        e.SetMatchCount(first.Id, 1); // correct an old match: 4 → 1
        var s = e.GetSnapshot();
        Assert.Equal(2, s.MatchCount);
        Assert.Equal(3, s.LifetimeCount);
        Assert.Equal(1, _db.Repo.GetMatchTotal(first.Id));

        e.RemoveLastChest(fromHotkey: false); // undo in the current match
        s = e.GetSnapshot();
        Assert.Equal(1, s.MatchCount);
        Assert.Equal(2, s.LifetimeCount);

        e.SetCurrentMatchCount(5);
        s = e.GetSnapshot();
        Assert.Equal(5, s.MatchCount);
        Assert.Equal(6, s.LifetimeCount);

        e.DeleteMatch(first.Id);
        s = e.GetSnapshot();
        Assert.Equal(5, s.LifetimeCount);
        Assert.Equal(1, s.MatchesPlayed);
    }

    [Fact]
    public void Existing_history_is_kept_when_the_new_version_opens_the_database()
    {
        // Data written the way 1.1 wrote it (several sessions, a lifetime adjustment, a correction).
        var e = Engine();
        e.AdjustLifetime(100);
        var m = e.StartMatch();
        Chests(e, 3);
        e.SetMatchCount(m.Id, 2);
        e.Shutdown();
        var before = _db.Repo.GetAggregateStats();

        _db.Reopen();
        Assert.Equal(Migrations.LatestVersion, _db.Report.ToVersion);
        var s = Engine().GetSnapshot();
        Assert.Equal(102, s.LifetimeCount);
        Assert.Equal(before.MatchesPlayed, s.MatchesPlayed);
        Assert.Single(_db.Repo.GetMatchSummaries());
    }

    [Fact]
    public void Discord_lobby_text_and_old_session_priority_use_all_time()
    {
        var texts = new PresenceTexts();
        var s = new DiscordSettings { Enabled = true, ShowNickname = false };
        var lobby = new PresenceContext { SessionCount = 3, LifetimeCount = 250 };
        Assert.Equal("In lobby • 250 chests total", PresenceBuilder.Build(lobby, s, texts)!.State);

#pragma warning disable CS0618
        s.Priority = PresencePriority.SessionChests;
        Assert.Equal("250 chests total", PresenceBuilder.Build(lobby, s, texts)!.State);

        var settings = new AppSettings { Discord = { Priority = PresencePriority.SessionChests } }.Normalize();
#pragma warning restore CS0618
        Assert.Equal(PresencePriority.LifetimeChests, settings.Discord.Priority);
    }

    [Fact]
    public void Settings_saved_with_the_session_priority_still_load()
    {
        var file = Path.Combine(_db.Directory, "settings.json");
        File.WriteAllText(file, """{ "Discord": { "Enabled": true, "Priority": "SessionChests" } }""");
        var loaded = new SettingsStore(file).Load();
        Assert.Equal(PresencePriority.LifetimeChests, loaded.Discord.Priority);
        Assert.False(File.Exists(file + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss")));
    }
}

/// <summary>Fix 5: one Start Match / End Match button.</summary>
public class MatchToggleTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _db.Dispose();

    private TrackingEngine Engine() => _db.CreateEngine(_clock, new TrackingOptions { ManualDebounce = TimeSpan.Zero });

    [Fact]
    public void Toggle_starts_then_ends_and_saves_the_result()
    {
        var e = Engine();
        var start = e.ToggleMatch();
        Assert.Equal(MatchControlOutcome.Started, start.Outcome);
        Assert.NotNull(e.CurrentMatch);

        _clock.AdvanceSeconds(5);
        e.AddChestManual(fromHotkey: true);
        _clock.AdvanceSeconds(5);
        e.AddChestManual(fromHotkey: true);
        _clock.AdvanceSeconds(30);

        var end = e.ToggleMatch();
        Assert.Equal(MatchControlOutcome.Ended, end.Outcome);
        Assert.Equal(2, end.Summary!.Chests);
        Assert.Null(e.CurrentMatch);
        Assert.NotNull(_db.Repo.GetMatch(start.Match!.Id)!.EndedAt); // saved
        var s = e.GetSnapshot();
        Assert.Equal(2, s.LifetimeCount);
        Assert.Equal(1, s.MatchesPlayed);
    }

    [Fact]
    public void Double_click_neither_ends_the_new_match_nor_creates_a_second_one()
    {
        var e = Engine();
        var first = e.ToggleMatch();
        _clock.Advance(TimeSpan.FromMilliseconds(250));
        var second = e.ToggleMatch();
        Assert.Equal(MatchControlOutcome.IgnoredDoubleClick, second.Outcome);
        Assert.Equal(first.Match!.Id, e.CurrentMatch!.Id);

        _clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(MatchControlOutcome.IgnoredDoubleClick, e.RequestStartMatch().Outcome);
        Assert.Equal(1, e.GetSnapshot().MatchesPlayed);

        _clock.AdvanceSeconds(2);
        Assert.Equal(MatchControlOutcome.Ended, e.ToggleMatch().Outcome);
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(MatchControlOutcome.IgnoredDoubleClick, e.ToggleMatch().Outcome);
        Assert.Null(e.CurrentMatch);
        Assert.Equal(1, e.GetSnapshot().MatchesPlayed);
    }

    [Fact]
    public void End_without_a_match_reports_it()
    {
        var e = Engine();
        Assert.Equal(MatchControlOutcome.NoActiveMatch, e.RequestEndMatch().Outcome);
        Assert.Equal(MatchControlOutcome.Started, e.ToggleMatch().Outcome); // not blocked by the failed end
    }

    [Fact]
    public void Active_match_survives_a_crash()
    {
        var e = Engine();
        var m = e.ToggleMatch().Match!;
        _clock.AdvanceSeconds(5);
        e.AddChestManual(fromHotkey: true);
        _clock.AdvanceSeconds(5);
        e.AddChestManual(fromHotkey: true);
        // No Shutdown(): the process died.

        _db.Reopen();
        _clock.AdvanceSeconds(60);
        var restarted = Engine();
        Assert.Equal(m.Id, restarted.CurrentMatch!.Id);
        Assert.Equal(2, restarted.GetSnapshot().MatchCount);
        _clock.AdvanceSeconds(5);
        Assert.Equal(MatchControlOutcome.Ended, restarted.ToggleMatch().Outcome); // the button shows "End Match"
        Assert.Equal(2, restarted.GetSnapshot().LifetimeCount);
    }
}
