using System.Collections.Concurrent;
using ChestTracker.Core.Detection;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Tracking;
using ChestTracker.Protocol;
using ChestTracker.Sync;
using ChestTracker.Vision;

namespace ChestTracker.Server.Tests;

/// <summary>
/// Two real clients (engine + SyncClient + the app's sound router) against the real server: automatic openings
/// reach the party statistics, and only the player who records a chest hears the chest sound.
/// </summary>
[Collection("server")]
public class ChestSoundOwnershipTests(ServerFixture fx)
{
    private const string Url = "http://localhost/";

    /// <summary>A desktop client with the sound routing of the app (MainViewModel → LocalSoundRouter).</summary>
    private sealed class Player : IAsyncDisposable
    {
        public Player(ServerFixture fx)
        {
            Device = new TestDevice(fx);
            LocalSoundRouter.Attach(Device.Engine, Sounds.Enqueue);
            HookActivity();
        }

        public TestDevice Device { get; }
        public ConcurrentQueue<SoundCue> Sounds { get; } = new();
        public ConcurrentQueue<ActivityDto> Activity { get; } = new();
        public int ChestSounds => Sounds.Count(c => c == SoundCue.ChestAdded);
        public SyncClient Client => Device.Client;
        public Guid Id => Client.Account!.PlayerId;

        /// <summary>Teammate activity is shown (toast/overlay) but routed to no sound, exactly like the app.</summary>
        public void HookActivity() => Device.Client.Activity += (_, a) => Activity.Enqueue(a);

        public void ComeBackOnline()
        {
            Device.ComeBackOnline();
            HookActivity();
        }

        /// <summary>The model sees a closed chest, then the same chest open: the app's tracker settings, counting step and engine.</summary>
        public string OpenChestAutomatically()
        {
            if (Device.Engine.CurrentMatch is null)
            {
                Device.Engine.StartMatch(); // match hotkey: counting runs only inside a match
            }

            Device.Clock.Step(5);
            var labels = new ModelLabels(["closed_chest", "open_chest"]);
            var tracker = new YoloChestPipeline(new NoDetector(labels), YoloSettings.TrackingOptionsFor(new DetectionSettings())).Tracker;
            var box = new Box(900, 500, 1000, 575);
            var t = Device.Clock.UtcNow;
            var events = new List<ChestOpenEvent>();
            for (var i = 0; i < 6; i++)
            {
                var det = i < 3 ? new Detection(0, "closed_chest", ObjectRole.ChestClosed, 0.8f, box) : new Detection(1, "open_chest", ObjectRole.ChestOpened, 0.85f, box);
                events.AddRange(tracker.Update([det], t.AddMilliseconds(125 * i)).Events);
            }

            var ev = Assert.Single(events);
            return DetectionCounting.Apply(Device.Engine, ev.ToDecision() with { Timestamp = Device.Clock.UtcNow }, false, true, trackedIdentity: true,
                expectedMatchId: Device.Engine.CurrentMatch!.Id);
        }

        public ValueTask DisposeAsync() => Device.DisposeAsync();
    }

    private sealed class NoDetector(ModelLabels labels) : IObjectDetector
    {
        public ModelLabels Labels { get; } = labels;
        public IReadOnlyList<Detection> Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride) => [];
        public void Dispose() { }
    }

    private static MemberDto Member(SyncClient viewer, Guid player) => viewer.Party!.Members.Single(m => m.PlayerId == player);

    private async Task<(Player A, Player B)> PartyOfTwoAsync()
    {
        var a = new Player(fx);
        var b = new Player(fx);
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

    [SkippableFact]
    public async Task Only_the_player_who_adds_a_chest_hears_it_and_remote_updates_are_silent()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;

        a.Device.OpenChest();                                   // 13. A adds a chest by hand
        await Wait.UntilAsync(() => Member(b.Client, a.Id).Lifetime?.Reported == 1, "Boris's counters show Anna's chest");
        await Wait.UntilAsync(() => b.Activity.Any(x => x.Kind == EventKind.Add), "Boris received the live activity");
        Assert.Equal(1, a.ChestSounds);
        Assert.Equal(0, b.ChestSounds);                         // 15. SignalR update: counters move, no sound

        b.Device.OpenChest();                                   // 14. B adds a chest
        await Wait.UntilAsync(() => Member(a.Client, b.Id).Lifetime?.Reported == 1, "Anna's counters show Boris's chest");
        await Wait.UntilAsync(() => a.Client.Party?.TeamLifetime.Reported == 2 && b.Client.Party?.TeamLifetime.Reported == 2, "team total on both");
        Assert.Equal(1, a.ChestSounds);
        Assert.Equal(1, b.ChestSounds);

        a.Device.Engine.RemoveLastChest(fromHotkey: true);      // undo also only on the player's own computer
        await Wait.UntilAsync(() => Member(b.Client, a.Id).Lifetime?.Reported == 0, "undo synced");
        Assert.DoesNotContain(SoundCue.ChestRemoved, b.Sounds);
        Assert.Contains(SoundCue.ChestRemoved, a.Sounds);
    }

    [SkippableFact]
    public async Task Automatic_opening_updates_personal_and_party_statistics_and_sounds_only_locally()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;

        Assert.Equal("counted", a.OpenChestAutomatically());    // 10/11. A's model detects an opening
        Assert.Equal(1, a.Device.Engine.GetSnapshot().LifetimeCount);
        await Wait.UntilAsync(() => Member(b.Client, a.Id).Lifetime?.Verified == 1, "Boris sees Anna's detected chest");
        await Wait.UntilAsync(() => b.Client.Party?.TeamLifetime.Verified == 1, "party lifetime");
        Assert.Equal(1, a.ChestSounds);
        Assert.Equal(0, b.ChestSounds);

        Assert.Equal("counted", b.OpenChestAutomatically());    // B's model detects one
        await Wait.UntilAsync(() => a.Client.Party?.TeamLifetime.Verified == 2, "party lifetime from both");
        Assert.Equal(1, a.ChestSounds);
        Assert.Equal(1, b.ChestSounds);
    }

    [SkippableFact]
    public async Task Offline_replay_and_server_restart_play_no_sounds()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (a, b) = await PartyOfTwoAsync();
        await using var _a = a;
        await using var _b = b;

        await a.Device.GoOfflineAsync();
        a.Device.OpenChest();
        a.Device.OpenChest();
        Assert.Equal(2, a.ChestSounds);                         // heard once each, when recorded

        a.ComeBackOnline();                                     // 16. outbox delivered after reconnect
        await Wait.UntilAsync(() => Member(b.Client, a.Id).Lifetime?.Reported == 2, "offline chests delivered");
        Assert.Equal(2, a.ChestSounds);
        Assert.Equal(0, b.ChestSounds);

        await fx.RestartAsync();                                // server restart: clients reconnect, totals re-sent
        await Wait.UntilAsync(() => a.Client.Status == SyncStatus.Online && b.Client.Status == SyncStatus.Online, "reconnected", 30000);
        await Wait.UntilAsync(() => b.Client.Party?.TeamLifetime.Reported == 2, "totals after restart", 30000);
        Assert.Equal(2, a.ChestSounds);
        Assert.Equal(0, b.ChestSounds);
    }
}
