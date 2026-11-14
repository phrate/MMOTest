using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

internal static class MigratePlayersDropBankId
{
    public static void Apply(SqliteConnection conn)
    {
        if (!MigrationHelpers.TableExists(conn, "players")
            || !MigrationHelpers.ColumnExists(conn, "players", "bank_id")) return;
        try
        {
            MigrationHelpers.Exec(conn, "ALTER TABLE players DROP COLUMN bank_id;");
            GD.Print("[Server] Dropped column: players.bank_id");
        }
        catch (SqliteException) { /* older SQLite, leave the dead column */ }
    }
}