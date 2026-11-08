using System.Globalization;
using ChestTracker.Core.Models;
using ChestTracker.Core.Settings;

namespace ChestTracker.Core.Presence;

/// <summary>One Discord activity, already clamped to Discord's field limits.</summary>
public sealed record PresenceActivity
{
    public required string Details { get; init; }
    public required string State { get; init; }
    public DateTimeOffset? Start { get; init; }
    public string? LargeImage { get; init; }
    public string? LargeText { get; init; }
    public string? SmallImage { get; init; }
    public string? SmallText { get; init; }
    public IReadOnlyList<PresenceButton> Buttons { get; init; } = [];

    // Records compare lists by reference; compare buttons by content so identical activities are not re-sent.
    public bool Equals(PresenceActivity? other) =>
        other is not null && Details == other.Details && State == other.State && Start == other.Start
        && LargeImage == other.LargeImage && LargeText == other.LargeText && SmallImage == other.SmallImage
        && SmallText == other.SmallText && Buttons.SequenceEqual(other.Buttons);

    public override int GetHashCode() => HashCode.Combine(Details, State, Start, LargeImage, SmallImage);
}

public sealed record PresenceButton(string Label, string Url);

/// <summary>Everything the presence text is built from.</summary>
public sealed record PresenceContext
{
    public int MatchCount { get; init; }
    public int SessionCount { get; init; }
    public int LifetimeCount { get; init; }
    public DateTimeOffset? MatchStartedAt { get; init; }
    public TrackingMode Mode { get; init; }
    public string? Nickname { get; init; }

    /// <summary>Null when not in a party.</summary>
    public TeamPresence? Team { get; init; }

    public bool FortniteRunning { get; init; } = true;
}

public sealed record TeamPresence(int Players, int MaxPlayers, int? TeamMatchChests, int TeamLifetimeChests);

/// <summary>Localizable templates; {0} placeholders as documented per property.</summary>
public sealed record PresenceTexts
{
    public string Solo { get; init; } = "Solo";
    /// <summary>{0} players, {1} max.</summary>
    public string Team { get; init; } = "Team • {0}/{1} players";
    /// <summary>{0} count.</summary>
    public string MatchChests { get; init; } = "{0} chests this match";
    public string SessionChests { get; init; } = "{0} chests this session";
    public string LifetimeChests { get; init; } = "{0} chests total";
    public string TeamMatchChests { get; init; } = "Team: {0} chests this match";
    public string TeamLifetimeChests { get; init; } = "Team: {0} chests total";
    public string InLobby { get; init; } = "In lobby";
    /// <summary>{0} lifetime count.</summary>
    public string LifetimeTooltip { get; init; } = "{0} chests lifetime";
    /// <summary>{0} mode name.</summary>
    public string ModeTooltip { get; init; } = "Tracking: {0}";
    public string ModeManual { get; init; } = "manual";
    public string ModeAutomatic { get; init; } = "automatic";
    public string ModeHybrid { get; init; } = "hybrid";
    public string ButtonLabel { get; init; } = "GitHub";
    public string AppName { get; init; } = "Chest Tracker";
}

/// <summary>
/// Pure mapping from tracker state to a Discord activity. Discord limits (Activity object / RPC SET_ACTIVITY):
/// details, state, large_text and small_text are 2–128 characters; asset keys ≤ 256; at most 2 buttons with
/// labels of 1–32 characters and URLs of up to 512 characters.
/// </summary>
public static class PresenceBuilder
{
    public const int MaxText = 128;
    public const int MinText = 2;
    public const int MaxButtonLabel = 32;
    public const int MaxButtonUrl = 512;
    public const int MaxAssetKey = 256;
    public const int MaxButtons = 2;

    /// <summary>The project page shown as the presence button. Fixed: it is not a setting.</summary>
    public const string ProjectUrl = "https://github.com/sonikx06/FNChestTracker";

    /// <returns>The activity to show, or null when the presence should be cleared.</returns>
    public static PresenceActivity? Build(PresenceContext c, DiscordSettings s, PresenceTexts t)
    {
        if (!s.Enabled || (s.HideWhenFortniteNotRunning && !c.FortniteRunning))
        {
            return null;
        }

        var inTeam = s.ShowTeam && c.Team is not null;
        var details = inTeam ? string.Format(CultureInfo.InvariantCulture, t.Team, c.Team!.Players, c.Team.MaxPlayers) : t.Solo;

        var stat = PrimaryStat(c, s.Priority, inTeam, t);
        var nickname = s.ShowNickname ? Clean(c.Nickname) : null;
        var state = string.IsNullOrEmpty(nickname) ? stat : $"{nickname} • {stat}";

        var mode = c.Mode switch
        {
            TrackingMode.Automatic => t.ModeAutomatic,
            TrackingMode.Hybrid => t.ModeHybrid,
            _ => t.ModeManual,
        };

        var largeText = s.ShowLifetime
            ? $"{t.AppName} • {string.Format(CultureInfo.InvariantCulture, t.LifetimeChests.Replace("{0}", "{0:N0}"), c.LifetimeCount)}"
            : t.AppName;

        return new PresenceActivity
        {
            Details = Fit(details),
            State = Fit(state),
            Start = s.ShowTimer ? c.MatchStartedAt : null,
            LargeImage = AssetKey(s.LargeImageKey),
            LargeText = Fit(largeText),
            SmallImage = AssetKey(inTeam ? s.TeamImageKey : s.SoloImageKey),
            SmallText = Fit(string.Format(CultureInfo.InvariantCulture, t.ModeTooltip, mode)),
            Buttons = Buttons(t),
        };
    }

    private static string PrimaryStat(PresenceContext c, PresencePriority priority, bool inTeam, PresenceTexts t)
    {
        string F(string template, int n) => string.Format(CultureInfo.InvariantCulture, template, n);

        switch (priority)
        {
            case PresencePriority.LifetimeChests:
#pragma warning disable CS0618 // saved settings from older versions
            case PresencePriority.SessionChests:
#pragma warning restore CS0618
                return F(t.LifetimeChests, c.LifetimeCount);
            case PresencePriority.TeamChests when inTeam:
                return c.Team!.TeamMatchChests is { } tm ? F(t.TeamMatchChests, tm) : F(t.TeamLifetimeChests, c.Team.TeamLifetimeChests);
            default:
                // Match chests; outside a match show the all-time total instead of a stale "0 this match".
                return c.MatchStartedAt is null ? $"{t.InLobby} • {F(t.LifetimeChests, c.LifetimeCount)}" : F(t.MatchChests, c.MatchCount);
        }
    }

    private static IReadOnlyList<PresenceButton> Buttons(PresenceTexts t)
    {
        var label = Truncate(Clean(t.ButtonLabel) ?? "GitHub", MaxButtonLabel);
        return label.Length == 0 ? [] : [new PresenceButton(label, ProjectUrl)];
    }

    private static string? AssetKey(string? key)
    {
        var k = key?.Trim();
        return string.IsNullOrEmpty(k) || k.Length > MaxAssetKey ? null : k;
    }

    /// <summary>Clamps to 2–128 characters without splitting surrogate pairs.</summary>
    public static string Fit(string text)
    {
        var s = Truncate(Clean(text) ?? "", MaxText);
        // Discord rejects strings shorter than 2 characters.
        return s.Length >= MinText ? s : (s + "  ")[..MinText];
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max)
        {
            return s;
        }

        var cut = max - 1;
        if (char.IsHighSurrogate(s[cut - 1]))
        {
            cut--;
        }

        return s[..cut] + "…";
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        return new string(s.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
    }
}
