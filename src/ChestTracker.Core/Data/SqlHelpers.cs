using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ChestTracker.Core.Data;

internal static class SqlHelpers
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        return cmd;
    }

    /// <summary>Adds a parameter, converting domain types to their storage representation.</summary>
    public static SqliteCommand Bind(this SqliteCommand cmd, string name, object? value)
    {
        object stored = value switch
        {
            null => DBNull.Value,
            Guid g => g.ToString("D"),
            DateTimeOffset d => d.ToUnixTimeMilliseconds(),
            bool b => b ? 1 : 0,
            Enum e => Convert.ToInt32(e, CultureInfo.InvariantCulture),
            _ => value,
        };
        cmd.Parameters.AddWithValue(name, stored);
        return cmd;
    }

    public static Guid GetGuidValue(this SqliteDataReader r, int ordinal) => Guid.Parse(r.GetString(ordinal));

    public static Guid? GetNullableGuid(this SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : Guid.Parse(r.GetString(ordinal));

    public static DateTimeOffset GetTime(this SqliteDataReader r, int ordinal) =>
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(ordinal));

    public static DateTimeOffset? GetNullableTime(this SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(ordinal));

    public static string? GetNullableString(this SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    public static double? GetNullableDouble(this SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetDouble(ordinal);

    public static long ScalarLong(this SqliteCommand cmd)
    {
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}
