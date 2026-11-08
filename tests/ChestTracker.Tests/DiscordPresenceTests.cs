using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ChestTracker.Core.Models;
using ChestTracker.Core.Presence;
using ChestTracker.Core.Settings;

namespace ChestTracker.Tests;

public class PresenceBuilderTests
{
    private static readonly PresenceTexts T = new();
    private static readonly DateTimeOffset Start = new(2026, 11, 8, 20, 0, 0, TimeSpan.Zero);

    private static PresenceContext Solo(int match = 12) => new()
    {
        MatchCount = match, SessionCount = 30, LifetimeCount = 1234, MatchStartedAt = Start, Nickname = "User",
        Mode = TrackingMode.Manual,
    };

    [Fact]
    public void Solo_presence_matches_the_specified_layout()
    {
        var a = PresenceBuilder.Build(Solo(), new DiscordSettings(), T)!;

        Assert.Equal("Solo", a.Details);
        Assert.Equal("User • 12 chests this match", a.State);
        Assert.Equal(Start, a.Start);
        Assert.Equal("logo", a.LargeImage);
        Assert.Equal("solo", a.SmallImage);
        Assert.Equal("Fortnite Chest Tracker", a.SmallText);
        Assert.Equal(PresenceBuilder.ProjectUrl, Assert.Single(a.Buttons).Url);
    }

    [Fact]
    public void Team_presence_shows_player_count_and_team_icon()
    {
        var c = Solo() with { Team = new TeamPresence(2, 4, 20, 300) };

        var a = PresenceBuilder.Build(c, new DiscordSettings(), T)!;

        Assert.Equal("Team • 2/4 players", a.Details);
        Assert.Equal("team", a.SmallImage);
        Assert.Equal("Solo", PresenceBuilder.Build(c, new DiscordSettings { ShowTeam = false }, T)!.Details);
    }

    [Theory]
    [InlineData(PresencePriority.MatchChests, "User • 12 chests this match")]
    [InlineData(PresencePriority.LifetimeChests, "User • 1234 chests total")]
    [InlineData(PresencePriority.TeamChests, "User • Team: 20 chests this match")]
    public void Priority_selects_the_main_statistic(PresencePriority priority, string expected)
    {
        var c = Solo() with { Team = new TeamPresence(2, 4, 20, 300) };
        Assert.Equal(expected, PresenceBuilder.Build(c, new DiscordSettings { Priority = priority }, T)!.State);
    }

    [Fact]
    public void Outside_a_match_shows_lobby_and_no_timer()
    {
        var a = PresenceBuilder.Build(Solo() with { MatchStartedAt = null }, new DiscordSettings(), T)!;

        Assert.Equal("User • In lobby • 1234 chests total", a.State);
        Assert.Null(a.Start);
    }

    [Fact]
    public void Toggles_hide_nickname_timer_and_add_lifetime()
    {
        var s = new DiscordSettings { ShowNickname = false, ShowTimer = false, ShowLifetime = true };

        var a = PresenceBuilder.Build(Solo(), s, T)!;

        Assert.Equal("12 chests this match", a.State);
        Assert.Null(a.Start);
        Assert.Equal("Chest Tracker • 1,234 chests total", a.LargeText);
    }

    [Fact]
    public void Disabled_or_fortnite_not_running_clears_presence()
    {
        Assert.Null(PresenceBuilder.Build(Solo(), new DiscordSettings { Enabled = false }, T));
        var notRunning = Solo() with { FortniteRunning = false };
        Assert.Null(PresenceBuilder.Build(notRunning, new DiscordSettings { HideWhenFortniteNotRunning = true }, T));
        Assert.NotNull(PresenceBuilder.Build(notRunning, new DiscordSettings(), T));
    }

    [Fact]
    public void Texts_respect_discord_length_limits()
    {
        var c = Solo() with { Nickname = new string('Ж', 200) + "😀😀" };

        var a = PresenceBuilder.Build(c, new DiscordSettings(), T)!;

        Assert.Equal(PresenceBuilder.MaxText, a.State.Length);
        Assert.EndsWith("…", a.State);
        Assert.Equal(2, PresenceBuilder.Fit("x").Length);
        Assert.False(char.IsHighSurrogate(PresenceBuilder.Fit(new string('a', 126) + "😀😀")[^2]));
    }

    [Fact]
    public void Project_button_always_links_the_fixed_repository()
    {
        var a = PresenceBuilder.Build(Solo(), new DiscordSettings(), T)!;

        var button = Assert.Single(a.Buttons);
        Assert.Equal("GitHub", button.Label);
        Assert.Equal("https://github.com/sonikx06/FNChestTracker/", button.Url);
        Assert.True(button.Url.Length <= PresenceBuilder.MaxButtonUrl);
    }

    [Fact]
    public void Project_link_in_old_settings_is_ignored()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-url-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, """{ "Discord": { "Enabled": true, "ProjectUrl": "https://evil.example/phish" } }""");
            var loaded = new SettingsStore(file).Load();
            Assert.True(loaded.Discord.Enabled);
            var a = PresenceBuilder.Build(Solo(), loaded.Discord, T)!;
            Assert.Equal(PresenceBuilder.ProjectUrl, Assert.Single(a.Buttons).Url);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Identical_activities_are_equal()
    {
        var s = new DiscordSettings();
        Assert.Equal(PresenceBuilder.Build(Solo(), s, T), PresenceBuilder.Build(Solo(), s, T));
        Assert.NotEqual(PresenceBuilder.Build(Solo(), s, T), PresenceBuilder.Build(Solo(13), s, T));
    }

    [Fact]
    public void Activity_json_uses_discord_field_names()
    {
        var a = PresenceBuilder.Build(Solo(), new DiscordSettings(), T)!;

        var json = DiscordIpcConnection.ToJson(a);

        Assert.Equal("Solo", json["details"]!.GetValue<string>());
        Assert.Equal(Start.ToUnixTimeMilliseconds(), json["timestamps"]!["start"]!.GetValue<long>());
        Assert.Equal("logo", json["assets"]!["large_image"]!.GetValue<string>());
        Assert.Equal("https://github.com/sonikx06/FNChestTracker/", json["buttons"]![0]!["url"]!.GetValue<string>());
    }
}

/// <summary>Speaks the Discord desktop RPC pipe protocol well enough to test the client.</summary>
public sealed class FakeDiscord : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private NamedPipeServerStream? _current;

    public FakeDiscord(string prefix, string? rejectWith = null)
    {
        Prefix = prefix;
        RejectWith = rejectWith;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string Prefix { get; }
    public string? RejectWith { get; }
    public ConcurrentQueue<JsonNode?> Activities { get; } = new();
    public int Handshakes;
    public ConcurrentQueue<string?> ClientIds { get; } = new();

    /// <summary>Drops the current client, like Discord restarting.</summary>
    public void Kick() => _current?.Dispose();

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(Prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 0, outBufferSize: 0); // unbuffered: any write-from-read-loop deadlock in the client shows up here
            _current = pipe;
            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token);
                await ServeAsync(pipe);
            }
            catch (Exception) when (!_cts.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await pipe.DisposeAsync();
            }
        }
    }

    private async Task ServeAsync(Stream pipe)
    {
        var (op, hello) = await Read(pipe);
        Assert.Equal(0, op);
        Interlocked.Increment(ref Handshakes);
        ClientIds.Enqueue(hello?["client_id"]?.GetValue<string>());
        if (RejectWith is not null)
        {
            await Write(pipe, 2, new JsonObject { ["code"] = 4000, ["message"] = RejectWith });
            return;
        }

        await Write(pipe, 1, new JsonObject
        {
            ["cmd"] = "DISPATCH", ["evt"] = "READY",
            ["data"] = new JsonObject { ["user"] = new JsonObject { ["username"] = "tester", ["id"] = "1" } },
        });
        await Write(pipe, 3, new JsonObject { ["ping"] = 1 }); // client must answer PONG

        while (!_cts.IsCancellationRequested)
        {
            var (fop, frame) = await Read(pipe);
            if (fop == 4) continue; // PONG
            if (frame?["cmd"]?.GetValue<string>() == "SET_ACTIVITY")
            {
                Activities.Enqueue(frame["args"]!["activity"]?.DeepClone());
                await Write(pipe, 1, new JsonObject { ["cmd"] = "SET_ACTIVITY", ["nonce"] = frame["nonce"]!.GetValue<string>(), ["evt"] = null });
            }
        }
    }

    private static async Task<(int, JsonNode?)> Read(Stream s)
    {
        var header = new byte[8];
        await s.ReadExactlyAsync(header);
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
        await s.ReadExactlyAsync(body);
        return (BinaryPrimitives.ReadInt32LittleEndian(header), body.Length == 0 ? null : JsonNode.Parse(body));
    }

    private static async Task Write(Stream s, int op, JsonNode payload)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await s.WriteAsync(frame);
        await s.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _current?.Dispose();
        try { await _loop; } catch { }
    }
}

public class DiscordPresenceServiceTests
{
    private static string NewPrefix() => $"ct-test-discord-{Guid.NewGuid():N}-";

    private static PresenceActivity Act(string state) => new() { Details = "Solo", State = state };

    private static async Task Until(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Missing_application_id_and_disabled_are_reported()
    {
        await using var svc = new DiscordPresenceService(NewPrefix());
        svc.Configure(enabled: true, applicationId: "");
        await Until(() => svc.Status == PresenceStatus.MissingApplicationId);
        svc.Configure(enabled: false, applicationId: "123");
        await Until(() => svc.Status == PresenceStatus.Disabled);
    }

    [Fact]
    public async Task Discord_not_running_is_handled_and_connects_when_it_starts()
    {
        var prefix = NewPrefix();
        await using var svc = new DiscordPresenceService(prefix, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));
        svc.Configure(true, "123456789012345678");
        svc.Update(Act("first"));
        await Until(() => svc.Status == PresenceStatus.DiscordNotRunning);

        await using var discord = new FakeDiscord(prefix);
        await Until(() => discord.Activities.Count == 1);

        Assert.Equal(PresenceStatus.Connected, svc.Status);
        Assert.Equal("tester", svc.ConnectedUser);
        Assert.Equal("first", discord.Activities.Single()!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rapid_changes_are_throttled_and_the_latest_wins()
    {
        var prefix = NewPrefix();
        await using var discord = new FakeDiscord(prefix);
        await using var svc = new DiscordPresenceService(prefix, TimeSpan.FromMilliseconds(600));
        svc.Configure(true, "123456789012345678");
        svc.Update(Act("0"));
        await Until(() => discord.Activities.Count == 1);

        for (var i = 1; i <= 20; i++)
        {
            svc.Update(Act(i.ToString()));
            await Task.Delay(10);
        }

        await Until(() => discord.Activities.LastOrDefault()?["state"]?.GetValue<string>() == "20");
        Assert.True(discord.Activities.Count <= 3, $"sent {discord.Activities.Count} updates");

        var before = discord.Activities.Count;
        svc.Update(Act("20")); // unchanged: nothing sent
        await Task.Delay(800);
        Assert.Equal(before, discord.Activities.Count);
    }

    [Fact]
    public async Task Reconnects_after_discord_restarts_and_resends_presence()
    {
        var prefix = NewPrefix();
        await using var discord = new FakeDiscord(prefix);
        await using var svc = new DiscordPresenceService(prefix, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));
        svc.Configure(true, "123456789012345678");
        svc.Update(Act("kept"));
        await Until(() => discord.Activities.Count == 1);

        discord.Kick();

        await Until(() => discord.Handshakes >= 2 && discord.Activities.Count >= 2);
        Assert.Equal("kept", discord.Activities.Last()!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Clearing_sends_null_activity()
    {
        var prefix = NewPrefix();
        await using var discord = new FakeDiscord(prefix);
        await using var svc = new DiscordPresenceService(prefix, TimeSpan.FromMilliseconds(50));
        svc.Configure(true, "123456789012345678");
        svc.Update(Act("visible"));
        await Until(() => discord.Activities.Count == 1);

        svc.Update(null);

        try
        {
            await Until(() => discord.Activities.Count == 2);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"status={svc.Status} error={svc.LastError} sent={svc.UpdatesSent} received={discord.Activities.Count} handshakes={discord.Handshakes} last={discord.Activities.LastOrDefault()?.ToJsonString()}");
        }

        Assert.Null(discord.Activities.Last());
    }

    [Fact]
    public async Task Rejected_application_id_reports_error()
    {
        var prefix = NewPrefix();
        await using var discord = new FakeDiscord(prefix, rejectWith: "Invalid Client ID");
        await using var svc = new DiscordPresenceService(prefix, retryInterval: TimeSpan.FromSeconds(30));
        svc.Configure(true, "1");

        await Until(() => svc.Status == PresenceStatus.Error);
        Assert.Equal("Invalid Client ID", svc.LastError);
    }
}
