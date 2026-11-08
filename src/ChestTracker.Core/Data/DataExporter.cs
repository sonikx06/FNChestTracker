using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChestTracker.Core.Models;

namespace ChestTracker.Core.Data;

public sealed class ExportPackage
{
    public const string FormatName = "fortnite-chest-tracker";

    public string Format { get; set; } = FormatName;
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset ExportedAt { get; set; }
    public List<SessionRecord> Sessions { get; set; } = [];
    public List<MatchRecord> Matches { get; set; } = [];
    public List<ChestEvent> Events { get; set; } = [];
    public List<DetectionReview> Reviews { get; set; } = [];
}

/// <summary>JSON export/import of the full event log and CSV export of match history.</summary>
public static class DataExporter
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ExportPackage CreatePackage(ChestRepository repo, DateTimeOffset now) => new()
    {
        ExportedAt = now,
        Sessions = repo.GetSessions().ToList(),
        Matches = repo.GetMatches().ToList(),
        Events = repo.GetAllEvents().ToList(),
        Reviews = repo.GetAllReviews().ToList(),
    };

    public static void ExportJson(ChestRepository repo, string path, DateTimeOffset now)
    {
        var package = CreatePackage(repo, now);
        WriteAtomically(path, JsonSerializer.Serialize(package, JsonOptions));
    }

    public static ImportResult ImportJson(ChestRepository repo, string path)
    {
        var json = File.ReadAllText(path);
        var package = JsonSerializer.Deserialize<ExportPackage>(json, JsonOptions)
                      ?? throw new InvalidDataException("Empty export file.");
        if (!string.Equals(package.Format, ExportPackage.FormatName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The file is not a Fortnite Chest Tracker export.");
        }

        if (package.FormatVersion > 1)
        {
            throw new InvalidDataException($"Unsupported export version {package.FormatVersion}.");
        }

        return repo.Import(package.Sessions, package.Matches, package.Events, package.Reviews);
    }

    public static void ExportMatchesCsv(IEnumerable<MatchSummary> matches, string path, TimeZoneInfo timeZone)
    {
        var sb = new StringBuilder();
        sb.AppendLine("match_id,started_local,ended_local,duration_seconds,chests,manual_adds,automatic_adds,undos,corrections,tracking_mode,recovered,note");
        foreach (var s in matches.OrderBy(m => m.Match.StartedAt))
        {
            var m = s.Match;
            var start = TimeZoneInfo.ConvertTime(m.StartedAt, timeZone);
            var end = m.EndedAt is { } e ? TimeZoneInfo.ConvertTime(e, timeZone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
            var duration = m.EndedAt is { } ended ? ((long)(ended - m.StartedAt).TotalSeconds).ToString(CultureInfo.InvariantCulture) : "";
            sb.Append(m.Id.ToString("D")).Append(',')
                .Append(start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                .Append(end).Append(',')
                .Append(duration).Append(',')
                .Append(s.Chests.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(s.ManualAdds.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(s.AutomaticAdds.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(s.Undos.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(s.Corrections.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(m.Mode).Append(',')
                .Append(m.Recovered ? "1" : "0").Append(',')
                .AppendLine(Csv(m.Note));
        }

        WriteAtomically(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    internal static void WriteAtomically(string path, string content, Encoding? encoding = null)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, content, encoding ?? new UTF8Encoding(false));
        File.Move(tmp, full, overwrite: true);
    }
}
