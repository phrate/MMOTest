using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

internal static class MigratePlayersXpToPlayerXp
{
    public static void Apply(SqliteConnection conn)
    {
        if (!MigrationHelpers.TableExists(conn, "players_xp") || MigrationHelpers.TableExists(conn, "player_xp")) return;
        MigrationHelpers.Exec(conn, "ALTER TABLE players_xp RENAME TO player_xp;");
        GD.Print("[Server] Renamed table: players_xp -> player_xp");
    }
}