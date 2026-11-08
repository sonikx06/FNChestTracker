using System.Globalization;
using ChestTracker.Core.Models;

namespace ChestTracker.Core.Statistics;

public sealed record DailyTotal(DateOnly Day, int Chests, int Matches);

public sealed record SessionTotal(Guid SessionId, DateTimeOffset StartedAt, int Chests, int Matches);

public sealed record PersonalBests
{
    public MatchSummary? BestMatch { get; init; }
    public DailyTotal? BestDay { get; init; }
    public SessionTotal? BestSession { get; init; }
    public int TotalMatches { get; init; }
    public int TotalChests { get; init; }
    public double AveragePerMatch { get; init; }

    /// <summary>Best chests-per-minute among matches that lasted at least 3 minutes.</summary>
    public double BestChestsPerMinute { get; init; }
}

public enum MatchPeriod
{
    AllTime,
    Today,
    Last7Days,
    Last30Days,
    ThisYear,
}

public sealed record MatchFilter
{
    public string? Search { get; init; }
    public MatchPeriod Period { get; init; } = MatchPeriod.AllTime;
    public TrackingMode? Mode { get; init; }
    public int? MinChests { get; init; }
}

/// <summary>Pure aggregations over match summaries; time grouping uses the supplied time zone.</summary>
public static class StatisticsCalculator
{
    public static IReadOnlyList<MatchSummary> Filter(IEnumerable<MatchSummary> matches, MatchFilter filter,
        DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        DateOnly? from = filter.Period switch
        {
            MatchPeriod.Today => today,
            MatchPeriod.Last7Days => today.AddDays(-6),
            MatchPeriod.Last30Days => today.AddDays(-29),
            MatchPeriod.ThisYear => new DateOnly(today.Year, 1, 1),
            _ => null,
        };

        var search = filter.Search?.Trim();
        return matches.Where(m =>
        {
            var local = TimeZoneInfo.ConvertTime(m.Match.StartedAt, timeZone);
            if (from is { } f && DateOnly.FromDateTime(local.DateTime) < f) return false;
            if (filter.Mode is { } mode && m.Match.Mode != mode) return false;
            if (filter.MinChests is { } min && m.Chests < min) return false;
            if (string.IsNullOrEmpty(search)) return true;

            return m.Match.Id.ToString("D").StartsWith(search, StringComparison.OrdinalIgnoreCase)
                   || local.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase)
                   || local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase)
                   || (m.Match.Note?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false)
                   || (int.TryParse(search, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && m.Chests == n);
        }).ToList();
    }

    /// <summary>Totals per local calendar day, oldest first. Optionally fills days without matches with zeros.</summary>
    public static IReadOnlyList<DailyTotal> PerDay(IEnumerable<MatchSummary> matches, TimeZoneInfo timeZone,
        DateOnly? from = null, DateOnly? to = null, bool fillGaps = true)
    {
        var groups = matches
            .GroupBy(m => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(m.Match.StartedAt, timeZone).DateTime))
            .ToDictionary(g => g.Key, g => new DailyTotal(g.Key, g.Sum(x => x.Chests), g.Count()));

        if (groups.Count == 0 && (from is null || to is null))
        {
            return [];
        }

        var first = from ?? groups.Keys.Min();
        var last = to ?? groups.Keys.Max();
        if (!fillGaps)
        {
            return groups.Values.Where(d => d.Day >= first && d.Day <= last).OrderBy(d => d.Day).ToList();
        }

        var result = new List<DailyTotal>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            result.Add(groups.TryGetValue(day, out var total) ? total : new DailyTotal(day, 0, 0));
        }

        return result;
    }

    /// <summary>Totals per application session, oldest first.</summary>
    public static IReadOnlyList<SessionTotal> PerSession(IEnumerable<MatchSummary> matches) =>
        matches.GroupBy(m => m.Match.SessionId)
            .Select(g => new SessionTotal(g.Key, g.Min(x => x.Match.StartedAt), g.Sum(x => x.Chests), g.Count()))
            .OrderBy(s => s.StartedAt)
            .ToList();

    /// <summary>Average chests per match after each match (chronological input).</summary>
    public static IReadOnlyList<double> CumulativeAverage(IEnumerable<MatchSummary> chronological)
    {
        var result = new List<double>();
        long sum = 0;
        var n = 0;
        foreach (var m in chronological)
        {
            sum += m.Chests;
            n++;
            result.Add((double)sum / n);
        }

        return result;
    }

    public static IReadOnlyList<double> RollingAverage(IReadOnlyList<int> values, int window)
    {
        window = Math.Max(1, window);
        var result = new List<double>(values.Count);
        long sum = 0;
        for (var i = 0; i < values.Count; i++)
        {
            sum += values[i];
            if (i >= window)
            {
                sum -= values[i - window];
            }

            result.Add((double)sum / Math.Min(i + 1, window));
        }

        return result;
    }

    public static PersonalBests Bests(IReadOnlyCollection<MatchSummary> matches, TimeZoneInfo timeZone, DateTimeOffset now)
    {
        if (matches.Count == 0)
        {
            return new PersonalBests();
        }

        var bestMatch = matches.OrderByDescending(m => m.Chests).ThenBy(m => m.Match.StartedAt).First();
        var bestDay = PerDay(matches, timeZone, fillGaps: false).OrderByDescending(d => d.Chests).ThenBy(d => d.Day).FirstOrDefault();
        var bestSession = PerSession(matches).OrderByDescending(s => s.Chests).ThenBy(s => s.StartedAt).FirstOrDefault();
        var rate = matches
            .Where(m => m.Match.EndedAt is not null && m.DurationAt(now) >= TimeSpan.FromMinutes(3))
            .Select(m => m.Chests / m.DurationAt(now).TotalMinutes)
            .DefaultIfEmpty(0)
            .Max();
        var total = matches.Sum(m => m.Chests);

        return new PersonalBests
        {
            BestMatch = bestMatch.Chests > 0 ? bestMatch : null,
            BestDay = bestDay is { Chests: > 0 } ? bestDay : null,
            BestSession = bestSession is { Chests: > 0 } ? bestSession : null,
            TotalMatches = matches.Count,
            TotalChests = total,
            AveragePerMatch = (double)total / matches.Count,
            BestChestsPerMinute = rate,
        };
    }
}
