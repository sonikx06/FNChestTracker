using System.Windows;
using System.Windows.Threading;
using ChestTracker.App.Capture;
using ChestTracker.App.Localization;
using ChestTracker.App.Services;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Core.Models;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Tracking;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChestTracker.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _toastTimer;
    private readonly LocalSoundRouter _soundRouter;

    [ObservableProperty]
    private ObservableObject _currentPage;

    [ObservableProperty]
    private string _currentPageKey = "Dashboard";

    [ObservableProperty]
    private string? _toastText;

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private ToastKind _toastKind;

    [ObservableProperty]
    private DetectionStatus _detectionStatus;

    [ObservableProperty]
    private string _detectionStatusText = "";

    public MainViewModel(AppServices services)
    {
        Services = services;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _soundRouter = new LocalSoundRouter(cue => Services.Sounds.Play(cue));
        Snapshot = services.Engine.GetSnapshot();

        Settings = new SettingsViewModel(this);
        Overlay = new OverlayViewModel(this);
        Dashboard = new DashboardViewModel(this);
        History = new HistoryViewModel(this);
        Statistics = new StatisticsViewModel(this);
        Detection = new DetectionViewModel(this);
        Party = new PartyViewModel(this);
        Server = new ServerViewModel(this);
        _currentPage = Dashboard;

        _clock = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Tick();
        _toastTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(3.2) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            IsToastVisible = false;
        };

        services.Engine.Changed += OnEngineChanged;
        services.Hotkeys.Pressed += (_, action) => HandleHotkey(action);
        services.Detection.StatusChanged += (_, _) => _dispatcher.BeginInvoke(UpdateDetectionStatus);
        Loc.LanguageChanged += (_, _) =>
        {
            UpdateDetectionStatus();
            Dashboard.Apply(Snapshot, null);
        };

        var sync = services.Sync;
        sync.StateChanged += (_, _) => _dispatcher.BeginInvoke(() => Overlay.ApplyParty(sync.Party, sync.Account?.PlayerId));
        sync.Activity += (_, a) => _dispatcher.BeginInvoke(() => OnTeammateActivity(a));
        sync.SharedMatchStarted += (_, e) => _dispatcher.BeginInvoke(() =>
            ShowToast(Loc.T(e.Joined ? "Party.Toast.Joined" : "Party.Toast.Invited"), e.Joined ? ToastKind.Success : ToastKind.Warning));
        sync.SharedMatchEnded += (_, _) => _dispatcher.BeginInvoke(() => ShowToast(Loc.T("Party.Toast.Ended")));
        sync.RemovedFromParty += (_, _) => _dispatcher.BeginInvoke(() => ShowToast(Loc.T("Party.Toast.Removed"), ToastKind.Warning));

        UpdateDetectionStatus();
        ApplySnapshot(Snapshot, null);
    }

    public PartyViewModel Party { get; }

    public ServerViewModel Server { get; }

    private void OnTeammateActivity(ChestTracker.Protocol.ActivityDto a)
    {
        var m = Services.Settings.Multiplayer;
        var text = a.Delta > 0 ? Loc.F("Party.Activity.Add", a.DisplayName) : Loc.F("Party.Activity.Remove", a.DisplayName);
        Overlay.ShowActivityMessage(text);
        if (m.NotifyTeammateChests && a.Delta > 0)
        {
            // Visual only: a chest sound plays only on the computer that recorded the chest (LocalSoundRouter).
            ShowToast(text, ToastKind.Info);
        }
    }

    public AppServices Services { get; }

    public TrackingSnapshot Snapshot { get; private set; }

    public DashboardViewModel Dashboard { get; }
    public HistoryViewModel History { get; }
    public StatisticsViewModel Statistics { get; }
    public DetectionViewModel Detection { get; }
    public SettingsViewModel Settings { get; }
    public OverlayViewModel Overlay { get; }

    public event EventHandler<TrackingSnapshot>? SnapshotChanged;

    [RelayCommand]
    private void Navigate(string key)
    {
        CurrentPageKey = key;
        Detection.SetActive(key == "Detection");
        CurrentPage = key switch
        {
            "History" => History,
            "Statistics" => Statistics,
            "Detection" => Detection,
            "Settings" => Settings,
            "Party" => Party,
            "Server" => Server,
            _ => Dashboard,
        };

        if (CurrentPage == Party) Party.OnNavigatedTo();

        if (CurrentPage == History) History.Refresh();
        if (CurrentPage == Statistics) Statistics.Refresh();
    }

    // ------------------------------------------------------------- actions

    public void AddChest(bool fromHotkey)
    {
        if (fromHotkey && Snapshot.Mode == TrackingMode.Automatic)
        {
            ShowToast(Loc.T("Toast.AutoModeHotkeys"), ToastKind.Warning);
            return;
        }

        Report(Services.Engine.AddChestManual(fromHotkey));
    }

    public void RemoveChest(bool fromHotkey)
    {
        if (fromHotkey && Snapshot.Mode == TrackingMode.Automatic)
        {
            ShowToast(Loc.T("Toast.AutoModeHotkeys"), ToastKind.Warning);
            return;
        }

        Report(Services.Engine.RemoveLastChest(fromHotkey));
    }

    public void StartMatch() => Guard(() => ReportMatch(Services.Engine.RequestStartMatch()));

    public void EndMatch() => Guard(() => ReportMatch(Services.Engine.RequestEndMatch()));

    /// <summary>The dashboard's single Start Match / End Match button.</summary>
    public void ToggleMatch() => Guard(() => ReportMatch(Services.Engine.ToggleMatch()));

    private void ReportMatch(MatchControlResult result)
    {
        switch (result.Outcome)
        {
            case MatchControlOutcome.Ended when result.Summary is { } summary:
                // The result is already saved: the event log and the match's end time are written before this returns.
                ShowToast(Loc.F("Toast.MatchEnded", summary.Chests), ToastKind.Success);
                break;
            case MatchControlOutcome.Started when result.Summary is { } previous:
                ShowToast(Loc.F("Toast.MatchEnded", previous.Chests), ToastKind.Success);
                break;
            case MatchControlOutcome.NoActiveMatch:
                ShowToast(Loc.T("Toast.NoMatch"), ToastKind.Warning);
                break;
        }
    }

    public void HandleHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.AddChest:
                AddChest(fromHotkey: true);
                break;
            case HotkeyAction.RemoveLastChest:
                RemoveChest(fromHotkey: true);
                break;
            case HotkeyAction.StartMatch:
                // The match hotkey toggles counting: start a match, press again (e.g. after dying) to end it.
                // Same authoritative engine state and double-press guard as the dashboard's Start/End button.
                ToggleMatch();
                break;
            case HotkeyAction.EndMatch:
                EndMatch();
                break;
            case HotkeyAction.ToggleOverlay:
                Settings.OverlayVisible = !Settings.OverlayVisible;
                break;
            case HotkeyAction.ToggleOverlayEditMode:
                Overlay.IsEditMode = !Overlay.IsEditMode;
                break;
            case HotkeyAction.ToggleTeamOverlay:
                Settings.OverlayTeamMode = !Settings.OverlayTeamMode;
                break;
        }
    }

    public void ShowToast(string text, ToastKind kind = ToastKind.Info)
    {
        ToastText = text;
        ToastKind = kind;
        IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("Action failed", ex);
            ShowToast(Loc.F("Toast.Error", ex.Message), ToastKind.Error);
        }
    }

    private void Report(TrackingResult result)
    {
        switch (result.Code)
        {
            case TrackingResultCode.Applied:
                break;
            case TrackingResultCode.IgnoredDebounce:
                ShowToast(Loc.T("Toast.Debounce"));
                break;
            case TrackingResultCode.IgnoredDuplicate:
                ShowToast(Loc.T("Toast.Duplicate"));
                break;
            case TrackingResultCode.IgnoredCooldown:
                ShowToast(Loc.T("Toast.Cooldown"));
                break;
            case TrackingResultCode.NothingToRemove:
                ShowToast(Loc.T("Toast.NothingToRemove"), ToastKind.Warning);
                break;
            case TrackingResultCode.NoActiveMatch:
                ShowToast(Loc.T("Toast.NoMatch"), ToastKind.Warning);
                break;
            default:
                ShowToast(Loc.F("Toast.Error", result.Code), ToastKind.Error);
                break;
        }
    }

    // -------------------------------------------------------------- events

    private void OnEngineChanged(object? sender, TrackingChangedEventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            OnTrackingChanged(e);
        }
        else
        {
            _dispatcher.BeginInvoke(() => OnTrackingChanged(e));
        }
    }

    private void OnTrackingChanged(TrackingChangedEventArgs e)
    {
        ApplySnapshot(e.Snapshot, e);
        _soundRouter.Handle(e);

        switch (e.Kind)
        {
            case TrackingChangeKind.ChestAdded:
                if (e.Event?.Source == EventSource.Automatic)
                {
                    ShowToast(Loc.F("Toast.Detected", (e.Event.Confidence ?? 0).ToString("P0", Loc.Culture)), ToastKind.Success);
                }

                break;
            case TrackingChangeKind.MatchStarted:
                ShowToast(Loc.T("Toast.MatchStarted"), ToastKind.Success);
                break;
            case TrackingChangeKind.ReviewAdded:
                ShowToast(Loc.T("Toast.Uncertain"), ToastKind.Warning);
                break;
        }
    }

    private void ApplySnapshot(TrackingSnapshot snapshot, TrackingChangedEventArgs? e)
    {
        Snapshot = snapshot;
        Dashboard.Apply(snapshot, e);
        Overlay.Apply(snapshot);
        History.MarkDirty(CurrentPage == History);
        Statistics.MarkDirty(CurrentPage == Statistics);
        SnapshotChanged?.Invoke(this, snapshot);

        if (snapshot.CurrentMatch is not null)
        {
            if (!_clock.IsEnabled) _clock.Start();
        }
        else
        {
            _clock.Stop();
        }

        Tick();
    }

    private void Tick()
    {
        var match = Snapshot.CurrentMatch;
        var text = match is null ? "00:00" : Loc.Duration(DateTimeOffset.UtcNow - match.StartedAt);
        Dashboard.DurationText = text;
        Overlay.DurationText = text;
    }

    private void UpdateDetectionStatus()
    {
        DetectionStatus = Services.Detection.Status;
        DetectionStatusText = Loc.T("Det.Status." + DetectionStatus);
        Overlay.DetectionStatus = DetectionStatus;
        Dashboard.DetectionStatus = DetectionStatus;
        Dashboard.DetectionStatusText = DetectionStatusText;
        Detection.OnStatusChanged();
    }

    public void Dispose()
    {
        _clock.Stop();
        _toastTimer.Stop();
        Services.Engine.Changed -= OnEngineChanged;
        Detection.Dispose();
    }
}
