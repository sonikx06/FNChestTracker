using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using ChestTracker.App.Localization;
using ChestTracker.App.Services;
using ChestTracker.Core.Presence;
using ChestTracker.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChestTracker.App.ViewModels;

/// <summary>Discord Rich Presence settings and a live preview of the status card.</summary>
public sealed partial class DiscordViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Action _changed;
    private readonly DispatcherTimer _clock;
    private PresenceCoordinator? _coordinator;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _previewVisible;
    [ObservableProperty] private string _previewHiddenText = "";
    [ObservableProperty] private string _previewDetails = "";
    [ObservableProperty] private string _previewState = "";
    [ObservableProperty] private string? _previewElapsed;
    [ObservableProperty] private string? _previewLargeText;
    [ObservableProperty] private string? _previewSmallText;
    [ObservableProperty] private string? _previewButton;
    [ObservableProperty] private ImageSource? _previewSmallImage;
    [ObservableProperty] private string? _nicknameInfo;
    [ObservableProperty] private string _assetText = "";
    [ObservableProperty] private bool _assetsOk;
    [ObservableProperty] private bool _assetsMissing;

    private static readonly ImageSource LogoImage = IconFactory.CreateBitmap(128);
    private static readonly ImageSource SoloImage = IconFactory.CreateBadge(64, team: false);
    private static readonly ImageSource TeamImage = IconFactory.CreateBadge(64, team: true);

    public DiscordViewModel(MainViewModel main, Action changed)
    {
        _main = main;
        _changed = changed;
        #pragma warning disable CS0618
        Priorities = Enum.GetValues<PresencePriority>().Where(p => p != PresencePriority.SessionChests).Select(p => new LocalizedOption<PresencePriority>(p, "Discord.Priority." + p)).ToList();
#pragma warning restore CS0618
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateElapsed();
        Loc.LanguageChanged += (_, _) => Refresh();
    }

    public ImageSource Logo => LogoImage;

    private DiscordSettings S => _main.Services.Settings.Discord;

    public IReadOnlyList<LocalizedOption<PresencePriority>> Priorities { get; }

    public void Attach(PresenceCoordinator coordinator)
    {
        _coordinator = coordinator;
        coordinator.Changed += (_, _) => Refresh();
        coordinator.Service.StatusChanged += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public bool Enabled { get => S.Enabled; set => Set(S.Enabled, value, v => S.Enabled = v, reconfigure: true); }

    /// <summary>Fixed Discord Application ID (read-only).</summary>
    public string ApplicationId => DiscordApp.ApplicationId;

    public bool ShowNickname { get => S.ShowNickname; set => Set(S.ShowNickname, value, v => S.ShowNickname = v); }

    public string Nickname { get => S.Nickname; set => Set(S.Nickname, value?.Trim() ?? "", v => S.Nickname = v); }

    public bool ShowLifetime { get => S.ShowLifetime; set => Set(S.ShowLifetime, value, v => S.ShowLifetime = v); }

    public bool ShowTeam { get => S.ShowTeam; set => Set(S.ShowTeam, value, v => S.ShowTeam = v); }

    public bool ShowTimer { get => S.ShowTimer; set => Set(S.ShowTimer, value, v => S.ShowTimer = v); }

    public bool HideWhenFortniteNotRunning
    {
        get => S.HideWhenFortniteNotRunning;
        set => Set(S.HideWhenFortniteNotRunning, value, v => S.HideWhenFortniteNotRunning = v);
    }

    /// <summary>Fixed project link of the presence button (read-only).</summary>
    public string ProjectUrl => PresenceBuilder.ProjectUrl;

    public LocalizedOption<PresencePriority> SelectedPriority
    {
        get => Priorities.FirstOrDefault(p => p.Value == S.Priority) ?? Priorities[0];
        set
        {
            if (value is not null) Set(S.Priority, value.Value, v => S.Priority = v, name: nameof(SelectedPriority));
        }
    }

    private void Set<T>(T current, T value, Action<T> apply, bool reconfigure = false, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        apply(value);
        OnPropertyChanged(name);
        _changed();
        if (reconfigure) _coordinator?.ApplyConfiguration();
        else _coordinator?.Recompute();
    }


    private void Refresh()
    {
        if (_coordinator is null) return;
        var svc = _coordinator.Service;
        IsConnected = svc.Status == PresenceStatus.Connected;
        StatusText = svc.Status switch
        {
            PresenceStatus.Connected when svc.LastError is { } err => Loc.F("Discord.Status.Refused", err),
            PresenceStatus.Connected => svc.ConnectedUser is { } u ? Loc.F("Discord.Status.ConnectedAs", u) : Loc.T("Discord.Status.Connected"),
            PresenceStatus.Error => Loc.F("Discord.Status.Error", svc.LastError ?? "?"),
            _ => Loc.T("Discord.Status." + svc.Status),
        };

        var check = _coordinator.AssetCheck;
        AssetsOk = check.Status == AssetCheckStatus.Ok;
        AssetsMissing = check.Status == AssetCheckStatus.Missing;
        AssetText = check.Status switch
        {
            AssetCheckStatus.Ok => Loc.F("Discord.Assets.Ok", string.Join(", ", DiscordApp.RequiredAssets)),
            AssetCheckStatus.Missing => Loc.F("Discord.Assets.Missing", string.Join(", ", check.Missing)),
            AssetCheckStatus.Unavailable => Loc.F("Discord.Assets.Unavailable", check.Error ?? "?"),
            _ => Loc.T("Discord.Assets.Checking"),
        };

        var nick = _coordinator.Nickname;
        NicknameInfo = S.ShowNickname ? Loc.F("Discord.NicknameSource." + nick.Source, nick.Name) : null;

        var a = _coordinator.Current;
        PreviewVisible = a is not null;
        PreviewHiddenText = !S.Enabled ? Loc.T("Discord.Preview.Disabled")
            : S.HideWhenFortniteNotRunning && !_coordinator.FortniteRunning ? Loc.T("Discord.Preview.NoFortnite")
            : "";
        if (a is null)
        {
            _clock.Stop();
            return;
        }

        PreviewDetails = a.Details;
        PreviewState = a.State;
        PreviewLargeText = a.LargeText;
        PreviewSmallText = a.SmallText;
        PreviewButton = a.Buttons.FirstOrDefault()?.Label;
        PreviewSmallImage = a.SmallImage == DiscordApp.TeamImageKey ? TeamImage : SoloImage;
        UpdateElapsed();
        if (a.Start is not null) _clock.Start(); else _clock.Stop();
    }

    private void UpdateElapsed()
    {
        PreviewElapsed = _coordinator?.Current?.Start is { } start
            ? Loc.F("Discord.Preview.Elapsed", Loc.Duration(DateTimeOffset.UtcNow - start))
            : null;
    }
}
