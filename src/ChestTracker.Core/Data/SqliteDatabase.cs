using Microsoft.Data.Sqlite;

namespace ChestTracker.Core.Data;

/// <summary>
/// Owns a single SQLite connection guarded by a lock. WAL journaling with
/// synchronous=FULL makes every committed event durable, so a crash loses nothing
/// that was acknowledged.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public SqliteDatabase(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        };
        _connection = new SqliteConnection(csb.ToString());
        try
        {
            _connection.Open();
            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA synchronous=FULL;");
            Execute("PRAGMA busy_timeout=5000;");
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public string FilePath { get; }

    public int SchemaVersion
    {
        get => Run(c => (int)c.Command("PRAGMA user_version;").ScalarLong());
    }

    public T Run<T>(Func<SqliteConnection, T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return action(_connection);
        }
    }

    public void Run(Action<SqliteConnection> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            action(_connection);
        }
    }

    public T InTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _connection.BeginTransaction();
            var result = action(_connection, tx);
            tx.Commit();
            return result;
        }
    }

    public void Execute(string sql)
    {
        lock (_gate)
        {
            using var cmd = _connection.Command(sql);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Runs SQLite's quick integrity check.</summary>
    public bool CheckIntegrity()
    {
        return Run(c =>
        {
            using var cmd = c.Command("PRAGMA quick_check;");
            return string.Equals(cmd.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Online backup (consistent even while WAL is active).</summary>
    public void BackupTo(string destinationFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationFile))!);
        Run(c =>
        {
            var csb = new SqliteConnectionStringBuilder { DataSource = destinationFile, Pooling = false };
            using var destination = new SqliteConnection(csb.ToString());
            destination.Open();
            c.BackupDatabase(destination);
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                // Fold the WAL back into the main file so the .db is self-contained.
                using var cmd = _connection.Command("PRAGMA wal_checkpoint(TRUNCATE);");
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Best effort.
            }

            _connection.Dispose();
        }
    }
}
