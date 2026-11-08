using System.Collections.ObjectModel;
using System.Globalization;
using ChestTracker.App.Capture;
using ChestTracker.App.Localization;
using ChestTracker.App.Services;
using ChestTracker.Core.Models;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Tracking;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChestTracker.App.ViewModels;

public enum DeltaKind
{
    Positive,
    Negative,
    Neutral,
}

public sealed class EventItemViewModel
{
    public EventItemViewModel(ChestEvent e)
    {
        Event = e;
        TimeText = e.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Title = e.Kind switch
        {
            ChestEventKind.Add when e.Note == "reviewed" => Loc.T("Event.Reviewed"),
            ChestEventKind.Add when e.Source == EventSource.Automatic => Loc.T("Event.AddAutomatic"),
            ChestEventKind.Add => Loc.T("Event.AddManual"),
            ChestEventKind.Undo => Loc.T("Event.Undo"),
            _ when e.MatchId is null => Loc.T("Event.Lifetime"),
            _ => Loc.T("Event.Correction"),
        };
        DeltaText = e.Delta > 0 ? "+" + e.Delta.ToString(CultureInfo.InvariantCulture) : e.Delta.ToString(CultureInfo.InvariantCulture).Replace('-', '−');
        Kind = e.Kind == ChestEventKind.Correction ? DeltaKind.Neutral : e.Delta > 0 ? DeltaKind.Positive : DeltaKind.Negative;
        ConfidenceText = e.Confidence is { } c ? c.ToString("P0", Loc.Culture) : "";
    }

    public ChestEvent Event { get; }
    public string TimeText { get; }
    public string Title { get; }
    public string DeltaText { get; }
    public DeltaKind Kind { get; }
    public string ConfidenceText { get; }
}

public sealed class ReviewItemViewModel
{
    public ReviewItemViewModel(DetectionReview review)
    {
        Review = review;
        TimeText = review.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        ConfidenceText = review.Confidence.ToString("P0", Loc.Culture);
    }

    public DetectionReview Review { get; }
    public string TimeText { get; }
    public string ConfidenceText { get; }
}

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty] private int _matchCount;
    [ObservableProperty] private int _lifetimeCount;
    [ObservableProperty] private int _matchesPlayed;
    [ObservableProperty] private int _record;
    [ObservableProperty] private string _averageText = "0";
    [ObservableProperty] private bool _isMatchActive;
    [ObservableProperty] private string _durationText = "00:00";
    [ObservableProperty] private string _matchStateText = "";
    [ObservableProperty] private string _hotkeyHint = "";
    [ObservableProperty] private string _modeDescription = "";
    [ObservableProperty] private bool _hasEvents;
    [ObservableProperty] private bool _hasPendingReviews;
    [ObservableProperty] private DetectionStatus _detectionStatus;
    [ObservableProperty] private string _detectionStatusText = "";
    [ObservableProperty] private bool _isDetectionMode;

    public DashboardViewModel(MainViewModel main)
    {
        _main = main;
        main.Services.Hotkeys.StatusChanged += (_, _) => UpdateHotkeyHint();
        UpdateHotkeyHint();
    }

    public SettingsViewModel Settings => _main.Settings;

    public ObservableCollection<EventItemViewModel> RecentEvents { get; } = [];

    public ObservableCollection<ReviewItemViewModel> PendingReviews { get; } = [];

    public void Apply(TrackingSnapshot s, TrackingChangedEventArgs? e)
    {
        MatchCount = s.MatchCount;
        LifetimeCount = s.LifetimeCount;
        MatchesPlayed = s.MatchesPlayed;
        Record = s.Record;
        AverageText = s.AveragePerMatch.ToString("0.0", Loc.Culture);
        IsMatchActive = s.CurrentMatch is not null;
        MatchStateText = Loc.T(IsMatchActive ? "Dash.MatchRunning" : "Dash.NoMatch");
        ModeDescription = Loc.T($"Mode.{s.Mode}.Desc");
        IsDetectionMode = s.Mode != TrackingMode.Manual;

        RecentEvents.Clear();
        foreach (var ev in _main.Services.Repository.GetRecentEvents(30))
        {
            RecentEvents.Add(new EventItemViewModel(ev));
        }

        HasEvents = RecentEvents.Count > 0;

        if (e is null || PendingReviews.Count != s.PendingReviews
            || e.Kind is TrackingChangeKind.ReviewAdded or TrackingChangeKind.ReviewResolved or TrackingChangeKind.DataReset)
        {
            PendingReviews.Clear();
            foreach (var r in _main.Services.Engine.GetPendingReviews().OrderByDescending(r => r.Timestamp))
            {
                PendingReviews.Add(new ReviewItemViewModel(r));
            }

            HasPendingReviews = PendingReviews.Count > 0;
        }

        UpdateHotkeyHint();
    }

    private void UpdateHotkeyHint()
    {
        var hk = _main.Services.Settings.Hotkeys;
        string D(HotkeyAction a) => HotkeyService.Describe(hk.Get(a));
        HotkeyHint = Loc.F("Dash.HotkeyHintFormat", D(HotkeyAction.AddChest), D(HotkeyAction.RemoveLastChest),
            D(HotkeyAction.StartMatch), D(HotkeyAction.EndMatch));
    }

    [RelayCommand]
    private void AddChest() => _main.Guard(() => _main.AddChest(fromHotkey: false));

    [RelayCommand]
    private void RemoveChest() => _main.Guard(() => _main.RemoveChest(fromHotkey: false));

    /// <summary>One state-dependent button: "Start Match" (green play) or "End Match" (red stop).</summary>
    [RelayCommand]
    private void ToggleMatch() => _main.ToggleMatch();

    [RelayCommand]
    private void ResetMatch()
    {
        if (!IsMatchActive)
        {
            _main.ShowToast(Loc.T("Toast.NoMatch"), ToastKind.Warning);
            return;
        }

        if (_main.Services.Dialogs.Confirm(Loc.T("Dialog.ResetMatch.Title"), Loc.T("Dialog.ResetMatch.Message"), danger: true))
        {
            _main.Guard(() => _main.Services.Engine.ResetCurrentMatch());
        }
    }

    [RelayCommand]
    private void EditCount()
    {
        if (!IsMatchActive)
        {
            _main.ShowToast(Loc.T("Toast.NoMatch"), ToastKind.Warning);
            return;
        }

        var value = _main.Services.Dialogs.PromptInt(Loc.T("Dialog.EditCount.Title"), Loc.T("Dialog.EditCount.Message"),
            MatchCount, min: 0, max: 9999);
        if (value is { } v)
        {
            _main.Guard(() => _main.Services.Engine.SetCurrentMatchCount(v));
        }
    }

    [RelayCommand]
    private void AcceptReview(ReviewItemViewModel? item)
    {
        if (item is not null)
        {
            _main.Guard(() => _main.Services.Engine.ResolveReview(item.Review.Id, accept: true));
        }
    }

    [RelayCommand]
    private void RejectReview(ReviewItemViewModel? item)
    {
        if (item is not null)
        {
            _main.Guard(() => _main.Services.Engine.ResolveReview(item.Review.Id, accept: false));
        }
    }

    [RelayCommand]
    private void ToggleOverlayEdit() => _main.Overlay.IsEditMode = !_main.Overlay.IsEditMode;
}
