using Npgsql;

namespace ChestTracker.Server.Data;

internal static class Db
{
    public static NpgsqlCommand Cmd(this NpgsqlConnection c, string sql, NpgsqlTransaction? tx, params (string Name, object? Value)[] parameters)
    {
        var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value switch
            {
                null => DBNull.Value,
                DateTimeOffset d => d.UtcDateTime,
                Enum e => Convert.ToInt16(e),
                _ => value,
            });
        }

        return cmd;
    }

    public static async Task<int> ExecAsync(this NpgsqlConnection c, string sql, NpgsqlTransaction? tx, params (string, object?)[] p)
    {
        await using var cmd = c.Cmd(sql, tx, p);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T?> ScalarAsync<T>(this NpgsqlConnection c, string sql, NpgsqlTransaction? tx, params (string, object?)[] p)
    {
        await using var cmd = c.Cmd(sql, tx, p);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public static async Task<List<T>> ListAsync<T>(this NpgsqlConnection c, string sql, NpgsqlTransaction? tx,
        Func<NpgsqlDataReader, T> map, params (string, object?)[] p)
    {
        await using var cmd = c.Cmd(sql, tx, p);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await r.ReadAsync())
        {
            list.Add(map(r));
        }

        return list;
    }

    public static DateTimeOffset Ts(this NpgsqlDataReader r, int i) =>
        new(DateTime.SpecifyKind(r.GetFieldValue<DateTime>(i), DateTimeKind.Utc));

    public static DateTimeOffset? NTs(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.Ts(i);

    public static Guid? NGuid(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
}
