using System.Net;
using System.Text;
using ChestTracker.Core.Models;
using ChestTracker.Core.Presence;
using ChestTracker.Core.Settings;

namespace ChestTracker.Tests;

/// <summary>Fixed Discord application: ID, asset keys and GitHub button cannot come from settings.</summary>
public class DiscordFixedAppTests
{
    private static readonly PresenceTexts T = new();

    private static PresenceContext Ctx(TeamPresence? team = null) => new()
    {
        MatchCount = 12, LifetimeCount = 300, MatchStartedAt = DateTimeOffset.UtcNow, Nickname = "User", Team = team,
    };

    [Fact]
    public void Constants_have_the_expected_values()
    {
        Assert.Equal("1569001762663440454", DiscordApp.ApplicationId);
        Assert.Equal("https://github.com/sonikx06/FNChestTracker/", DiscordApp.ProjectUrl);
        Assert.Equal("GitHub", DiscordApp.ButtonLabel);
        Assert.Equal(["logo", "solo", "team"], DiscordApp.RequiredAssets);
    }

    [Fact]
    public void Activity_uses_discord_asset_keys_by_mode_and_the_fixed_button()
    {
        var solo = PresenceBuilder.Build(Ctx(), new DiscordSettings(), T)!;
        Assert.Equal("logo", solo.LargeImage);
        Assert.Equal("solo", solo.SmallImage);

        var team = PresenceBuilder.Build(Ctx(new TeamPresence(2, 4, 5, 20)), new DiscordSettings(), T)!;
        Assert.Equal("logo", team.LargeImage);
        Assert.Equal("team", team.SmallImage);

        // Team info hidden → solo badge.
        Assert.Equal("solo", PresenceBuilder.Build(Ctx(new TeamPresence(2, 4, 5, 20)), new DiscordSettings { ShowTeam = false }, T)!.SmallImage);

        var json = DiscordIpcConnection.ToJson(team);
        Assert.Equal("logo", json["assets"]!["large_image"]!.GetValue<string>());
        Assert.Equal("team", json["assets"]!["small_image"]!.GetValue<string>());
        Assert.Equal("GitHub", json["buttons"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("https://github.com/sonikx06/FNChestTracker/", json["buttons"]![0]!["url"]!.GetValue<string>());
        Assert.DoesNotContain(":\\", json.ToJsonString()); // no local file paths
    }

    [Theory]
    [InlineData(TrackingMode.Manual)]
    [InlineData(TrackingMode.Hybrid)]
    [InlineData(TrackingMode.Automatic)]
    public void Small_icon_tooltip_is_always_the_english_app_name(TrackingMode mode)
    {
        var russian = T with { Solo = "Соло", MatchChests = "{0} сундуков за матч", AppName = "Трекер" };
        foreach (var ctx in new[] { Ctx() with { Mode = mode }, Ctx(new TeamPresence(2, 4, 5, 20)) with { Mode = mode } })
        {
            var a = PresenceBuilder.Build(ctx, new DiscordSettings(), russian)!;
            Assert.Equal("Fortnite Chest Tracker", a.SmallText);
            var assets = DiscordIpcConnection.ToJson(a)["assets"]!;
            Assert.Equal("Fortnite Chest Tracker", assets["small_text"]!.GetValue<string>());
            Assert.DoesNotContain("Tracking", assets.ToJsonString());
        }
    }

    [Fact]
    public void Large_image_tooltip_is_unchanged()
    {
        Assert.Equal("Chest Tracker", PresenceBuilder.Build(Ctx(), new DiscordSettings(), T)!.LargeText);
        Assert.Equal("Chest Tracker • 300 chests total",
            PresenceBuilder.Build(Ctx(), new DiscordSettings { ShowLifetime = true }, T)!.LargeText);
    }

    [Fact]
    public async Task Button_stays_in_every_update_after_match_start_end_and_stat_changes()
    {
        var prefix = $"ct-test-discord-{Guid.NewGuid():N}-";
        await using var discord = new FakeDiscord(prefix);
        await using var svc = new DiscordPresenceService(prefix, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200));
        svc.Configure(true, DiscordApp.ApplicationId);

        var start = DateTimeOffset.UtcNow;
        PresenceContext[] steps =
        [
            new() { Nickname = "User", LifetimeCount = 10 },                                       // lobby
            new() { Nickname = "User", LifetimeCount = 10, MatchStartedAt = start },               // match started
            new() { Nickname = "User", LifetimeCount = 11, MatchCount = 1, MatchStartedAt = start }, // chest
            new() { Nickname = "User", LifetimeCount = 11, MatchCount = 1, MatchStartedAt = start, Team = new TeamPresence(2, 4, 3, 30) },
            new() { Nickname = "User", LifetimeCount = 11 },                                       // match ended
        ];
        foreach (var step in steps)
        {
            svc.Update(PresenceBuilder.Build(step, new DiscordSettings(), T));
            var expected = discord.Activities.Count + 1;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (discord.Activities.Count < expected && DateTime.UtcNow < deadline) await Task.Delay(20);
        }

        Assert.Equal(steps.Length, discord.Activities.Count);
        foreach (var activity in discord.Activities)
        {
            var button = Assert.Single(activity!["buttons"]!.AsArray())!;
            Assert.Equal("GitHub", button["label"]!.GetValue<string>());
            Assert.Equal("https://github.com/sonikx06/FNChestTracker/", button["url"]!.GetValue<string>());
            Assert.Equal("Fortnite Chest Tracker", activity["assets"]!["small_text"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Discord_reply_with_buttons_is_described()
    {
        var reply = System.Text.Json.Nodes.JsonNode.Parse("""
            { "buttons": ["GitHub"], "metadata": { "button_urls": ["https://github.com/sonikx06/FNChestTracker/"] } }
            """);
        Assert.Equal("GitHub → https://github.com/sonikx06/FNChestTracker/", DiscordIpcConnection.DescribeButtons(reply));
        Assert.Equal("none", DiscordIpcConnection.DescribeButtons(System.Text.Json.Nodes.JsonNode.Parse("{}")));
    }

    [Fact]
    public void Button_label_is_fixed_even_with_translated_texts()
    {
        var a = PresenceBuilder.Build(Ctx(), new DiscordSettings(), T with { ButtonLabel = "Something else" })!;
        Assert.Equal(new PresenceButton("GitHub", DiscordApp.ProjectUrl), Assert.Single(a.Buttons));
    }

    [Fact]
    public void Old_saved_id_url_and_image_keys_never_override_the_constants()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-discord-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, """
                {
                  "Discord": {
                    "Enabled": true, "ApplicationId": "111111111111111111", "ShowNickname": true, "Nickname": "Local",
                    "ShowLifetime": true, "ShowTeam": false, "Priority": "LifetimeChests",
                    "ProjectUrl": "https://example.com/other", "LargeImageKey": "C:\\images\\logo.png",
                    "SoloImageKey": "", "TeamImageKey": "x"
                  }
                }
                """, Encoding.UTF8);

            var store = new SettingsStore(file);
            var loaded = store.Load();
            // Unrelated options are preserved.
            Assert.True(loaded.Discord.ShowLifetime);
            Assert.False(loaded.Discord.ShowTeam);
            Assert.Equal("Local", loaded.Discord.Nickname);
            Assert.Equal(PresencePriority.LifetimeChests, loaded.Discord.Priority);

            var a = PresenceBuilder.Build(Ctx(), loaded.Discord, T)!;
            Assert.Equal("logo", a.LargeImage);
            Assert.Equal("solo", a.SmallImage);
            Assert.Equal(DiscordApp.ProjectUrl, Assert.Single(a.Buttons).Url);

            // Saving drops the obsolete keys from the file.
            store.Save(loaded);
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("ApplicationId", text);
            Assert.DoesNotContain("ProjectUrl", text);
            Assert.DoesNotContain("ImageKey", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Presence_service_handshakes_with_the_fixed_application_id()
    {
        var prefix = $"ct-test-discord-{Guid.NewGuid():N}-";
        await using var discord = new FakeDiscord(prefix);
        await using var svc = new DiscordPresenceService(prefix, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));
        svc.Configure(true, DiscordApp.ApplicationId);
        svc.Update(PresenceBuilder.Build(Ctx(), new DiscordSettings(), T));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (discord.Activities.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(25);

        Assert.Equal(DiscordApp.ApplicationId, Assert.Single(discord.ClientIds));
        var activity = Assert.Single(discord.Activities)!;
        Assert.Equal("logo", activity["assets"]!["large_image"]!.GetValue<string>());
        Assert.Equal(DiscordApp.ProjectUrl, activity["buttons"]![0]!["url"]!.GetValue<string>());
        Assert.Equal(PresenceStatus.Connected, svc.Status);
    }
}

public class DiscordAssetCheckerTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task All_assets_present()
    {
        var h = new FakeHandler(_ => Json("""[{"id":"1","type":1,"name":"team"},{"id":"2","type":1,"name":"solo"},{"id":"3","type":1,"name":"logo"}]"""));
        var r = await new DiscordAssetChecker(h).CheckAsync(DiscordApp.ApplicationId, DiscordApp.RequiredAssets);

        Assert.Equal(AssetCheckStatus.Ok, r.Status);
        Assert.Empty(r.Missing);
        var req = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://discord.com/api/v10/oauth2/applications/1569001762663440454/assets", req.RequestUri!.ToString());
        Assert.Null(req.Content); // nothing but the public application ID is sent
        Assert.Null(req.Headers.Authorization);
    }

    [Fact]
    public async Task Missing_assets_are_named()
    {
        var h = new FakeHandler(_ => Json("""[{"id":"3","type":1,"name":"Logo"},{"id":"2","type":1,"name":"solo"}]"""));
        var r = await new DiscordAssetChecker(h).CheckAsync(DiscordApp.ApplicationId, DiscordApp.RequiredAssets);

        Assert.Equal(AssetCheckStatus.Missing, r.Status);
        Assert.Equal(["logo", "team"], r.Missing); // keys are case-sensitive
    }

    [Fact]
    public async Task Errors_are_reported_as_unavailable()
    {
        var down = await new DiscordAssetChecker(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
            .CheckAsync(DiscordApp.ApplicationId, DiscordApp.RequiredAssets);
        Assert.Equal(AssetCheckStatus.Unavailable, down.Status);
        Assert.Equal("HTTP 503", down.Error);

        var offline = await new DiscordAssetChecker(new FakeHandler(_ => throw new HttpRequestException("no network")))
            .CheckAsync(DiscordApp.ApplicationId, DiscordApp.RequiredAssets);
        Assert.Equal(AssetCheckStatus.Unavailable, offline.Status);

        var garbage = await new DiscordAssetChecker(new FakeHandler(_ => Json("not json")))
            .CheckAsync(DiscordApp.ApplicationId, DiscordApp.RequiredAssets);
        Assert.Equal(AssetCheckStatus.Unavailable, garbage.Status);
    }
}
