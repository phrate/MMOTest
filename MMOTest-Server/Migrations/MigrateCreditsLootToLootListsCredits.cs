using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

internal static class MigrateCreditsLootToLootListsCredits
{
    public static void Apply(SqliteConnection conn)
    {
        if (!MigrationHelpers.TableExists(conn, "credits_loot") || MigrationHelpers.TableExists(conn, "loot_lists_credits")) return;
        MigrationHelpers.Exec(conn, "ALTER TABLE credits_loot RENAME TO loot_lists_credits;");
        GD.Print("[Server] Renamed table: credits_loot -> loot_lists_credits");
    }
}