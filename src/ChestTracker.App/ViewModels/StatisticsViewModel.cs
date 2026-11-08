using System.Globalization;
using ChestTracker.App.Controls;
using ChestTracker.App.Localization;
using ChestTracker.Core.Statistics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChestTracker.App.ViewModels;

public sealed partial class StatisticsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _dirty = true;

    [ObservableProperty] private List<ChartPoint> _perMatch = [];
    [ObservableProperty] private List<ChartPoint> _perDay = [];
    [ObservableProperty] private List<ChartPoint> _perSession = [];
    [ObservableProperty] private List<ChartPoint> _rollingAverage = [];
    [ObservableProperty] private List<ChartPoint> _cumulativeAverage = [];
    [ObservableProperty] private string _bestMatchText = "—";
    [ObservableProperty] private string _bestMatchDate = "";
    [ObservableProperty] private string _bestDayText = "—";
    [ObservableProperty] private string _bestDayDate = "";
    [ObservableProperty] private string _bestSessionText = "—";
    [ObservableProperty] private string _bestSessionDate = "";
    [ObservableProperty] private string _bestRateText = "—";
    [ObservableProperty] private string _totalMatchesText = "0";
    [ObservableProperty] private string _averageText = "0";
    [ObservableProperty] private bool _hasData;

    public StatisticsViewModel(MainViewModel main)
    {
        _main = main;
        Loc.LanguageChanged += (_, _) => MarkDirty(_main.CurrentPage == this);
    }

    public string EmptyText => Loc.T("Stats.NoData");

    public void MarkDirty(bool refreshNow)
    {
        _dirty = true;
        if (refreshNow)
        {
            Refresh();
        }
    }

    public void Refresh()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        OnPropertyChanged(nameof(EmptyText));
        var now = DateTimeOffset.UtcNow;
        var tz = TimeZoneInfo.Local;
        var all = _main.Services.Repository.GetMatchSummaries().OrderBy(m => m.Match.StartedAt).ToList();
        HasData = all.Count > 0;

        PerMatch = all.TakeLast(40)
            .Select(m => new ChartPoint(m.Match.StartedAt.ToLocalTime().ToString("dd.MM", CultureInfo.InvariantCulture), m.Chests,
                $"{m.Match.StartedAt.ToLocalTime():dd.MM HH:mm} · {m.Chests}"))
            .ToList();

        var today = DateOnly.FromDateTime(DateTime.Now);
        PerDay = StatisticsCalculator.PerDay(all, tz, today.AddDays(-29), today)
            .Select(d => new ChartPoint(d.Day.ToString("dd.MM", CultureInfo.InvariantCulture), d.Chests,
                $"{d.Day:dd.MM.yyyy} · {d.Chests} ({d.Matches})"))
            .ToList();

        PerSession = StatisticsCalculator.PerSession(all).TakeLast(20)
            .Select(s => new ChartPoint(s.StartedAt.ToLocalTime().ToString("dd.MM", CultureInfo.InvariantCulture), s.Chests,
                $"{s.StartedAt.ToLocalTime():dd.MM HH:mm} · {s.Chests} ({s.Matches})"))
            .ToList();

        var values = all.Select(m => m.Chests).ToList();
        var rolling = StatisticsCalculator.RollingAverage(values, 10);
        var cumulative = StatisticsCalculator.CumulativeAverage(all);
        var skip = Math.Max(0, all.Count - 60);
        RollingAverage = all.Skip(skip)
            .Select((m, i) => new ChartPoint(m.Match.StartedAt.ToLocalTime().ToString("dd.MM", CultureInfo.InvariantCulture), Math.Round(rolling[skip + i], 2)))
            .ToList();
        CumulativeAverage = all.Skip(skip)
            .Select((m, i) => new ChartPoint("", Math.Round(cumulative[skip + i], 2)))
            .ToList();

        var bests = StatisticsCalculator.Bests(all, tz, now);
        var unit = Loc.T("Stats.ChestsUnit");
        BestMatchText = bests.BestMatch is { } bm ? $"{bm.Chests}" : "—";
        BestMatchDate = bests.BestMatch is { } bm2 ? bm2.Match.StartedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "";
        BestDayText = bests.BestDay is { } bd ? $"{bd.Chests}" : "—";
        BestDayDate = bests.BestDay is { } bd2 ? $"{bd2.Day:dd.MM.yyyy} · {bd2.Matches} ×" : "";
        BestSessionText = bests.BestSession is { } bs ? $"{bs.Chests}" : "—";
        BestSessionDate = bests.BestSession is { } bs2 ? $"{bs2.StartedAt.ToLocalTime():dd.MM.yyyy} · {bs2.Matches} ×" : "";
        BestRateText = bests.BestChestsPerMinute > 0 ? bests.BestChestsPerMinute.ToString("0.00", Loc.Culture) : "—";
        TotalMatchesText = bests.TotalMatches.ToString(Loc.Culture);
        AverageText = $"{bests.AveragePerMatch.ToString("0.0", Loc.Culture)} {unit}";
    }
}
