using System.Globalization;
using System.Text;

namespace ChestTracker.Vision;

/// <summary>What the local player is doing, as far as the screen shows it.</summary>
public enum GameplayState
{
    /// <summary>Not determined yet (match just started, or the screen check is unavailable). Nothing is counted.</summary>
    Unknown,

    /// <summary>Local Start/control confirmation followed by a stable readable HUD without a spectator title.</summary>
    Playing,

    /// <summary>Fortnite shows an English or Russian spectator title; automatic counting is blocked.</summary>
    Spectating,
}

/// <summary>Looks at a frame and says whether Fortnite's spectator banner is visible (null: could not tell).</summary>
public interface ISpectatorProbe
{
    /// <summary>False when the check cannot run on this PC (for example no OCR language installed).</summary>
    bool Available { get; }

    bool? IsSpectating(ReadOnlySpan<byte> bgra, int width, int height, int stride);
}

/// <summary>
/// Where the spectator banner is and what it says. Fortnite draws "SPECTATING" above the watched player's name, centred
/// at the top of the screen (checked on recorded gameplay at 1080p).
/// </summary>
public static class SpectatorBanner
{
    /// <summary>Top-centre title band, excluding chat, menus and most of the watched player's name.</summary>
    public static (int X, int Y, int Width, int Height) Region(int width, int height)
    {
        var x = (int)(width * 0.25);
        var w = (int)(width * 0.50);
        var h = Math.Max(1, (int)(height * 0.045));
        return (x, 0, Math.Max(1, w), h);
    }

    /// <summary>Russian title transcribed from the supplied 2026-11-15 gameplay screenshot; English from prior OCR fixtures.</summary>
    public static readonly IReadOnlyList<string> Titles = ["SPECTATING", "РЕЖИМ ЗРИТЕЛЯ"];

    /// <summary>Production OCR uses individual title lines, never a substring of a nickname or arbitrary screen text.</summary>
    public static bool MatchesTitleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToArray();
        return (words.Length == 1 && words[0].Length >= 9 && Distance(words[0], "SPECTATING") <= 1)
            || (words.Length == 2 && Distance(words[0], "РЕЖИМ") <= 1 && Distance(words[1], "ЗРИТЕЛЯ") <= 1);
    }

    /// <summary>
    /// True when OCR text contains a banner title. Tolerates typical OCR slips (one or two wrong letters, I/l/1 and
    /// O/0 confusion) but needs at least 8 letters, so ordinary HUD text never matches.
    /// </summary>
    public static bool Matches(string? ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return false;
        }

        if (ocrText.Split(['\r', '\n']).Any(MatchesTitleLine)) return true;

        foreach (var raw in ocrText.Split([' ', '\n', '\r', '\t', '—', '-', ':', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = Normalize(raw);
            if (word.Length < 8)
            {
                continue;
            }

            foreach (var title in Titles)
            {
                if (Math.Abs(word.Length - title.Length) <= 1 && Distance(word, title) <= 2)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToUpper(CultureInfo.InvariantCulture))
        {
            var c = ch switch
            {
                '1' or 'L' or '|' or '!' => 'I',
                '0' => 'O',
                '5' => 'S',
                _ => ch,
            };
            if (char.IsLetter(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}

public sealed record GameplayStateOptions
{
    /// <summary>Banner sightings in a row that switch to Spectating. One: never count while the banner may be up.</summary>
    public int SpectatingAfter { get; init; } = 1;

    /// <summary>Clean frames in a row (no banner) needed to switch to Playing, e.g. 16 frames ≈ 2 s at 8 FPS.</summary>
    public int PlayingAfter { get; init; } = 16;

    /// <summary>...and at least this long without a sighting (protects against a few missed OCR reads).</summary>
    public TimeSpan PlayingAfterTime { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Without a successful check for this long the state falls back to Unknown (nothing counts).</summary>
    public TimeSpan UnknownAfter { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Turns per-frame checks into a stable state. One sighting stops counting immediately. After spectator evidence
/// or prolonged unavailable checks, the user must confirm regained control, followed by a clean stretch of frames.
/// Missing title text alone never clears that latch. Starting a new local match is an explicit user action.
/// </summary>
public sealed class GameplayStateTracker(GameplayStateOptions? options = null)
{
    private readonly GameplayStateOptions _o = options ?? new GameplayStateOptions();
    private int _positives;
    private int _negatives;
    private DateTimeOffset? _lastSighting;
    private DateTimeOffset? _firstClean;
    private DateTimeOffset? _lastCheck;
    private bool _requiresControlConfirmation;

    public bool RequiresControlConfirmation => _requiresControlConfirmation;

    /// <summary>Explicit user confirmation after reboot/control recovery; never starts a local or shared match.</summary>
    public void ConfirmLocalControl()
    {
        _requiresControlConfirmation = false;
        State = GameplayState.Unknown;
        _positives = _negatives = 0;
        _lastSighting = _firstClean = _lastCheck = null;
    }

    public GameplayState State { get; private set; } = GameplayState.Unknown;

    /// <summary>Applies one frame's check; returns true when the state changed.</summary>
    public bool Observe(bool? spectating, DateTimeOffset time)
    {
        var before = State;
        if (spectating is null)
        {
            if (_lastCheck is { } last && time - last > _o.UnknownAfter && State == GameplayState.Playing)
            {
                State = GameplayState.Unknown;
                _requiresControlConfirmation = true;
                _negatives = 0;
                _firstClean = null;
            }

            return State != before;
        }

        _lastCheck = time;
        if (spectating.Value)
        {
            _requiresControlConfirmation = true;
            _positives++;
            _negatives = 0;
            _firstClean = null;
            _lastSighting = time;
            if (_positives >= _o.SpectatingAfter)
            {
                State = GameplayState.Spectating;
            }
        }
        else
        {
            _positives = 0;
            _negatives++;
            _firstClean ??= time;
            var cleanFor = time - (_lastSighting is { } s && s > _firstClean ? s : _firstClean.Value);
            if (!_requiresControlConfirmation && State != GameplayState.Playing && _negatives >= _o.PlayingAfter && cleanFor >= _o.PlayingAfterTime)
            {
                State = GameplayState.Playing;
            }
        }

        return State != before;
    }

    /// <summary>New match: nothing is known about the screen yet.</summary>
    public void Reset()
    {
        State = GameplayState.Unknown;
        _requiresControlConfirmation = false;
        _positives = _negatives = 0;
        _lastSighting = _firstClean = _lastCheck = null;
    }
}
