using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

internal static class MigrateLootToLootLists
{
    public static void Apply(SqliteConnection conn)
    {
        if (!MigrationHelpers.TableExists(conn, "loot") || MigrationHelpers.TableExists(conn, "loot_lists")) return;
        MigrationHelpers.Exec(conn, "ALTER TABLE loot RENAME TO loot_lists;");
        GD.Print("[Server] Renamed table: loot -> loot_lists");
    }
}