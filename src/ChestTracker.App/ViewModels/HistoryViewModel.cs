using System.Collections.ObjectModel;
using System.Globalization;
using ChestTracker.App.Localization;
using ChestTracker.Core.Data;
using ChestTracker.Core.Models;
using ChestTracker.Core.Statistics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ChestTracker.App.ViewModels;

public sealed class MatchRowViewModel
{
    public MatchRowViewModel(MatchSummary s, DateTimeOffset now)
    {
        Summary = s;
        var local = s.Match.StartedAt.ToLocalTime();
        StartText = local.ToString("dd.MM.yyyy  HH:mm", CultureInfo.InvariantCulture);
        DurationText = Loc.Duration(s.DurationAt(now));
        ModeText = Loc.T("Mode." + s.Match.Mode);
        StatusText = s.IsActive ? Loc.T("Hist.Active") : s.Match.Recovered ? Loc.T("Hist.Recovered") : Loc.T("Hist.Finished");
        ShortId = s.Match.Id.ToString("N")[..8];
    }

    public MatchSummary Summary { get; }
    public string StartText { get; }
    public string DurationText { get; }
    public int Chests => Summary.Chests;
    public int ManualAdds => Summary.ManualAdds;
    public int AutomaticAdds => Summary.AutomaticAdds;
    public int Corrections => Summary.Corrections + Summary.Undos;
    public string ModeText { get; }
    public string StatusText { get; }
    public string ShortId { get; }
    public bool IsActive => Summary.IsActive;
}

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private IReadOnlyList<MatchSummary> _all = [];
    private bool _dirty = true;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private LocalizedOption<MatchPeriod> _selectedPeriod;
    [ObservableProperty] private LocalizedOption<TrackingMode?> _selectedMode;
    [ObservableProperty] private string _minChestsText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private MatchRowViewModel? _selectedRow;

    public HistoryViewModel(MainViewModel main)
    {
        _main = main;
        Periods = Enum.GetValues<MatchPeriod>().Select(p => new LocalizedOption<MatchPeriod>(p, "Period." + p)).ToList();
        Modes =
        [
            new LocalizedOption<TrackingMode?>(null, "Hist.AllModes"),
            .. Enum.GetValues<TrackingMode>().Select(m => new LocalizedOption<TrackingMode?>(m, "Mode." + m)),
        ];
        _selectedPeriod = Periods[0];
        _selectedMode = Modes[0];
        Loc.LanguageChanged += (_, _) => MarkDirty(true);
    }

    public IReadOnlyList<LocalizedOption<MatchPeriod>> Periods { get; }

    public IReadOnlyList<LocalizedOption<TrackingMode?>> Modes { get; }

    public ObservableCollection<MatchRowViewModel> Rows { get; } = [];

    public void MarkDirty(bool refreshNow)
    {
        _dirty = true;
        if (refreshNow)
        {
            Refresh();
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        _all = _main.Services.Repository.GetMatchSummaries();
        _dirty = false;
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedPeriodChanged(LocalizedOption<MatchPeriod> value) => ApplyFilter();

    partial void OnSelectedModeChanged(LocalizedOption<TrackingMode?> value) => ApplyFilter();

    partial void OnMinChestsTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_dirty)
        {
            _all = _main.Services.Repository.GetMatchSummaries();
            _dirty = false;
        }

        var now = DateTimeOffset.UtcNow;
        var filter = new MatchFilter
        {
            Search = SearchText,
            Period = SelectedPeriod?.Value ?? MatchPeriod.AllTime,
            Mode = SelectedMode?.Value,
            MinChests = int.TryParse(MinChestsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var min) ? min : null,
        };
        var filtered = StatisticsCalculator.Filter(_all, filter, now, TimeZoneInfo.Local);

        Rows.Clear();
        foreach (var m in filtered)
        {
            Rows.Add(new MatchRowViewModel(m, now));
        }

        var total = filtered.Sum(m => m.Chests);
        var avg = filtered.Count == 0 ? 0 : (double)total / filtered.Count;
        SummaryText = Loc.F("Hist.Count", filtered.Count, total, avg.ToString("0.0", Loc.Culture));
        IsEmpty = filtered.Count == 0;
    }

    [RelayCommand]
    private void Edit(MatchRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null)
        {
            return;
        }

        var value = _main.Services.Dialogs.PromptInt(Loc.T("Dialog.EditCount.Title"), Loc.T("Dialog.EditCount.Message"),
            row.Chests, 0, 9999);
        if (value is { } v)
        {
            _main.Guard(() => _main.Services.Engine.SetMatchCount(row.Summary.Id, v));
            Refresh();
        }
    }

    [RelayCommand]
    private void Delete(MatchRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null)
        {
            return;
        }

        if (_main.Services.Dialogs.Confirm(Loc.T("Dialog.DeleteMatch.Title"), Loc.T("Dialog.DeleteMatch.Message"), danger: true))
        {
            _main.Guard(() => _main.Services.Engine.DeleteMatch(row.Summary.Id));
            Refresh();
        }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"chest-matches-{DateTime.Now:yyyyMMdd}.csv",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _main.Guard(() =>
        {
            DataExporter.ExportMatchesCsv(Rows.Select(r => r.Summary), dialog.FileName, TimeZoneInfo.Local);
            _main.ShowToast(Loc.F("Toast.Exported", System.IO.Path.GetFileName(dialog.FileName)), ToastKind.Success);
        });
    }
}
