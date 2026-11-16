using System;
using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

internal static class ItemCapabilitiesV1
{
    private const string MigrationId = "item_capabilities_v1";

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

        if (MigrationHelpers.ColumnExists(conn, "items", "type_slug"))
        {
            try { Exec(conn, tx, "ALTER TABLE items DROP COLUMN type_slug;"); }
            catch (SqliteException e) { GD.PrintErr($"[ItemCapabilitiesV1] drop type_slug: {e.Message}"); }
        }
        if (!MigrationHelpers.ColumnExists(conn, "items", "description"))
            Exec(conn, tx, "ALTER TABLE items ADD COLUMN description TEXT NOT NULL DEFAULT '';");
        if (!MigrationHelpers.ColumnExists(conn, "items", "base_price"))
            Exec(conn, tx, "ALTER TABLE items ADD COLUMN base_price INTEGER NOT NULL DEFAULT 0;");
        if (!MigrationHelpers.ColumnExists(conn, "items", "is_active"))
            Exec(conn, tx, "ALTER TABLE items ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1));");

        Exec(conn, tx, @"
            CREATE TABLE IF NOT EXISTS item_tags (
                item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
                tag     TEXT    NOT NULL,
                PRIMARY KEY (item_id, tag)
            );");
        Exec(conn, tx, "CREATE INDEX IF NOT EXISTS idx_item_tags_tag ON item_tags(tag);");

        Exec(conn, tx, @"
            CREATE TABLE IF NOT EXISTS item_equipment (
                item_id                INTEGER PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
                slot                   TEXT NOT NULL CHECK (slot IN (
                    'head','back','neck','body','belt','legs','wrist_left','wrist_right',
                    'hands','ring_1','ring_2','feet','main_hand','off_hand','ammo')),
                attack_speed_override  INTEGER NOT NULL DEFAULT 0,
                attack_bonus_melee     INTEGER NOT NULL DEFAULT 0,
                attack_bonus_ranged    INTEGER NOT NULL DEFAULT 0,
                attack_bonus_ether     INTEGER NOT NULL DEFAULT 0,
                defense_bonus_melee    INTEGER NOT NULL DEFAULT 0,
                defense_bonus_ranged   INTEGER NOT NULL DEFAULT 0,
                defense_bonus_ether    INTEGER NOT NULL DEFAULT 0,
                bonus_melee_strength   INTEGER NOT NULL DEFAULT 0,
                bonus_ranged_strength  INTEGER NOT NULL DEFAULT 0,
                bonus_ether_strength   INTEGER NOT NULL DEFAULT 0
            );");

        Exec(conn, tx, @"
            CREATE TABLE IF NOT EXISTS item_consumable (
                item_id      INTEGER PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
                effect_type  TEXT    NOT NULL CHECK (effect_type IN ('heal','buff','utility')),
                heal_amount  INTEGER NOT NULL DEFAULT 0 CHECK (heal_amount >= 0)
            );");

        Exec(conn, tx, @"
            CREATE TABLE IF NOT EXISTS item_set_member (
                item_id   INTEGER PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
                set_slug  TEXT    NOT NULL
            );");
        Exec(conn, tx, "CREATE INDEX IF NOT EXISTS idx_item_set_member_slug ON item_set_member(set_slug);");

        Exec(conn, tx, "DROP TABLE IF EXISTS weapons;");
        Exec(conn, tx, "DROP TABLE IF EXISTS consumables;");

        using (var mark = conn.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = "INSERT INTO schema_migrations (migration_id) VALUES ($id);";
            mark.Parameters.AddWithValue("$id", MigrationId);
            mark.ExecuteNonQuery();
        }

        tx.Commit();
        GD.Print("[Server] Migration: item capabilities schema applied");
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}