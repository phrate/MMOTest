using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

internal static class ItemIconPathV1
{
    private const string MigrationId = "item_icon_path_v1";

    public static void Apply(SqliteConnection conn)
    {
        if (MigrationHelpers.TableExists(conn, "schema_migrations"))
        {
            using var check = conn.CreateCommand();
            check.CommandText = "SELECT 1 FROM schema_migrations WHERE migration_id = $id;";
            check.Parameters.AddWithValue("$id", MigrationId);
            if (check.ExecuteScalar() != null) return;
        }

        using var tx = conn.BeginTransaction();

        if (!MigrationHelpers.ColumnExists(conn, "items", "icon_path"))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "ALTER TABLE items ADD COLUMN icon_path TEXT NOT NULL DEFAULT '';";
            cmd.ExecuteNonQuery();
        }

        using (var mark = conn.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = "INSERT INTO schema_migrations (migration_id) VALUES ($id);";
            mark.Parameters.AddWithValue("$id", MigrationId);
            mark.ExecuteNonQuery();
        }

        tx.Commit();
        GD.Print("[Server] Migration: item.icon_path added");
    }
}