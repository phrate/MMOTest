using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

internal static class MigrateCombatLevelsToLevelsCombat
{
    public static void Apply(SqliteConnection conn)
    {
        if (!MigrationHelpers.TableExists(conn, "combat_levels") || MigrationHelpers.TableExists(conn, "levels_combat")) return;
        MigrationHelpers.Exec(conn, "ALTER TABLE combat_levels RENAME TO levels_combat;");
        MigrationHelpers.Exec(conn, "DROP INDEX IF EXISTS idx_combat_levels_slug;");
        MigrationHelpers.Exec(conn, "DROP INDEX IF EXISTS idx_combat_levels_template;");
        GD.Print("[Server] Renamed table: combat_levels -> levels_combat");
    }
}