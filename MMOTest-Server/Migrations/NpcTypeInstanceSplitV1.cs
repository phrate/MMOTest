using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

/// <summary>
/// Splits the monolithic `npcs` table into `npc_types` (shared definitions)
/// and `npc_instances` (per-placement state). Drops the old table entirely —
/// existing placeholders are discarded. Seeds one Citizen type + one instance
/// for testing.
///
/// One-time. Recorded in `schema_migrations`.
/// </summary>
internal static class NpcTypeInstanceSplitV1
{
    private const string MigrationId = "npc_type_instance_split_v1";

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

        // 1. Create new tables
        Exec(conn, tx, @"
            CREATE TABLE IF NOT EXISTS npc_types (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                slug_id             TEXT    NOT NULL UNIQUE,
                display             TEXT    NOT NULL,
                render_slug         TEXT    NOT NULL DEFAULT '',
                dialogue_script     TEXT    NOT NULL DEFAULT '',
                is_banker           INTEGER NOT NULL DEFAULT 0 CHECK (is_banker IN (0, 1)),
                attackable          INTEGER NOT NULL DEFAULT 1 CHECK (attackable IN (0, 1)),
                aggressive          INTEGER NOT NULL DEFAULT 0 CHECK (aggressive IN (0, 1)),
                aggro_range         INTEGER NOT NULL DEFAULT 8 CHECK (aggro_range >= 1),
                ai_package_state    TEXT    NOT NULL DEFAULT 'idle',
                spawn_timer         INTEGER NOT NULL DEFAULT 3,
                spawn_flags         TEXT    NOT NULL DEFAULT '{}',
                loot_table_name     TEXT    NOT NULL DEFAULT 'common_loot_1',
                credits_table_name  TEXT    NOT NULL DEFAULT '',
                levels_combat_id    INTEGER REFERENCES levels_combat(id) ON DELETE SET NULL,
                created_at          TEXT    NOT NULL DEFAULT (datetime('now'))
            );");

        Exec(conn, tx, @"
            CREATE TABLE IF NOT EXISTS npc_instances (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                npc_type_id         INTEGER NOT NULL REFERENCES npc_types(id) ON DELETE CASCADE,
                name_override       TEXT,
                tile_x              INTEGER NOT NULL,
                tile_y              INTEGER NOT NULL,
                tile_z              INTEGER NOT NULL DEFAULT 100,
                playing_animation   TEXT    NOT NULL DEFAULT '',
                is_dead             INTEGER NOT NULL DEFAULT 0 CHECK (is_dead IN (0, 1)),
                is_dying            INTEGER NOT NULL DEFAULT 0 CHECK (is_dying IN (0, 1)),
                died_at             TEXT
            );");

        Exec(conn, tx, "CREATE INDEX IF NOT EXISTS idx_npc_instances_type ON npc_instances (npc_type_id);");
        Exec(conn, tx, "CREATE INDEX IF NOT EXISTS idx_npc_instances_tile ON npc_instances (tile_x, tile_y);");

        // 2. Drop old placeholders and their combat rows
        Exec(conn, tx, "DROP TABLE IF EXISTS npcs;");
        Exec(conn, tx, "DELETE FROM levels_combat WHERE slug_id LIKE 'npc\\_%' ESCAPE '\\' OR template_slug IS NOT NULL;");

        // 3. Seed Citizen type + one test instance
        long combatId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO levels_combat
                    (slug_id, final_level,
                     hitpoints, max_hitpoints,
                     accuracy, max_accuracy,
                     strength, max_strength,
                     defence, max_defence,
                     ether, max_ether,
                     melee_bonus, ranged_bonus, ether_bonus,
                     melee_defense, ranged_defense, ether_defense,
                     attack_speed)
                VALUES ('npc_citizen', 1,
                        10, 10, 1, 1, 1, 1, 1, 1, 1, 1,
                        0, 0, 0, 0, 0, 0, 10)
                RETURNING id;";
            combatId = (long)cmd.ExecuteScalar()!;
        }

        long typeId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO npc_types
                    (slug_id, display, render_slug, dialogue_script, is_banker,
                     attackable, aggressive, aggro_range, ai_package_state,
                     spawn_timer, spawn_flags, loot_table_name, credits_table_name,
                     levels_combat_id)
                VALUES ('citizen', 'Citizen', 'man', '', 0,
                        1, 0, 8, 'idle', 3, '{}', 'common_loot_1', '',
                        $combat_id)
                RETURNING id;";
            cmd.Parameters.AddWithValue("$combat_id", combatId);
            typeId = (long)cmd.ExecuteScalar()!;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO npc_instances
                    (npc_type_id, name_override, tile_x, tile_y, tile_z,
                     playing_animation, is_dead, is_dying)
                VALUES ($tid, NULL, 0, 0, 100, 'idle', 0, 0);";
            cmd.Parameters.AddWithValue("$tid", typeId);
            cmd.ExecuteNonQuery();
        }

        // 4. Record migration
        using (var mark = conn.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = "INSERT INTO schema_migrations (migration_id) VALUES ($id);";
            mark.Parameters.AddWithValue("$id", MigrationId);
            mark.ExecuteNonQuery();
        }

        tx.Commit();
        GD.Print("[Server] Migration: npc_types + npc_instances created; old NPCs dropped; Citizen seeded");
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}