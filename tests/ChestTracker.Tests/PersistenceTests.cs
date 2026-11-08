using ChestTracker.Core.Data;
using ChestTracker.Core.Models;
using ChestTracker.Core.Tracking;

namespace ChestTracker.Tests;

public class PersistenceTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Fresh_database_is_migrated_to_latest_version()
    {
        Assert.Equal(Migrations.LatestVersion, _db.Db.SchemaVersion);
        Assert.True(_db.Report.CreatedNew);
    }

    [Fact]
    public void Statistics_survive_restart()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();
        engine.AddChestManual(false);
        engine.AddChestManual(false);
        engine.EndMatch();
        engine.AdjustLifetime(10);
        engine.Shutdown();

        _db.Reopen();
        var restarted = _db.CreateEngine(_clock);

        var s = restarted.GetSnapshot();
        Assert.Equal(12, s.LifetimeCount);
        Assert.Equal(1, s.MatchesPlayed);
        Assert.Equal(0, s.SessionCount);
        Assert.Null(s.CurrentMatch);
    }

    [Fact]
    public void Older_schema_is_upgraded_with_backup_and_data_kept()
    {
        // Build a v1 database by hand.
        _db.Db.Dispose();
        File.Delete(_db.DatabaseFile);
        using (var v1 = new SqliteDatabase(_db.DatabaseFile))
        {
            v1.Execute(Migrations.All[0].Sql);
            v1.Execute("PRAGMA user_version = 1;");
            v1.Execute("INSERT INTO sessions VALUES ('11111111-1111-1111-1111-111111111111', 0, 1000);");
            v1.Execute("INSERT INTO matches VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 0, 1000, 0, 0, NULL);");
            v1.Execute("INSERT INTO chest_events (event_id, match_id, ts, kind, source, delta) VALUES ('33333333-3333-3333-3333-333333333333', '22222222-2222-2222-2222-222222222222', 500, 0, 0, 1);");
        }

        _db.Open();

        Assert.Equal(1, _db.Report.FromVersion);
        Assert.Equal(Migrations.LatestVersion, _db.Report.ToVersion);
        Assert.NotNull(_db.Report.PreMigrationBackup);
        Assert.True(File.Exists(_db.Report.PreMigrationBackup));
        Assert.Equal(1, _db.Repo.GetLifetimeTotal());
        Assert.Empty(_db.Repo.GetPendingReviews());
    }

    [Fact]
    public void Unfinished_old_match_is_closed_at_last_activity_after_crash()
    {
        var engine = _db.CreateEngine(_clock);
        var match = engine.StartMatch();
        _clock.AdvanceSeconds(90);
        engine.AddChestManual(false);
        var lastActivity = _clock.UtcNow;
        // Simulated crash: no Shutdown(), connection dropped.
        _db.Reopen();

        _clock.Advance(TimeSpan.FromHours(2));
        var restarted = _db.CreateEngine(_clock);

        var stored = _db.Repo.GetMatch(match.Id)!;
        Assert.True(stored.Recovered);
        Assert.Equal(lastActivity, stored.EndedAt);
        Assert.Null(restarted.CurrentMatch);
        Assert.Equal(1, restarted.GetSnapshot().LifetimeCount);
        Assert.All(_db.Repo.GetSessions().Where(s => s.Id != restarted.SessionId), s => Assert.NotNull(s.EndedAt));
    }

    [Fact]
    public void Recent_unfinished_match_is_resumed_after_crash()
    {
        var engine = _db.CreateEngine(_clock);
        var match = engine.StartMatch();
        engine.AddChestManual(false);
        engine.AddChestManual(false);
        _db.Reopen();

        _clock.Advance(TimeSpan.FromMinutes(2));
        var restarted = _db.CreateEngine(_clock);

        Assert.Equal(match.Id, restarted.CurrentMatch?.Id);
        var s = restarted.GetSnapshot();
        Assert.Equal(2, s.MatchCount);
        Assert.Equal(2, s.SessionCount);

        Assert.True(restarted.AddChestManual(true).Applied);
        Assert.Equal(3, restarted.GetSnapshot().MatchCount);
    }

    [Fact]
    public void Corrupt_database_is_quarantined_and_restored_from_backup()
    {
        var engine = _db.CreateEngine(_clock);
        engine.AddChestManual(false);
        engine.Shutdown();
        _db.Backups.CreateBackup(_db.Db, "test");
        _db.Db.Dispose();

        File.WriteAllBytes(_db.DatabaseFile, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        foreach (var side in new[] { "-wal", "-shm" })
        {
            File.Delete(_db.DatabaseFile + side);
        }

        _db.Open();

        Assert.True(_db.Report.RestoredFromBackup);
        Assert.NotNull(_db.Report.CorruptFileMovedTo);
        Assert.True(File.Exists(_db.Report.CorruptFileMovedTo));
        Assert.Equal(1, _db.Repo.GetLifetimeTotal());
    }

    [Fact]
    public void Corrupt_database_without_backup_starts_fresh()
    {
        _db.Db.Dispose();
        File.WriteAllBytes(_db.DatabaseFile, Enumerable.Repeat((byte)0x11, 4096).ToArray());

        _db.Open();

        Assert.False(_db.Report.RestoredFromBackup);
        Assert.True(_db.Report.CreatedNew);
        Assert.Equal(Migrations.LatestVersion, _db.Db.SchemaVersion);
    }

    [Fact]
    public void Export_then_import_into_empty_database_reproduces_statistics_and_is_idempotent()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();
        engine.AddChestManual(false);
        engine.AddChestManual(false);
        engine.RemoveLastChest(false);
        engine.EndMatch();
        engine.AdjustLifetime(5);
        var exportFile = Path.Combine(_db.Directory, "export.json");
        DataExporter.ExportJson(_db.Repo, exportFile, _clock.UtcNow);

        using var other = new TempDatabase();
        var first = DataExporter.ImportJson(other.Repo, exportFile);
        var second = DataExporter.ImportJson(other.Repo, exportFile);

        Assert.Equal(4, first.Events);
        Assert.Equal(0, second.Events);
        Assert.Equal(0, second.Matches);
        Assert.Equal(_db.Repo.GetLifetimeTotal(), other.Repo.GetLifetimeTotal());
        Assert.Equal(6, other.Repo.GetLifetimeTotal());
    }

    [Fact]
    public void Import_rejects_foreign_json()
    {
        var file = Path.Combine(_db.Directory, "foreign.json");
        File.WriteAllText(file, """{ "Format": "something-else", "FormatVersion": 1 }""");

        Assert.Throws<InvalidDataException>(() => DataExporter.ImportJson(_db.Repo, file));
    }

    [Fact]
    public void Csv_export_contains_header_and_rows()
    {
        var engine = _db.CreateEngine(_clock);
        engine.StartMatch();
        engine.AddChestManual(false);
        engine.EndMatch();
        var csv = Path.Combine(_db.Directory, "matches.csv");

        DataExporter.ExportMatchesCsv(_db.Repo.GetMatchSummaries(), csv, TimeZoneInfo.Utc);

        var lines = File.ReadAllLines(csv);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("match_id,", lines[0].TrimStart('﻿'));
        Assert.Contains(",1,1,0,0,0,Manual,0,", lines[1]);
    }

    [Fact]
    public void Reset_all_data_keeps_current_session_usable()
    {
        var engine = _db.CreateEngine(_clock);
        engine.AddChestManual(false);
        engine.EndMatch();

        engine.ResetAllData();
        engine.AddChestManual(false);

        var s = engine.GetSnapshot();
        Assert.Equal(1, s.LifetimeCount);
        Assert.Equal(1, s.SessionCount);
        Assert.Equal(1, s.MatchesPlayed);
    }

    [Fact]
    public void Match_summary_counts_sources_and_corrections()
    {
        var engine = _db.CreateEngine(_clock, mode: TrackingMode.Hybrid);
        var match = engine.StartMatch();
        engine.AddChestManual(false);
        _clock.AdvanceSeconds(10);
        engine.AddChestAutomatic(0.9);
        _clock.AdvanceSeconds(10);
        engine.RemoveLastChest(false);
        engine.SetCurrentMatchCount(4);

        var summary = _db.Repo.GetMatchSummary(match.Id)!;
        Assert.Equal(4, summary.Chests);
        Assert.Equal(1, summary.ManualAdds);
        Assert.Equal(1, summary.AutomaticAdds);
        Assert.Equal(1, summary.Undos);
        Assert.Equal(1, summary.Corrections);
        Assert.Equal(TrackingMode.Hybrid, summary.Match.Mode);
    }

    [Fact]
    public void Backups_are_pruned_to_keep_limit()
    {
        var backups = new BackupService(Path.Combine(_db.Directory, "prune"), keep: 3);
        for (var i = 0; i < 6; i++)
        {
            backups.CreateBackup(_db.Db, "t" + i);
        }

        Assert.Equal(3, backups.GetBackups().Count);
    }
}
