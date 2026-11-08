using System.Globalization;
using System.IO.Compression;
using ChestTracker.Protocol;
using ChestTracker.Server.Data;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ChestTracker.Server.Services;

/// <summary>
/// Logical backups that need no external tools: every table is exported with PostgreSQL COPY (CSV) into a
/// ZIP archive (<c>*.ctbak</c>). A backup is taken in one REPEATABLE READ transaction, so it is consistent.
/// <see cref="RestoreAsync"/> loads an archive into an empty (freshly migrated) database.
/// </summary>
public sealed class BackupService(NpgsqlDataSource db, IOptions<ServerOptions> options, TimeProvider clock, ILogger<BackupService> logger)
    : BackgroundService
{
    /// <summary>Parent tables first; restore runs in this order and deletion in reverse.</summary>
    public static readonly string[] Tables =
    [
        "players", "device_credentials", "parties", "party_members", "shared_matches", "shared_match_participants", "chest_events",
    ];

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Directory => Path.GetFullPath(options.Value.BackupDirectory, AppContext.BaseDirectory);

    public DateTimeOffset? LastBackupAt => List().FirstOrDefault()?.CreatedAt;

    public IReadOnlyList<BackupInfoDto> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return new DirectoryInfo(Directory).GetFiles("chesttracker-*.ctbak")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupInfoDto(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
            .ToList();
    }

    public async Task<BackupInfoDto> CreateBackupAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var stamp = clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(Directory, $"chesttracker-{stamp}.ctbak");
            var tmp = path + ".tmp";

            await using (var c = await db.OpenConnectionAsync(ct))
            await using (var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct))
            await using (var file = File.Create(tmp))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var version = await c.ScalarAsync<int>("SELECT COALESCE(MAX(version), 0) FROM schema_migrations", tx);
                await using (var meta = new StreamWriter(zip.CreateEntry("meta.txt").Open()))
                {
                    await meta.WriteLineAsync($"schema={version}");
                    await meta.WriteLineAsync($"created={clock.GetUtcNow():O}");
                }

                foreach (var table in Tables)
                {
                    using var reader = await c.BeginTextExportAsync($"COPY {table} TO STDOUT (FORMAT CSV, HEADER)", ct);
                    await using var entry = new StreamWriter(zip.CreateEntry(table + ".csv", CompressionLevel.Optimal).Open());
                    var buffer = new char[81920];
                    int read;
                    while ((read = await reader.ReadAsync(buffer, ct)) > 0)
                    {
                        await entry.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }

                await tx.CommitAsync(ct);
            }

            File.Move(tmp, path, overwrite: true);
            Prune();
            var info = new FileInfo(path);
            logger.LogInformation("Backup written: {File} ({Size} bytes)", info.Name, info.Length);
            return new BackupInfoDto(info.Name, info.Length, clock.GetUtcNow());
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Restores an archive. The database must be empty unless <paramref name="replace"/> is true.</summary>
    public async Task RestoreAsync(string archive, bool replace, CancellationToken ct = default)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var existing = await c.ScalarAsync<long>("SELECT COUNT(*) FROM players", tx);
        if (existing > 0 && !replace)
        {
            throw new InvalidOperationException("The database is not empty. Use --replace to overwrite it.");
        }

        foreach (var table in Tables.Reverse())
        {
            await c.ExecAsync($"DELETE FROM {table}", tx);
        }

        using var zip = ZipFile.OpenRead(archive);
        foreach (var table in Tables)
        {
            var entry = zip.GetEntry(table + ".csv") ?? throw new InvalidDataException($"Backup has no {table}.csv");
            using var source = new StreamReader(entry.Open());
            var header = await source.ReadLineAsync(ct) ?? throw new InvalidDataException($"{table}.csv is empty");
            // Column list from the header keeps older backups loadable after columns were added.
            await using (var writer = await c.BeginTextImportAsync($"COPY {table} ({header}) FROM STDIN (FORMAT CSV)", ct))
            {
                var buffer = new char[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await writer.WriteAsync(buffer.AsMemory(0, read));
                }
            }
        }

        await tx.CommitAsync(ct);
        logger.LogInformation("Restored backup {Archive}", archive);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = options.Value.BackupIntervalHours;
        if (hours <= 0)
        {
            return;
        }

        var interval = TimeSpan.FromHours(hours);
        // First backup soon after start if the newest one is older than the interval.
        var due = LastBackupAt is { } last ? last + interval - clock.GetUtcNow() : TimeSpan.FromMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(due > TimeSpan.Zero ? due : TimeSpan.Zero, stoppingToken);
                await CreateBackupAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic backup failed");
            }

            due = interval;
        }
    }

    private void Prune()
    {
        foreach (var old in new DirectoryInfo(Directory).GetFiles("chesttracker-*.ctbak")
                     .OrderByDescending(f => f.LastWriteTimeUtc).Skip(Math.Max(1, options.Value.BackupKeep)))
        {
            try
            {
                old.Delete();
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete old backup {File}", old.Name);
            }
        }
    }
}
