using System.Globalization;
using ChestTracker.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace ChestTracker.Core.Data;

public sealed record DatabaseOpenReport
{
    public bool CreatedNew { get; init; }
    public bool RestoredFromBackup { get; init; }
    public string? CorruptFileMovedTo { get; init; }
    public int FromVersion { get; init; }
    public int ToVersion { get; init; }
    public string? PreMigrationBackup { get; init; }
}

/// <summary>
/// Opens the database safely: verifies integrity, quarantines a corrupt file,
/// restores the newest backup if possible, backs up before migrating, then migrates.
/// </summary>
public static class DatabaseBootstrapper
{
    public static (SqliteDatabase Database, DatabaseOpenReport Report) Open(string databaseFile, BackupService backups)
    {
        var existed = File.Exists(databaseFile) && new FileInfo(databaseFile).Length > 0;
        string? quarantined = null;
        var restored = false;

        var db = TryOpenHealthy(databaseFile);
        if (db is null)
        {
            quarantined = Quarantine(databaseFile);
            Log.Warn($"Database failed integrity check; moved to {quarantined}");

            foreach (var backup in backups.GetBackups())
            {
                File.Copy(backup, databaseFile, overwrite: true);
                db = TryOpenHealthy(databaseFile);
                if (db is not null)
                {
                    restored = true;
                    Log.Info($"Restored database from backup {backup}");
                    break;
                }

                Quarantine(databaseFile);
            }

            db ??= new SqliteDatabase(databaseFile);
        }

        var from = db.SchemaVersion;
        string? preMigration = null;
        if (from > 0 && from < Migrations.LatestVersion)
        {
            preMigration = backups.CreateBackup(db, $"premigrationv{from}");
        }

        try
        {
            Migrations.Apply(db);
        }
        catch
        {
            db.Dispose();
            throw;
        }

        var report = new DatabaseOpenReport
        {
            CreatedNew = !existed || (quarantined is not null && !restored),
            RestoredFromBackup = restored,
            CorruptFileMovedTo = quarantined,
            FromVersion = from,
            ToVersion = db.SchemaVersion,
            PreMigrationBackup = preMigration,
        };
        return (db, report);
    }

    private static SqliteDatabase? TryOpenHealthy(string file)
    {
        SqliteDatabase? db = null;
        try
        {
            db = new SqliteDatabase(file);
            if (db.CheckIntegrity())
            {
                return db;
            }
        }
        catch (SqliteException ex)
        {
            Log.Warn($"Cannot open database {file}", ex);
        }

        db?.Dispose();
        return null;
    }

    private static string Quarantine(string file)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var target = $"{file}.corrupt-{stamp}";
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var source = file + suffix;
            if (File.Exists(source))
            {
                File.Move(source, target + suffix, overwrite: true);
            }
        }

        return target;
    }
}
