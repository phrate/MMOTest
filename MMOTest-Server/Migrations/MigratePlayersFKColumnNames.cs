using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

internal static class MigratePlayersFKColumnNames
{
    public static void Apply(SqliteConnection conn)
    {
        if (!MigrationHelpers.TableExists(conn, "players")) return;
        if (MigrationHelpers.ColumnExists(conn, "players", "combat_levels_id")
            && !MigrationHelpers.ColumnExists(conn, "players", "levels_combat_id"))
        {
            MigrationHelpers.Exec(conn, "ALTER TABLE players RENAME COLUMN combat_levels_id TO levels_combat_id;");
            GD.Print("[Server] Renamed column: players.combat_levels_id -> levels_combat_id");
        }
        if (MigrationHelpers.ColumnExists(conn, "players", "skill_levels_id")
            && !MigrationHelpers.ColumnExists(conn, "players", "levels_skill_id"))
        {
            MigrationHelpers.Exec(conn, "ALTER TABLE players RENAME COLUMN skill_levels_id TO levels_skill_id;");
            GD.Print("[Server] Renamed column: players.skill_levels_id -> levels_skill_id");
        }
    }
}