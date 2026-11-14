using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

internal static class MigrateNpcsFKColumnName
{
    public static void Apply(SqliteConnection conn)
    {
        if (!MigrationHelpers.TableExists(conn, "npcs")) return;
        if (MigrationHelpers.ColumnExists(conn, "npcs", "combat_levels_id")
            && !MigrationHelpers.ColumnExists(conn, "npcs", "levels_combat_id"))
        {
            MigrationHelpers.Exec(conn, "ALTER TABLE npcs RENAME COLUMN combat_levels_id TO levels_combat_id;");
            GD.Print("[Server] Renamed column: npcs.combat_levels_id -> levels_combat_id");
        }
    }
}