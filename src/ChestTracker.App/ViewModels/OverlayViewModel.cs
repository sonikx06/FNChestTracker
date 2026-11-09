using System.Windows.Media;
using ChestTracker.App.Capture;
using ChestTracker.Core.Models;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Tracking;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChestTracker.App.ViewModels;

public sealed record OverlayTeamMember(string Name, string Count, bool Online, bool IsMe);

public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty] private int _matchCount;
    [ObservableProperty] private int _matchesPlayed;
    [ObservableProperty] private int _lifetimeCount;
    [ObservableProperty] private bool _isMatchActive;
    [ObservableProperty] private string _durationText = "00:00";
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private OverlayLayout _layout;
    [ObservableProperty] private double _opacity;
    [ObservableProperty] private double _scale;
    [ObservableProperty] private Brush _textBrush = Brushes.White;
    [ObservableProperty] private Brush _accentBrush = Brushes.MediumPurple;
    [ObservableProperty] private Color _accentColor = Colors.MediumPurple;
    [ObservableProperty] private Brush _panelBrush = Brushes.Transparent;
    [ObservableProperty] private Brush _panelBorderBrush = Brushes.Transparent;
    [ObservableProperty] private bool _showTimer;
    [ObservableProperty] private bool _showDetection;
    [ObservableProperty] private bool _isDetectionMode;
    [ObservableProperty] private bool _excludeFromCapture;
    [ObservableProperty] private DetectionStatus _detectionStatus;
    [ObservableProperty] private Brush _detectionBrush = Brushes.Gray;

    [ObservableProperty] private bool _teamMode;
    [ObservableProperty] private bool _inParty;
    [ObservableProperty] private string _teamMatchText = "—";
    [ObservableProperty] private string _teamLifetimeText = "0";
    [ObservableProperty] private bool _sharedMatchActive;
    [ObservableProperty] private string _teamName = "";
    [ObservableProperty] private string? _activityText;
    [ObservableProperty] private bool _showActivity;

    private readonly System.Windows.Threading.DispatcherTimer _activityTimer;

    public OverlayViewModel(MainViewModel main)
    {
        _main = main;
        _activityTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _activityTimer.Tick += (_, _) =>
        {
            _activityTimer.Stop();
            ActivityText = null;
        };
        ApplySettings(main.Services.Settings.Overlay);
        ApplyMultiplayer(main.Services.Settings.Multiplayer);
    }

    public System.Collections.ObjectModel.ObservableCollection<OverlayTeamMember> TeamMembers { get; } = [];

    /// <summary>Team panel replaces the personal layout while in a party and team mode is on.</summary>
    public bool IsTeamLayout => TeamMode && InParty;

    public bool IsPersonalLayout => !IsTeamLayout;

    public void ApplyMultiplayer(MultiplayerSettings m)
    {
        TeamMode = m.OverlayTeamMode;
        ShowActivity = m.OverlayShowActivity;
    }

    /// <summary>
    /// Team numbers come from the same projection as the Party page (<see cref="ChestTracker.Sync.PartyStatistics"/>):
    /// team match = active shared match total, team total = party lifetime (former members included). Your own
    /// number on the overlay is your local match count, labelled as such.
    /// </summary>
    public void ApplyParty(ChestTracker.Sync.PartyStatistics? stats)
    {
        InParty = stats is not null;
        TeamMembers.Clear();
        if (stats is null)
        {
            SharedMatchActive = false;
            TeamMatchText = "—";
            TeamLifetimeText = "0";
            return;
        }

        TeamName = stats.PartyName;
        SharedMatchActive = stats.SharedMatchActive;
        TeamMatchText = ChestTracker.Sync.PartyStatistics.Format(stats.TeamMatch);
        TeamLifetimeText = ChestTracker.Sync.PartyStatistics.Format(stats.TeamLifetime);
        foreach (var m in stats.Members)
        {
            // During a shared match each member's match count, otherwise their party total (as on the Party page).
            var counts = stats.SharedMatchActive ? m.Match : m.Lifetime;
            TeamMembers.Add(new OverlayTeamMember(m.Name, ChestTracker.Sync.PartyStatistics.Format(counts), m.Online, m.IsMe));
        }
    }

    public void ShowActivityMessage(string text)
    {
        if (!ShowActivity) return;
        ActivityText = text;
        _activityTimer.Stop();
        _activityTimer.Start();
    }

    partial void OnTeamModeChanged(bool value) => RaiseLayout();

    partial void OnInPartyChanged(bool value) => RaiseLayout();

    private void RaiseLayout()
    {
        OnPropertyChanged(nameof(IsTeamLayout));
        OnPropertyChanged(nameof(IsPersonalLayout));
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(IsStandard));
        OnPropertyChanged(nameof(IsExpanded));
    }

    /// <summary>Raised when the window should move back to its default position.</summary>
    public event EventHandler? PositionResetRequested;

    public double? SavedLeft => _main.Services.Settings.Overlay.Left;

    public double? SavedTop => _main.Services.Settings.Overlay.Top;

    public bool IsCompact => IsPersonalLayout && Layout == OverlayLayout.Compact;
    public bool IsStandard => IsPersonalLayout && Layout == OverlayLayout.Standard;
    public bool IsExpanded => IsPersonalLayout && Layout == OverlayLayout.Expanded;

    public bool ShowDetectionIndicator => ShowDetection && IsDetectionMode;

    public void Apply(TrackingSnapshot s)
    {
        MatchCount = s.MatchCount;
        MatchesPlayed = s.MatchesPlayed;
        LifetimeCount = s.LifetimeCount;
        IsMatchActive = s.CurrentMatch is not null;
        IsDetectionMode = s.Mode != TrackingMode.Manual;
    }

    public void ApplySettings(OverlaySettings o)
    {
        IsVisible = o.Visible;
        Layout = o.Layout;
        Opacity = o.Opacity;
        Scale = o.Scale;
        ShowTimer = o.ShowMatchTimer;
        ShowDetection = o.ShowDetectionStatus;
        ExcludeFromCapture = o.ExcludeFromCapture;

        var text = Parse(o.TextColor, Colors.White);
        var accent = Parse(o.AccentColor, Color.FromRgb(0xC0, 0x84, 0xFC));
        var panel = Parse(o.BackgroundColor, Color.FromRgb(0x14, 0x0F, 0x22));
        TextBrush = Frozen(text);
        AccentColor = accent;
        AccentBrush = Frozen(accent);
        if (o.ShowBackground)
        {
            PanelBrush = Frozen(Color.FromArgb((byte)(o.BackgroundOpacity * 255), panel.R, panel.G, panel.B));
            PanelBorderBrush = Frozen(Color.FromArgb((byte)(Math.Min(1, o.BackgroundOpacity + 0.1) * 90), accent.R, accent.G, accent.B));
        }
        else
        {
            PanelBrush = Brushes.Transparent;
            PanelBorderBrush = Brushes.Transparent;
        }
    }

    public void ResetPosition() => PositionResetRequested?.Invoke(this, EventArgs.Empty);

    public void SavePosition(double left, double top) => _main.Settings.SetOverlayPosition(left, top);

    partial void OnLayoutChanged(OverlayLayout value)
    {
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(IsStandard));
        OnPropertyChanged(nameof(IsExpanded));
    }

    partial void OnShowDetectionChanged(bool value) => OnPropertyChanged(nameof(ShowDetectionIndicator));

    partial void OnIsDetectionModeChanged(bool value) => OnPropertyChanged(nameof(ShowDetectionIndicator));

    partial void OnDetectionStatusChanged(DetectionStatus value)
    {
        DetectionBrush = value switch
        {
            DetectionStatus.Running => Frozen(Color.FromRgb(0x22, 0xC5, 0x5E)),
            DetectionStatus.Candidate => Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B)),
            DetectionStatus.Detected => Frozen(Color.FromRgb(0xC0, 0x84, 0xFC)),
            DetectionStatus.WaitingForGame or DetectionStatus.Paused or DetectionStatus.Checking => Frozen(Color.FromRgb(0x71, 0x71, 0x7A)),
            DetectionStatus.Spectating => Frozen(Color.FromRgb(0x38, 0xBD, 0xF8)),
            DetectionStatus.Error or DetectionStatus.Unsupported => Frozen(Color.FromRgb(0xF4, 0x3F, 0x5E)),
            _ => Frozen(Color.FromRgb(0x52, 0x52, 0x5B)),
        };
    }

    private static Color Parse(string hex, Color fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
