using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ChestTracker.Core.Presence;

/// <summary>
/// Fixed Discord application of Chest Tracker. These are constants on purpose: they are not settings, so nothing
/// stored in settings.json (older versions saved an editable ID, URL and image keys) can override them.
/// </summary>
public static class DiscordApp
{
    /// <summary>Application ID from the Discord Developer Portal (public, not a secret).</summary>
    public const string ApplicationId = "1569001762663440454";

    /// <summary>Project page shown as the presence button.</summary>
    public const string ProjectUrl = "https://github.com/sonikx06/FNChestTracker/";

    public const string ButtonLabel = "GitHub";

    /// <summary>Tooltip of the small solo/team icon. Always English, never translated, independent of the tracking mode.</summary>
    public const string SmallImageText = "Fortnite Chest Tracker";

    // Art asset keys (names) uploaded under Rich Presence → Art Assets of the application above.
    // Discord resolves these names on its side; local file paths are never sent.
    public const string LargeImageKey = "logo";
    public const string SoloImageKey = "solo";
    public const string TeamImageKey = "team";

    public static IReadOnlyList<string> RequiredAssets { get; } = [LargeImageKey, SoloImageKey, TeamImageKey];
}

public enum AssetCheckStatus
{
    /// <summary>Not checked yet.</summary>
    Unknown,

    /// <summary>All required art assets exist in the Discord application.</summary>
    Ok,

    /// <summary>Some required asset keys are not uploaded: Discord shows a "?" placeholder for them.</summary>
    Missing,

    /// <summary>The asset list could not be loaded (offline, Discord API down).</summary>
    Unavailable,
}

public sealed record AssetCheckResult(AssetCheckStatus Status, IReadOnlyList<string> Missing, IReadOnlyList<string> Found, string? Error = null);

/// <summary>
/// Checks that the art asset keys used by the presence exist in the Discord application, using Discord's public
/// asset list (GET /api/v10/oauth2/applications/{id}/assets). Only the public application ID is sent; no user data,
/// tokens or credentials.
/// </summary>
public sealed class DiscordAssetChecker(HttpMessageHandler? handler = null)
{
    private static readonly Uri Api = new("https://discord.com/api/v10/");

    public async Task<AssetCheckResult> CheckAsync(string applicationId, IReadOnlyList<string> required, CancellationToken ct = default)
    {
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.BaseAddress = Api;
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChestTracker (https://github.com/sonikx06/FNChestTracker, 1)");
            using var response = await http.GetAsync($"oauth2/applications/{Uri.EscapeDataString(applicationId)}/assets", ct);
            if (!response.IsSuccessStatusCode)
            {
                return new AssetCheckResult(AssetCheckStatus.Unavailable, [], [], $"HTTP {(int)response.StatusCode}");
            }

            var assets = await response.Content.ReadFromJsonAsync<List<AssetDto>>(cancellationToken: ct) ?? [];
            var names = assets.Select(a => a.Name ?? "").Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
            var missing = required.Where(r => !names.Contains(r)).ToList();
            return new AssetCheckResult(missing.Count == 0 ? AssetCheckStatus.Ok : AssetCheckStatus.Missing, missing,
                names.OrderBy(n => n, StringComparer.Ordinal).ToList());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return new AssetCheckResult(AssetCheckStatus.Unavailable, [], [], ex.Message);
        }
    }

    private sealed record AssetDto([property: JsonPropertyName("name")] string? Name);
}
