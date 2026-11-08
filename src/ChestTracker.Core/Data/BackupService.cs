using System.Globalization;

namespace ChestTracker.Core.Data;

public sealed class BackupService
{
    private const string Prefix = "chests-";

    public BackupService(string directory, int keep = 14)
    {
        Directory = directory;
        Keep = Math.Max(1, keep);
    }

    public string Directory { get; }
    public int Keep { get; }

    public string CreateBackup(SqliteDatabase db, string reason)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var safeReason = new string(reason.Where(char.IsLetterOrDigit).ToArray());
        var path = Path.Combine(Directory, $"{Prefix}{stamp}-{safeReason}.db");
        db.BackupTo(path);
        Prune();
        return path;
    }

    /// <summary>Creates a "daily" backup when the newest backup is older than 24 hours.</summary>
    public string? CreateDailyBackupIfNeeded(SqliteDatabase db)
    {
        var latest = GetBackups().FirstOrDefault();
        if (latest is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(latest) < TimeSpan.FromHours(24))
        {
            return null;
        }

        return CreateBackup(db, "daily");
    }

    /// <summary>Backups, newest first.</summary>
    public IReadOnlyList<string> GetBackups()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory.GetFiles(Directory, Prefix + "*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(p => p, StringComparer.Ordinal)
            .ToList();
    }

    public string? GetLatestBackup() => GetBackups().FirstOrDefault();

    private void Prune()
    {
        foreach (var old in GetBackups().Skip(Keep))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
                // Ignore locked files; they will be pruned next time.
            }
        }
    }
}
