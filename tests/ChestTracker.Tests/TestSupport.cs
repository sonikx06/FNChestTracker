using ChestTracker.Core.Abstractions;
using ChestTracker.Core.Data;
using ChestTracker.Core.Tracking;

namespace ChestTracker.Tests;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? start = null) => UtcNow = start ?? new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;

    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}

/// <summary>Temporary directory with a migrated database; deleted on dispose.</summary>
public sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        Directory = Path.Combine(Path.GetTempPath(), "chesttracker-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        DatabaseFile = Path.Combine(Directory, "chests.db");
        Backups = new BackupService(Path.Combine(Directory, "backups"));
        Open();
    }

    public string Directory { get; }
    public string DatabaseFile { get; }
    public BackupService Backups { get; }
    public SqliteDatabase Db { get; private set; } = null!;
    public ChestRepository Repo { get; private set; } = null!;
    public DatabaseOpenReport Report { get; private set; } = null!;

    public void Open()
    {
        (Db, Report) = DatabaseBootstrapper.Open(DatabaseFile, Backups);
        Repo = new ChestRepository(Db);
    }

    /// <summary>Simulates an application restart (the connection is closed, file kept).</summary>
    public void Reopen()
    {
        Db.Dispose();
        Open();
    }

    public TrackingEngine CreateEngine(FakeClock clock, TrackingOptions? options = null,
        Core.Models.TrackingMode mode = Core.Models.TrackingMode.Manual)
    {
        var engine = new TrackingEngine(Repo, clock, options, mode);
        engine.Initialize();
        return engine;
    }

    public void Dispose()
    {
        Db.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
