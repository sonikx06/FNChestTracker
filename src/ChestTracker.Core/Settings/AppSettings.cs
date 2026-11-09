using ChestTracker.Core.Models;
using ChestTracker.Core.Tracking;

namespace ChestTracker.Core.Settings;

public enum HotkeyAction
{
    AddChest,
    RemoveLastChest,
    StartMatch,
    EndMatch,
    ToggleOverlay,
    ToggleOverlayEditMode,
    ToggleTeamOverlay,
}

/// <summary>Values match the Win32 MOD_* flags used by RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

public enum OverlayLayout
{
    Compact,
    Standard,
    Expanded,
}

public enum CaptureBackend
{
    /// <summary>Windows.Graphics.Capture (Windows 10 1903+).</summary>
    WindowsGraphicsCapture,

    /// <summary>GDI screen copy fallback (borderless windowed only).</summary>
    GdiScreen,
}

public enum DetectionEngine
{
    /// <summary>Gold/brightness burst around the crosshair (OpenCV, no model needed).</summary>
    Heuristic,

    /// <summary>YOLOv8 object detection with per-chest tracking (needs a trained ONNX model).</summary>
    Yolo,
}

public enum CaptureTargetKind
{
    /// <summary>Find the Fortnite window by process name.</summary>
    AutoFortnite,
    PrimaryMonitor,
}

public sealed class HotkeyAssignment
{
    public HotkeyAction Action { get; set; }

    /// <summary>Win32 virtual-key code, 0 = not bound.</summary>
    public int VirtualKey { get; set; }

    public HotkeyModifiers Modifiers { get; set; }

    public bool IsEmpty => VirtualKey == 0;

    /// <summary>Mouse4/Mouse5 (handled through Raw Input instead of RegisterHotKey).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMouseButton => MouseButtons.IsMouseButton(VirtualKey);

    public bool SameKeyAs(HotkeyAssignment other) =>
        !IsEmpty && VirtualKey == other.VirtualKey && Modifiers == other.Modifiers;

    public HotkeyAssignment Clone() => new() { Action = Action, VirtualKey = VirtualKey, Modifiers = Modifiers };
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool SoundsEnabled { get; set; } = true;
    public double SoundVolume { get; set; } = 0.5;
}

public sealed class TrackingSettings
{
    public bool AutoStartMatch { get; set; } = true;
    public int ManualDebounceMs { get; set; } = 300;
    public double HybridMergeWindowSeconds { get; set; } = 2.5;
    public int ResumeWindowMinutes { get; set; } = 10;
}

public sealed class HotkeySettings
{
    public bool Enabled { get; set; } = true;
    public List<HotkeyAssignment> Bindings { get; set; } = [];

    public HotkeyAssignment Get(HotkeyAction action) =>
        Bindings.FirstOrDefault(b => b.Action == action) ?? new HotkeyAssignment { Action = action };

    public static List<HotkeyAssignment> Defaults() =>
    [
        new() { Action = HotkeyAction.AddChest, VirtualKey = 0x77 },        // F8
        new() { Action = HotkeyAction.RemoveLastChest, VirtualKey = 0x76 }, // F7
        new() { Action = HotkeyAction.StartMatch, VirtualKey = 0x78 },      // F9
        new() { Action = HotkeyAction.EndMatch, VirtualKey = 0x79 },        // F10
        new() { Action = HotkeyAction.ToggleOverlay, VirtualKey = 0x75 },   // F6
        new() { Action = HotkeyAction.ToggleOverlayEditMode, VirtualKey = 0 },
        new() { Action = HotkeyAction.ToggleTeamOverlay, VirtualKey = 0 },
    ];
}

public sealed class OverlaySettings
{
    public bool Visible { get; set; } = true;
    public OverlayLayout Layout { get; set; } = OverlayLayout.Standard;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Opacity { get; set; } = 0.95;
    public double Scale { get; set; } = 1.0;
    public string Preset { get; set; } = "Neon";
    public string TextColor { get; set; } = "#FFFFFF";
    public string AccentColor { get; set; } = "#C084FC";
    public bool ShowBackground { get; set; } = true;
    public string BackgroundColor { get; set; } = "#140F22";
    public double BackgroundOpacity { get; set; } = 0.72;
    public bool ShowMatchTimer { get; set; } = true;
    public bool ShowDetectionStatus { get; set; } = true;

    /// <summary>Hide the overlay from screenshots/recordings (and from the detector's own screen capture).</summary>
    public bool ExcludeFromCapture { get; set; }
}

public sealed class DetectionSettings
{
    public int Sensitivity { get; set; } = 50;
    public double CooldownSeconds { get; set; } = 4;
    public double SamplingFps { get; set; } = 4;
    public CaptureBackend Backend { get; set; } = CaptureBackend.WindowsGraphicsCapture;
    public CaptureTargetKind Target { get; set; } = CaptureTargetKind.AutoFortnite;
    public string ProcessName { get; set; } = "FortniteClient-Win64-Shipping";

    /// <summary>Test mode: detections are shown but never counted.</summary>
    public bool TestMode { get; set; }

    public bool QueueUncertain { get; set; } = true;

    /// <summary>YOLOv8 ONNX model (default; the model ships with the app) or the experimental heuristic gold-burst detector.
    /// Only one engine runs at a time, so the two can never both count the same opening.</summary>
    public DetectionEngine Engine { get; set; } = DetectionEngine.Yolo;

    /// <summary>YOLOv8 model exported to ONNX (see tools/yolo). Empty = the bundled models/best.onnx next to the app.</summary>
    public string YoloModelPath { get; set; } = "";

    /// <summary>Minimum per-frame detection confidence for the YOLO model.</summary>
    public double YoloConfidence { get; set; } = 0.35;

    /// <summary>Draw boxes and chest states on the detection page preview.</summary>
    public bool DebugView { get; set; } = true;

    /// <summary>
    /// Writes YOLO tracking diagnostics to the log (state transitions, rejected detections, throttled). Off by default;
    /// set "DiagnosticLog": true in settings.json (no UI). Counted openings are always logged.
    /// </summary>
    public bool DiagnosticLog { get; set; }

    // Region of interest (relative to the frame) where the chest burst appears.
    public double RoiWidth { get; set; } = 0.34;
    public double RoiHeight { get; set; } = 0.40;
    public double RoiCenterY { get; set; } = 0.55;

    // Gold range in OpenCV HSV (H 0..180, S/V 0..255).
    public int GoldHueMin { get; set; } = 15;
    public int GoldHueMax { get; set; } = 34;
    public int GoldSaturationMin { get; set; } = 110;
    public int GoldValueMin { get; set; } = 170;

    /// <summary>Optional "Search" prompt template matching.</summary>
    public bool UseTemplate { get; set; }

    // Prompt region relative to the frame (left, top, width, height).
    public double PromptX { get; set; } = 0.52;
    public double PromptY { get; set; } = 0.49;
    public double PromptWidth { get; set; } = 0.20;
    public double PromptHeight { get; set; } = 0.12;
}

/// <summary>A server the user connected to, with this device's credential for it (if signed in).</summary>
public sealed class SavedServer
{
    public string Url { get; set; } = "";

    /// <summary>Device credential issued by that server (guest or account; never a password).</summary>
    public Guid? PlayerId { get; set; }

    /// <summary>Device secret encrypted for the current Windows user (DPAPI), base64.</summary>
    public string? ProtectedSecret { get; set; }

    public string DisplayName { get; set; } = "";

    /// <summary>Null for guest credentials.</summary>
    public string? Username { get; set; }

    public DateTimeOffset? LastConnected { get; set; }
}

public sealed class MultiplayerSettings
{
    /// <summary>Active server address. Empty until the user enters one; no address is built in.</summary>
    public string ServerUrl { get; set; } = "";

    public List<SavedServer> SavedServers { get; set; } = [];

    /// <summary>Connect to the active server at start-up (turned off by "Disconnect").</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>Legacy single-server credential (version 1.0); migrated into <see cref="SavedServers"/>.</summary>
    public Guid? PlayerId { get; set; }

    /// <summary>Legacy; see <see cref="PlayerId"/>.</summary>
    public string? ProtectedSecret { get; set; }

    public string DisplayName { get; set; } = "";
    public bool AutoShareMatches { get; set; } = true;
    public bool AutoJoinSharedMatches { get; set; } = true;
    public bool NotifyTeammateChests { get; set; } = true;
    /// <summary>No longer used: teammates' chests never play a sound (only the player who records a chest hears it). Kept so old settings files load.</summary>
    public bool TeammateSound { get; set; }
    public bool OverlayTeamMode { get; set; }
    public bool OverlayShowActivity { get; set; } = true;
}

public enum PresencePriority
{
    MatchChests,

    /// <summary>Replaced by <see cref="LifetimeChests"/>; kept so settings saved by 1.1 still load.</summary>
    [Obsolete("Session statistics were replaced by all-time statistics.")]
    SessionChests,
    LifetimeChests,
    TeamChests,
}

public sealed class DiscordSettings
{
    public bool Enabled { get; set; } = true;

    // The Application ID, project URL and art asset keys are fixed constants (DiscordApp), not settings.
    // Values saved by older versions ("ApplicationId", "ProjectUrl", "*ImageKey") are ignored when loading.

    public bool ShowNickname { get; set; } = true;

    /// <summary>Name shown in the status; empty = the party display name (if any).</summary>
    public string Nickname { get; set; } = "";

    public bool ShowLifetime { get; set; }
    public bool ShowTeam { get; set; } = true;
    public bool ShowTimer { get; set; } = true;
    public PresencePriority Priority { get; set; } = PresencePriority.MatchChests;
    public bool HideWhenFortniteNotRunning { get; set; }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public string Language { get; set; } = "ru";
    public TrackingMode TrackingMode { get; set; } = TrackingMode.Manual;
    public GeneralSettings General { get; set; } = new();
    public TrackingSettings Tracking { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public DetectionSettings Detection { get; set; } = new();
    public MultiplayerSettings Multiplayer { get; set; } = new();
    public DiscordSettings Discord { get; set; } = new();

    /// <summary>Repairs missing sections and clamps every value to a safe range.</summary>
    public AppSettings Normalize()
    {
        Discord ??= new DiscordSettings();
        Discord.Nickname ??= "";
        if (!Enum.IsDefined(Discord.Priority))
        {
            Discord.Priority = PresencePriority.MatchChests;
        }
#pragma warning disable CS0618
        else if (Discord.Priority == PresencePriority.SessionChests)
        {
            Discord.Priority = PresencePriority.LifetimeChests;
        }
#pragma warning restore CS0618

        Multiplayer ??= new MultiplayerSettings();
        Multiplayer.ServerUrl ??= "";
        Multiplayer.DisplayName ??= "";
        Multiplayer.SavedServers ??= [];
        Multiplayer.SavedServers.RemoveAll(s => s is null || string.IsNullOrWhiteSpace(s.Url));
        if (Multiplayer.PlayerId is { } legacyId && !string.IsNullOrEmpty(Multiplayer.ProtectedSecret)
            && !string.IsNullOrWhiteSpace(Multiplayer.ServerUrl))
        {
            var entry = Multiplayer.SavedServers.FirstOrDefault(s => string.Equals(s.Url, Multiplayer.ServerUrl, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new SavedServer { Url = Multiplayer.ServerUrl };
                Multiplayer.SavedServers.Add(entry);
            }

            entry.PlayerId = legacyId;
            entry.ProtectedSecret = Multiplayer.ProtectedSecret;
            entry.DisplayName = Multiplayer.DisplayName;
        }

        Multiplayer.PlayerId = null;
        Multiplayer.ProtectedSecret = null;
        General ??= new GeneralSettings();
        Tracking ??= new TrackingSettings();
        Hotkeys ??= new HotkeySettings();
        Overlay ??= new OverlaySettings();
        Detection ??= new DetectionSettings();
        Detection.YoloModelPath ??= "";
        Detection.YoloConfidence = double.IsFinite(Detection.YoloConfidence) ? Math.Clamp(Detection.YoloConfidence, 0.05, 0.95) : 0.35;
        if (!Enum.IsDefined(Detection.Engine))
        {
            Detection.Engine = DetectionEngine.Yolo;
        }

        Hotkeys.Bindings ??= [];

        if (Language is not ("ru" or "en"))
        {
            Language = "ru";
        }

        if (!Enum.IsDefined(TrackingMode))
        {
            TrackingMode = TrackingMode.Manual;
        }

        // Keep exactly one binding per action, adding defaults for new actions.
        var defaults = HotkeySettings.Defaults();
        Hotkeys.Bindings = Enum.GetValues<HotkeyAction>()
            .Select(a => Hotkeys.Bindings.FirstOrDefault(b => b.Action == a)?.Clone()
                         ?? defaults.First(d => d.Action == a))
            .ToList();

        General.SoundVolume = Math.Clamp(General.SoundVolume, 0, 1);
        Tracking.ManualDebounceMs = Math.Clamp(Tracking.ManualDebounceMs, 0, 2000);
        Tracking.HybridMergeWindowSeconds = Math.Clamp(Tracking.HybridMergeWindowSeconds, 0, 10);
        Tracking.ResumeWindowMinutes = Math.Clamp(Tracking.ResumeWindowMinutes, 0, 120);

        Overlay.Opacity = Math.Clamp(Overlay.Opacity, 0.15, 1);
        Overlay.Scale = Math.Clamp(Overlay.Scale, 0.5, 3);
        Overlay.BackgroundOpacity = Math.Clamp(Overlay.BackgroundOpacity, 0, 1);
        Overlay.TextColor = ValidColor(Overlay.TextColor, "#FFFFFF");
        Overlay.AccentColor = ValidColor(Overlay.AccentColor, "#C084FC");
        Overlay.BackgroundColor = ValidColor(Overlay.BackgroundColor, "#140F22");
        Overlay.Preset ??= "Custom";

        var d = Detection;
        d.Sensitivity = Math.Clamp(d.Sensitivity, 0, 100);
        d.CooldownSeconds = Math.Clamp(d.CooldownSeconds, 0.5, 30);
        d.SamplingFps = Math.Clamp(d.SamplingFps, 1, 15);
        d.RoiWidth = Math.Clamp(d.RoiWidth, 0.05, 1);
        d.RoiHeight = Math.Clamp(d.RoiHeight, 0.05, 1);
        d.RoiCenterY = Math.Clamp(d.RoiCenterY, 0, 1);
        d.GoldHueMin = Math.Clamp(d.GoldHueMin, 0, 180);
        d.GoldHueMax = Math.Clamp(d.GoldHueMax, d.GoldHueMin, 180);
        d.GoldSaturationMin = Math.Clamp(d.GoldSaturationMin, 0, 255);
        d.GoldValueMin = Math.Clamp(d.GoldValueMin, 0, 255);
        d.PromptWidth = Math.Clamp(d.PromptWidth, 0.02, 1);
        d.PromptHeight = Math.Clamp(d.PromptHeight, 0.02, 1);
        d.PromptX = Math.Clamp(d.PromptX, 0, 1 - d.PromptWidth);
        d.PromptY = Math.Clamp(d.PromptY, 0, 1 - d.PromptHeight);
        if (string.IsNullOrWhiteSpace(d.ProcessName))
        {
            d.ProcessName = "FortniteClient-Win64-Shipping";
        }

        return this;
    }

    public TrackingOptions ToTrackingOptions() => new()
    {
        ManualDebounce = TimeSpan.FromMilliseconds(Tracking.ManualDebounceMs),
        AutomaticCooldown = TimeSpan.FromSeconds(Detection.CooldownSeconds),
        HybridMergeWindow = TimeSpan.FromSeconds(Tracking.HybridMergeWindowSeconds),
        AutoStartMatch = Tracking.AutoStartMatch,
        ResumeWindow = TimeSpan.FromMinutes(Tracking.ResumeWindowMinutes),
    };

    private static string ValidColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var v = value.Trim();
        if (!v.StartsWith('#'))
        {
            v = "#" + v;
        }

        return (v.Length is 7 or 9) && v.Skip(1).All(Uri.IsHexDigit) ? v.ToUpperInvariant() : fallback;
    }
}

public static class HotkeyConflicts
{
    /// <summary>Actions whose non-empty key combination is also bound to another action.</summary>
    public static IReadOnlySet<HotkeyAction> Find(IEnumerable<HotkeyAssignment> bindings)
    {
        var list = bindings.Where(b => !b.IsEmpty).ToList();
        var result = new HashSet<HotkeyAction>();
        for (var i = 0; i < list.Count; i++)
        {
            for (var j = i + 1; j < list.Count; j++)
            {
                if (list[i].SameKeyAs(list[j]))
                {
                    result.Add(list[i].Action);
                    result.Add(list[j].Action);
                }
            }
        }

        return result;
    }
}
