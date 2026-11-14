using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

internal static class SqlHelpers
{
    public static long InsertEmptyRow(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {table} DEFAULT VALUES RETURNING id;";
        return (long)cmd.ExecuteScalar()!;
    }

    public static bool RowExists(SqliteConnection conn, SqliteTransaction tx, string table, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT 1 FROM {table} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() != null;
    }
}