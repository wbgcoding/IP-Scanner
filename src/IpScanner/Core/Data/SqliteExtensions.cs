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
}
