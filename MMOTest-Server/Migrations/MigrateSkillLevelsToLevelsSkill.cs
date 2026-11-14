using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

internal static class MigrateSkillLevelsToLevelsSkill
{
    public static void Apply(SqliteConnection conn)
    {
        if (!MigrationHelpers.TableExists(conn, "skill_levels") || MigrationHelpers.TableExists(conn, "levels_skill")) return;
        MigrationHelpers.Exec(conn, "ALTER TABLE skill_levels RENAME TO levels_skill;");
        MigrationHelpers.Exec(conn, "DROP INDEX IF EXISTS idx_skill_levels_player;");
        GD.Print("[Server] Renamed table: skill_levels -> levels_skill");
    }
}