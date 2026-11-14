using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

internal static class DropMaxStackColumn
{
    public static void Apply(SqliteConnection conn)
    {
        if (!MigrationHelpers.TableExists(conn, "items") || !MigrationHelpers.ColumnExists(conn, "items", "max_stack")) return;
        try
        {
            MigrationHelpers.Exec(conn, "ALTER TABLE items DROP COLUMN max_stack;");
            GD.Print("[Server] Dropped column: items.max_stack");
        }
        catch (SqliteException e)
        {
            GD.PrintErr($"[Server] Could not drop items.max_stack: {e.Message}");
        }
    }
}