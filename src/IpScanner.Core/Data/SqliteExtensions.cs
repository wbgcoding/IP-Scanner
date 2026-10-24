using Microsoft.Data.Sqlite;

namespace IpScanner.Core.Data;

internal static class SqliteExtensions
{
    public static SqliteCommand CreateCommand(this SqliteConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    /// <summary>Runs a statement that returns no rows and disposes the command afterwards.</summary>
    public static int Exec(this SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand(sql);
        if (tx is not null) cmd.Transaction = tx;
        return cmd.ExecuteNonQuery();
    }
}
