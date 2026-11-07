using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public record PlayerRecord(long Id, string Username, string PasswordHash, bool IsAdmin);

public class Database
{
    private const int SqliteConstraintError = 19;

    // Migration ID is intentionally kept from the pre-rename era. Renaming the
    // key would cause re-migration of existing DBs (XP × 10 again).
    private const string PlayerXpTenthsMigration = "players_xp_tenths_v1";

    private readonly string _connectionString;

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // FKs are enforced per-connection in SQLite. Enable them everywhere
        // except the schema bootstrap, which does so explicitly after DDL.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 1000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// Creates every table if missing. Idempotent: safe to run on every startup.
    /// Runs with foreign_keys=OFF so forward references (players to stat tables,
    /// combat_levels self-reference) don't fail at CREATE time. Re-enabled after.
    /// </summary>
    public void Initialize()
    {
        using var conn = Open();

        
        MigrateLootToLootLists(conn);
        MigratePlayersXpToPlayerXp(conn);
        MigratePlayerBankToJson(conn);


        Exec(conn, "PRAGMA journal_mode = WAL;");
        Exec(conn, "PRAGMA foreign_keys = OFF;");

        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS combat_levels (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                slug_id         TEXT    NOT NULL UNIQUE,
                template_slug   TEXT    REFERENCES combat_levels(slug_id) ON DELETE SET NULL,
                final_level     INTEGER NOT NULL DEFAULT 3 CHECK (final_level >= 1),
                hitpoints       INTEGER NOT NULL DEFAULT 10 CHECK (hitpoints     >= 0),
                max_hitpoints   INTEGER NOT NULL DEFAULT 10 CHECK (max_hitpoints >= 1),
                accuracy        INTEGER NOT NULL DEFAULT 1  CHECK (accuracy      >= 1),
                max_accuracy    INTEGER NOT NULL DEFAULT 1  CHECK (max_accuracy  >= 1),
                strength        INTEGER NOT NULL DEFAULT 1  CHECK (strength      >= 1),
                max_strength    INTEGER NOT NULL DEFAULT 1  CHECK (max_strength  >= 1),
                defence         INTEGER NOT NULL DEFAULT 1  CHECK (defence       >= 1),
                max_defence     INTEGER NOT NULL DEFAULT 1  CHECK (max_defence   >= 1),
                ether           INTEGER NOT NULL DEFAULT 1  CHECK (ether         >= 1),
                max_ether       INTEGER NOT NULL DEFAULT 1  CHECK (max_ether     >= 1),
                attack_speed    INTEGER NOT NULL DEFAULT 10 CHECK (attack_speed >= 1),
                poisoned        INTEGER NOT NULL DEFAULT 0 CHECK (poisoned IN (0, 1)),
                burning         INTEGER NOT NULL DEFAULT 0 CHECK (burning  IN (0, 1)),
                frozen          INTEGER NOT NULL DEFAULT 0 CHECK (frozen   IN (0, 1)),
                melee_bonus     INTEGER NOT NULL DEFAULT 0 CHECK (melee_bonus    >= 0),
                ranged_bonus    INTEGER NOT NULL DEFAULT 0 CHECK (ranged_bonus   >= 0),
                ether_bonus     INTEGER NOT NULL DEFAULT 0 CHECK (ether_bonus    >= 0),
                melee_defense   INTEGER NOT NULL DEFAULT 0 CHECK (melee_defense  >= 0),
                ranged_defense  INTEGER NOT NULL DEFAULT 0 CHECK (ranged_defense >= 0),
                ether_defense   INTEGER NOT NULL DEFAULT 0 CHECK (ether_defense  >= 0),
                created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT    NOT NULL DEFAULT (datetime('now'))
            );");

        // XP values are stored as tenths after PlayerXpTenthsMigration runs.
        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS player_xp (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                hitpoints   INTEGER NOT NULL DEFAULT 0 CHECK (hitpoints   >= 0),
                accuracy    INTEGER NOT NULL DEFAULT 0 CHECK (accuracy    >= 0),
                strength    INTEGER NOT NULL DEFAULT 0 CHECK (strength    >= 0),
                defence     INTEGER NOT NULL DEFAULT 0 CHECK (defence     >= 0),
                ether       INTEGER NOT NULL DEFAULT 0 CHECK (ether       >= 0),
                woodcutting INTEGER NOT NULL DEFAULT 0 CHECK (woodcutting >= 0),
                mining      INTEGER NOT NULL DEFAULT 0 CHECK (mining      >= 0),
                fishing     INTEGER NOT NULL DEFAULT 0 CHECK (fishing     >= 0)
            );");

        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS schema_migrations (
                migration_id TEXT PRIMARY KEY,
                applied_at   TEXT NOT NULL DEFAULT (datetime('now'))
            );");

        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS players (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                username          TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                password_hash     TEXT    NOT NULL,
                is_admin          INTEGER NOT NULL DEFAULT 0 CHECK (is_admin IN (0, 1)),
                login_count       INTEGER NOT NULL DEFAULT 0,
                created_at        TEXT    NOT NULL DEFAULT (datetime('now')),
                last_login        TEXT,
                tile_x            INTEGER,
                tile_y            INTEGER,
                tile_z            INTEGER,
                combat_levels_id  INTEGER REFERENCES combat_levels(id) ON DELETE SET NULL,
                skill_levels_id   INTEGER REFERENCES skill_levels(id)  ON DELETE SET NULL,
                xp_id             INTEGER REFERENCES player_xp(id)    ON DELETE SET NULL,
                inventory_id      INTEGER REFERENCES player_inventory(id) ON DELETE SET NULL,
                sid               INTEGER NOT NULL DEFAULT 0,
                bank_data         TEXT    NOT NULL DEFAULT '[]',
                bank_capacity     INTEGER NOT NULL DEFAULT 100
            );");

        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS skill_levels (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                player_id       INTEGER NOT NULL UNIQUE REFERENCES players(id) ON DELETE CASCADE,
                woodcutting     INTEGER NOT NULL DEFAULT 1 CHECK (woodcutting     >= 0),
                max_woodcutting INTEGER NOT NULL DEFAULT 1 CHECK (max_woodcutting >= 1),
                mining          INTEGER NOT NULL DEFAULT 1 CHECK (mining          >= 0),
                max_mining      INTEGER NOT NULL DEFAULT 1 CHECK (max_mining      >= 1),
                fishing         INTEGER NOT NULL DEFAULT 1 CHECK (fishing         >= 0),
                max_fishing     INTEGER NOT NULL DEFAULT 1 CHECK (max_fishing     >= 1),
                created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT    NOT NULL DEFAULT (datetime('now'))
            );");

        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS npcs (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                name              TEXT    NOT NULL,
                display           TEXT    NOT NULL,
                tile_x            INTEGER NOT NULL,
                tile_y            INTEGER NOT NULL,
                tile_z            INTEGER NOT NULL,
                playing_animation TEXT    NOT NULL DEFAULT '',
                loot_table_name   TEXT    NOT NULL DEFAULT 'common_loot_1',
                is_dead           INTEGER NOT NULL DEFAULT 0 CHECK (is_dead  IN (0, 1)),
                is_dying          INTEGER NOT NULL DEFAULT 0 CHECK (is_dying IN (0, 1)),
                died_at           TEXT,
                spawn_timer       INTEGER NOT NULL DEFAULT 3,
                spawn_flags       TEXT    NOT NULL DEFAULT '{}',
                ai_package_state  TEXT    NOT NULL DEFAULT 'idle',
                ai_action_state   TEXT    NOT NULL DEFAULT 'none',
                attackable        INTEGER NOT NULL DEFAULT 1 CHECK (attackable IN (0, 1)),
                combat_levels_id  INTEGER REFERENCES combat_levels(id) ON DELETE SET NULL,
                render_slug       TEXT    NOT NULL DEFAULT '',
                dialogue_script   TEXT    NOT NULL DEFAULT '',
                created_at        TEXT    NOT NULL DEFAULT (datetime('now')),
                aggressive        INTEGER NOT NULL DEFAULT 0 CHECK (aggressive IN (0, 1)),
                aggro_range       INTEGER NOT NULL DEFAULT 8 CHECK (aggro_range >= 1),
                is_banker         INTEGER NOT NULL DEFAULT 0 CHECK (is_banker IN (0, 1))
            );");

        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS weapons (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                slug_id         TEXT    NOT NULL UNIQUE,
                display_name    TEXT    NOT NULL,
                attack_speed    INTEGER NOT NULL DEFAULT 5 CHECK (attack_speed >= 1),
                melee_bonus     INTEGER NOT NULL DEFAULT 0 CHECK (melee_bonus  >= 0)
            );");

        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS consumables (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                slug_id         TEXT    NOT NULL UNIQUE,
                display_name    TEXT    NOT NULL,
                effect_type     TEXT    NOT NULL CHECK (effect_type IN ('heal', 'buff', 'utility')),
                heal_amount     INTEGER NOT NULL DEFAULT 0 CHECK (heal_amount >= 0)
            );");

        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS items (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                slug_id         TEXT    NOT NULL UNIQUE,
                display_name    TEXT    NOT NULL,
                type            TEXT    NOT NULL CHECK (type IN ('weapon', 'consumable', 'armor', 'misc')),
                type_slug       TEXT    NOT NULL,
                stackable       INTEGER NOT NULL DEFAULT 0 CHECK (stackable IN (0, 1)),
                max_stack       INTEGER NOT NULL DEFAULT 1 CHECK (max_stack >= 1),
                can_trade    INTEGER NOT NULL DEFAULT 1 CHECK (can_trade IN (0, 1)),
                cert_of_slug TEXT    NOT NULL DEFAULT '',
                non_certable  INTEGER NOT NULL DEFAULT 0 CHECK (non_certable IN (0, 1)),
                non_bankable  INTEGER NOT NULL DEFAULT 0 CHECK (non_bankable IN (0, 1))
            );");

        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS player_inventory (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                slot_1          INTEGER NOT NULL DEFAULT 0,
                slot_2          INTEGER NOT NULL DEFAULT 0,
                slot_3          INTEGER NOT NULL DEFAULT 0,
                slot_4          INTEGER NOT NULL DEFAULT 0,
                slot_5          INTEGER NOT NULL DEFAULT 0,
                slot_6          INTEGER NOT NULL DEFAULT 0,
                slot_7          INTEGER NOT NULL DEFAULT 0,
                slot_8          INTEGER NOT NULL DEFAULT 0,
                slot_9          INTEGER NOT NULL DEFAULT 0,
                slot_10         INTEGER NOT NULL DEFAULT 0,
                slot_11         INTEGER NOT NULL DEFAULT 0,
                slot_12         INTEGER NOT NULL DEFAULT 0,
                slot_13         INTEGER NOT NULL DEFAULT 0,
                slot_14         INTEGER NOT NULL DEFAULT 0,
                slot_15         INTEGER NOT NULL DEFAULT 0,
                slot_16         INTEGER NOT NULL DEFAULT 0,
                slot_17         INTEGER NOT NULL DEFAULT 0,
                slot_18         INTEGER NOT NULL DEFAULT 0,
                slot_19         INTEGER NOT NULL DEFAULT 0,
                slot_20         INTEGER NOT NULL DEFAULT 0,
                slot_21         INTEGER NOT NULL DEFAULT 0,
                slot_22         INTEGER NOT NULL DEFAULT 0,
                slot_23         INTEGER NOT NULL DEFAULT 0,
                slot_24         INTEGER NOT NULL DEFAULT 0,
                slot_25         INTEGER NOT NULL DEFAULT 0,
                slot_26         INTEGER NOT NULL DEFAULT 0,
                slot_27         INTEGER NOT NULL DEFAULT 0,
                slot_28         INTEGER NOT NULL DEFAULT 0
            );");

        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS player_bank (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                slot_1          INTEGER NOT NULL DEFAULT 0,
                slot_2          INTEGER NOT NULL DEFAULT 0,
                slot_3          INTEGER NOT NULL DEFAULT 0,
                slot_4          INTEGER NOT NULL DEFAULT 0,
                slot_5          INTEGER NOT NULL DEFAULT 0,
                slot_6          INTEGER NOT NULL DEFAULT 0,
                slot_7          INTEGER NOT NULL DEFAULT 0,
                slot_8          INTEGER NOT NULL DEFAULT 0,
                slot_9          INTEGER NOT NULL DEFAULT 0,
                slot_10         INTEGER NOT NULL DEFAULT 0,
                slot_11         INTEGER NOT NULL DEFAULT 0,
                slot_12         INTEGER NOT NULL DEFAULT 0,
                slot_13         INTEGER NOT NULL DEFAULT 0,
                slot_14         INTEGER NOT NULL DEFAULT 0,
                slot_15         INTEGER NOT NULL DEFAULT 0,
                slot_16         INTEGER NOT NULL DEFAULT 0,
                slot_17         INTEGER NOT NULL DEFAULT 0,
                slot_18         INTEGER NOT NULL DEFAULT 0,
                slot_19         INTEGER NOT NULL DEFAULT 0,
                slot_20         INTEGER NOT NULL DEFAULT 0,
                slot_21         INTEGER NOT NULL DEFAULT 0,
                slot_22         INTEGER NOT NULL DEFAULT 0,
                slot_23         INTEGER NOT NULL DEFAULT 0,
                slot_24         INTEGER NOT NULL DEFAULT 0,
                slot_25         INTEGER NOT NULL DEFAULT 0,
                slot_26         INTEGER NOT NULL DEFAULT 0,
                slot_27         INTEGER NOT NULL DEFAULT 0,
                slot_28         INTEGER NOT NULL DEFAULT 0
            );");

        // Indexes
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_combat_levels_slug     ON combat_levels (slug_id);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_combat_levels_template ON combat_levels (template_slug);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_skill_levels_player    ON skill_levels  (player_id);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_npcs_tile              ON npcs (tile_x, tile_y);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_npcs_combat            ON npcs (combat_levels_id);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_items_type             ON items (type, type_slug);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_items_slug             ON items (slug_id);");

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS loot_lists (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            slug_id   TEXT    NOT NULL UNIQUE,
            item_bag  TEXT    NOT NULL DEFAULT '{}'
        );");

        // Auto-add columns for older DBs that predate newer schema fields.
        EnsureColumn(conn, "players", "is_admin",         "INTEGER NOT NULL DEFAULT 0 CHECK (is_admin IN (0, 1))");
        EnsureColumn(conn, "players", "login_count",      "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "players", "last_login",       "TEXT");
        EnsureColumn(conn, "players", "tile_x",           "INTEGER");
        EnsureColumn(conn, "players", "tile_y",           "INTEGER");
        EnsureColumn(conn, "players", "tile_z",           "INTEGER");
        EnsureColumn(conn, "players", "combat_levels_id", "INTEGER");
        EnsureColumn(conn, "players", "skill_levels_id",  "INTEGER");
        EnsureColumn(conn, "players", "xp_id",            "INTEGER");
        EnsureColumn(conn, "players", "inventory_id",     "INTEGER");
        EnsureColumn(conn, "players", "bank_id",          "INTEGER");
        EnsureColumn(conn, "npcs",    "spawn_flags",      "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(conn, "npcs",    "ai_package_state", "TEXT NOT NULL DEFAULT 'idle'");
        EnsureColumn(conn, "npcs",    "ai_action_state",  "TEXT NOT NULL DEFAULT 'none'");
        EnsureColumn(conn, "npcs",    "attackable",       "INTEGER NOT NULL DEFAULT 1 CHECK (attackable IN (0, 1))");
        EnsureColumn(conn, "npcs",    "render_slug",      "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "combat_levels", "final_level", "INTEGER NOT NULL DEFAULT 3 CHECK (final_level >= 1)");
        EnsureColumn(conn, "players", "sid", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "npcs", "aggressive",  "INTEGER NOT NULL DEFAULT 0 CHECK (aggressive IN (0, 1))");
        EnsureColumn(conn, "npcs", "aggro_range", "INTEGER NOT NULL DEFAULT 8 CHECK (aggro_range >= 1)");
        EnsureColumn(conn, "combat_levels", "attack_speed", "INTEGER NOT NULL DEFAULT 10 CHECK (attack_speed >= 1)");
        EnsureColumn(conn, "players", "credits",            "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "npcs",    "credits_table_name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "npcs", "dialogue_script", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "items", "can_trade",    "INTEGER NOT NULL DEFAULT 1 CHECK (can_trade IN (0, 1))");
        EnsureColumn(conn, "items", "cert_of_slug", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "players", "bank_data",     "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(conn, "players", "bank_capacity", "INTEGER NOT NULL DEFAULT 100");
        EnsureColumn(conn, "items",   "non_certable",  "INTEGER NOT NULL DEFAULT 0 CHECK (non_certable IN (0, 1))");
        EnsureColumn(conn, "items",   "non_bankable",  "INTEGER NOT NULL DEFAULT 0 CHECK (non_bankable IN (0, 1))");
        EnsureColumn(conn, "npcs",    "is_banker",     "INTEGER NOT NULL DEFAULT 0 CHECK (is_banker IN (0, 1))");

        EnsureCertItems(conn);

        MigratePlayerXpToTenths(conn);
        Exec(conn, "PRAGMA foreign_keys = ON;");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void MigratePlayerXpToTenths(SqliteConnection conn)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM schema_migrations WHERE migration_id = $id;";
            check.Parameters.AddWithValue("$id", PlayerXpTenthsMigration);
            if (check.ExecuteScalar() != null) return;
        }

        using var tx = conn.BeginTransaction();
        try
        {
            using (var check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT 1 FROM schema_migrations WHERE migration_id = $id;";
                check.Parameters.AddWithValue("$id", PlayerXpTenthsMigration);
                if (check.ExecuteScalar() != null)
                {
                    tx.Commit();
                    return;
                }
            }

            using (var scale = conn.CreateCommand())
            {
                scale.Transaction = tx;
                scale.CommandText = @"
                    UPDATE players_xp SET
                        hitpoints = hitpoints * 10,
                        accuracy = accuracy * 10,
                        strength = strength * 10,
                        defence = defence * 10,
                        ether = ether * 10,
                        woodcutting = woodcutting * 10,
                        mining = mining * 10,
                        fishing = fishing * 10;";
                scale.ExecuteNonQuery();
            }

            var rows = new List<(long Id, long[] Xp, int[] Levels)>();
            using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = @"
                    SELECT x.id,
                           x.hitpoints, x.accuracy, x.strength, x.defence,
                           x.ether, x.woodcutting, x.mining, x.fishing,
                           COALESCE(c.max_hitpoints, 10), COALESCE(c.max_accuracy, 1),
                           COALESCE(c.max_strength, 1), COALESCE(c.max_defence, 1),
                           COALESCE(c.max_ether, 1), COALESCE(s.max_woodcutting, 1),
                           COALESCE(s.max_mining, 1), COALESCE(s.max_fishing, 1)
                    FROM players_xp x
                    LEFT JOIN players p ON p.xp_id = x.id
                    LEFT JOIN combat_levels c ON c.id = p.combat_levels_id
                    LEFT JOIN skill_levels s ON s.id = p.skill_levels_id;";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    var xp = new long[8];
                    var levels = new int[8];
                    for (int i = 0; i < xp.Length; i++)
                    {
                        xp[i] = reader.GetInt64(i + 1);
                        levels[i] = reader.GetInt32(i + 9);
                    }
                    rows.Add((reader.GetInt64(0), xp, levels));
                }
            }

            foreach (var row in rows)
            {
                for (int i = 0; i < row.Xp.Length; i++)
                {
                    int level = Math.Clamp(row.Levels[i], 1, SkillExperience.MaxLevel);
                    row.Xp[i] = Math.Max(row.Xp[i], SkillExperience.GetXpForLevelTenths(level));
                }

                using var update = conn.CreateCommand();
                update.Transaction = tx;
                update.CommandText = @"
                    UPDATE players_xp SET
                        hitpoints = $hp, accuracy = $accuracy, strength = $strength,
                        defence = $defence, ether = $ether, woodcutting = $woodcutting,
                        mining = $mining, fishing = $fishing
                    WHERE id = $id;";
                update.Parameters.AddWithValue("$hp", row.Xp[0]);
                update.Parameters.AddWithValue("$accuracy", row.Xp[1]);
                update.Parameters.AddWithValue("$strength", row.Xp[2]);
                update.Parameters.AddWithValue("$defence", row.Xp[3]);
                update.Parameters.AddWithValue("$ether", row.Xp[4]);
                update.Parameters.AddWithValue("$woodcutting", row.Xp[5]);
                update.Parameters.AddWithValue("$mining", row.Xp[6]);
                update.Parameters.AddWithValue("$fishing", row.Xp[7]);
                update.Parameters.AddWithValue("$id", row.Id);
                update.ExecuteNonQuery();
            }

            using (var mark = conn.CreateCommand())
            {
                mark.Transaction = tx;
                mark.CommandText = "INSERT INTO schema_migrations (migration_id) VALUES ($id);";
                mark.Parameters.AddWithValue("$id", PlayerXpTenthsMigration);
                mark.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }


    /// <summary>
    /// Ensures the player has combat_levels, skill_levels, and players_xp rows and
    /// that players links to them. Idempotent: safe to call on any player, any number of times.
    /// Missing rows are created with base stats at 1/1 (hitpoints 10/10), all
    /// bonuses/defenses at 0, status flags false.
    ///
    /// Handles the case where players.combat_levels_id points at a row that
    /// doesn't actually exist (orphan) by re-creating and relinking. Reuses any
    /// existing `player_<id>` combat row rather than duplicating.
    /// Returns true if the player has all three links after the call, false on failure.
    /// </summary>
    public Task<bool> EnsurePlayerStatsAsync(long playerId) => Task.Run(() =>
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        try
        {
            long? currentCombatId = null;
            long? currentSkillId = null;
            long? currentXpId = null;
            using (var check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT combat_levels_id, skill_levels_id, xp_id FROM players WHERE id = $pid;";
                check.Parameters.AddWithValue("$pid", playerId);
                using var reader = check.ExecuteReader();
                if (!reader.Read()) { tx.Rollback(); return false; }
                currentCombatId = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
                currentSkillId  = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                currentXpId     = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
            }

            // Resolve combat row
            long finalCombatId;
            if (currentCombatId.HasValue && RowExists(conn, tx, "combat_levels", currentCombatId.Value))
                finalCombatId = currentCombatId.Value;
            else
                finalCombatId = GetOrCreateCombatRow(conn, tx, playerId);

            // Resolve skill row
            long finalSkillId;
            if (currentSkillId.HasValue && RowExists(conn, tx, "skill_levels", currentSkillId.Value))
                finalSkillId = currentSkillId.Value;
            else
                finalSkillId = GetOrCreateSkillRow(conn, tx, playerId);

            // Resolve XP row
            long finalXpId = GetOrCreateXpRow(conn, tx, currentXpId, finalCombatId, finalSkillId);

            // Link all three rows back onto players
            using (var link = conn.CreateCommand())
            {
                link.Transaction = tx;
                link.CommandText = "UPDATE players SET combat_levels_id = $cid, skill_levels_id = $sid, xp_id = $xid WHERE id = $pid;";
                link.Parameters.AddWithValue("$cid", finalCombatId);
                link.Parameters.AddWithValue("$sid", finalSkillId);
                link.Parameters.AddWithValue("$xid", finalXpId);
                link.Parameters.AddWithValue("$pid", playerId);
                link.ExecuteNonQuery();
            }

            // Verify all links are populated
            using (var verify = conn.CreateCommand())
            {
                verify.Transaction = tx;
                verify.CommandText = "SELECT combat_levels_id, skill_levels_id, xp_id FROM players WHERE id = $pid;";
                verify.Parameters.AddWithValue("$pid", playerId);
                using var reader = verify.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                {
                    tx.Rollback();
                    return false;
                }
            }

            tx.Commit();
            return true;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    });

    public Task<bool> EnsurePlayerInventoryAsync(long playerId) => Task.Run(() =>
{
    using var conn = Open();
    using var tx = conn.BeginTransaction();
    try
    {
        long? invId = null;
        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT inventory_id FROM players WHERE id = $pid;";
            check.Parameters.AddWithValue("$pid", playerId);
            using var r = check.ExecuteReader();
            if (!r.Read()) { tx.Rollback(); return false; }
            invId = r.IsDBNull(0) ? null : r.GetInt64(0);
        }

        long finalInv = invId.HasValue && RowExists(conn, tx, "player_inventory", invId.Value)
            ? invId.Value
            : InsertEmptyRow(conn, tx, "player_inventory");

        using (var link = conn.CreateCommand())
        {
            link.Transaction = tx;
            link.CommandText = "UPDATE players SET inventory_id = $i WHERE id = $pid;";
            link.Parameters.AddWithValue("$i", finalInv);
            link.Parameters.AddWithValue("$pid", playerId);
            link.ExecuteNonQuery();
        }

        using (var verify = conn.CreateCommand())
        {
            verify.Transaction = tx;
            verify.CommandText = "SELECT inventory_id FROM players WHERE id = $pid;";
            verify.Parameters.AddWithValue("$pid", playerId);
            using var r = verify.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0)) { tx.Rollback(); return false; }
        }

        tx.Commit();
        return true;
    }
    catch
    {
        try { tx.Rollback(); } catch { }
        throw;
    }
});

    private static long InsertEmptyRow(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {table} DEFAULT VALUES RETURNING id;";
        return (long)cmd.ExecuteScalar();
    }

    private static bool RowExists(SqliteConnection conn, SqliteTransaction tx, string table, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT 1 FROM {table} WHERE id = $id;";   // table name is a hardcoded literal
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() != null;
    }

    private static long GetOrCreateCombatRow(SqliteConnection conn, SqliteTransaction tx, long playerId)
    {
        string slug = $"player_{playerId}";

        // Reuse an existing row if one already has this slug (orphan recovery).
        using (var existing = conn.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT id FROM combat_levels WHERE slug_id = $slug;";
            existing.Parameters.AddWithValue("$slug", slug);
            var r = existing.ExecuteScalar();
            if (r != null) return (long)r;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO combat_levels
                (slug_id, template_slug,
                hitpoints, max_hitpoints,
                accuracy, max_accuracy,
                strength, max_strength,
                defence, max_defence,
                ether, max_ether,
                melee_bonus, ranged_bonus, ether_bonus,
                melee_defense, ranged_defense, ether_defense,
                poisoned, burning, frozen)
            VALUES
                ($slug, NULL,
                10, 10,
                1, 1,
                1, 1,
                1, 1,
                1, 1,
                0, 0, 0,
                0, 0, 0,
                0, 0, 0)
            RETURNING id;";
        cmd.Parameters.AddWithValue("$slug", slug);
        return (long)cmd.ExecuteScalar();
    }

    private static long GetOrCreateSkillRow(SqliteConnection conn, SqliteTransaction tx, long playerId)
    {
        using (var existing = conn.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT id FROM skill_levels WHERE player_id = $pid;";
            existing.Parameters.AddWithValue("$pid", playerId);
            var r = existing.ExecuteScalar();
            if (r != null) return (long)r;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO skill_levels
                (player_id, woodcutting, max_woodcutting, mining, max_mining, fishing, max_fishing)
            VALUES
                ($pid, 1, 1, 1, 1, 1, 1)
            RETURNING id;";
        cmd.Parameters.AddWithValue("$pid", playerId);
        return (long)cmd.ExecuteScalar();
    }

    private static long GetOrCreateXpRow(
        SqliteConnection conn, SqliteTransaction tx, long? currentXpId, long combatId, long skillId)
    {
        if (currentXpId.HasValue && RowExists(conn, tx, "player_xp", currentXpId.Value))
            return currentXpId.Value;

        var levels = new int[8];
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = @"
                SELECT c.max_hitpoints, c.max_accuracy, c.max_strength, c.max_defence,
                       c.max_ether, s.max_woodcutting, s.max_mining, s.max_fishing
                FROM combat_levels c CROSS JOIN skill_levels s
                WHERE c.id = $combatId AND s.id = $skillId;";
            read.Parameters.AddWithValue("$combatId", combatId);
            read.Parameters.AddWithValue("$skillId", skillId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("Cannot initialize XP for missing player stat rows.");
            for (int i = 0; i < levels.Length; i++)
                levels[i] = Math.Clamp(reader.GetInt32(i), 1, SkillExperience.MaxLevel);
        }

        var xp = levels.Select(SkillExperience.GetXpForLevelTenths).ToArray();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = @"
            INSERT INTO player_xp
                (hitpoints, accuracy, strength, defence, ether, woodcutting, mining, fishing)
            VALUES
                ($hp, $accuracy, $strength, $defence, $ether, $woodcutting, $mining, $fishing)
            RETURNING id;";
        insert.Parameters.AddWithValue("$hp", xp[0]);
        insert.Parameters.AddWithValue("$accuracy", xp[1]);
        insert.Parameters.AddWithValue("$strength", xp[2]);
        insert.Parameters.AddWithValue("$defence", xp[3]);
        insert.Parameters.AddWithValue("$ether", xp[4]);
        insert.Parameters.AddWithValue("$woodcutting", xp[5]);
        insert.Parameters.AddWithValue("$mining", xp[6]);
        insert.Parameters.AddWithValue("$fishing", xp[7]);
        return (long)insert.ExecuteScalar();
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using (var info = conn.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            using var reader = info.ExecuteReader();
            bool found = false;
            while (reader.Read())
            {
                if (reader.GetString(1) == column) { found = true; break; }
            }
            if (found) return;
        }
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

        /// <summary>
    /// One-shot rename: loot -> loot_lists. No-op if loot is already gone or
    /// loot_lists already exists. Runs with FKs enabled so any future REFERENCES
    /// clauses get updated automatically.
    /// </summary>
    private static void MigrateLootToLootLists(SqliteConnection conn)
    {
        if (!TableExists(conn, "loot") || TableExists(conn, "loot_lists")) return;
        Exec(conn, "ALTER TABLE loot RENAME TO loot_lists;");
        GD.Print("[Server] Renamed table: loot -> loot_lists");
    }

    /// <summary>
    /// One-shot rename: players_xp -> player_xp. Runs with FKs enabled so
    /// SQLite rewrites players.xp_id REFERENCES players_xp(id) to point at
    /// player_xp(id). Without FKs on, that reference would be stale and any
    /// INSERT to players with a non-NULL xp_id would fail FK checks.
    /// </summary>
    private static void MigratePlayersXpToPlayerXp(SqliteConnection conn)
    {
        if (!TableExists(conn, "players_xp") || TableExists(conn, "player_xp")) return;
        Exec(conn, "ALTER TABLE players_xp RENAME TO player_xp;");
        GD.Print("[Server] Renamed table: players_xp -> player_xp");
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    public string GetSqliteVersion()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        return (string)cmd.ExecuteScalar();
    }

    public Task<string> GetSqliteVersionAsync() => Task.Run(GetSqliteVersion);

    public Task<long> CountPlayersAsync() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM players;";
        return (long)cmd.ExecuteScalar();
    });

    private (bool Created, long Id) TryCreatePlayer(string username, string passwordHash, bool isAdmin)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        try
        {
            long xpId;
            using (var xp = conn.CreateCommand())
            {
                xp.Transaction = tx;
                xp.CommandText = "INSERT INTO player_xp (hitpoints) VALUES ($hpXp) RETURNING id;";
                xp.Parameters.AddWithValue("$hpXp", SkillExperience.GetXpForLevelTenths(10));
                xpId = (long)xp.ExecuteScalar();
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = isAdmin
                ? "INSERT INTO players (username, password_hash, is_admin, xp_id) VALUES ($u, $h, 1, $xpId) RETURNING id;"
                : "INSERT INTO players (username, password_hash, xp_id) VALUES ($u, $h, $xpId) RETURNING id;";
            cmd.Parameters.AddWithValue("$u", username);
            cmd.Parameters.AddWithValue("$h", passwordHash);
            cmd.Parameters.AddWithValue("$xpId", xpId);
            long playerId = (long)cmd.ExecuteScalar();
            tx.Commit();
            return (true, playerId);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraintError)
        {
            try { tx.Rollback(); } catch { }
            return (false, 0L);
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    public Task<(bool Created, long Id)> TryCreatePlayerAsync(string username, string passwordHash, bool isAdmin = false)
        => Task.Run(() => TryCreatePlayer(username, passwordHash, isAdmin));

    public Task<PlayerRecord> GetPlayerByUsernameAsync(string username) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, is_admin FROM players WHERE username = $u;";
        cmd.Parameters.AddWithValue("$u", username);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new PlayerRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0);
    });

    /// <summary>Reads the player's inventory as 28 packed longs. Empty slots = 0.</summary>
    public Task<long[]?> GetPlayerInventoryAsync(long playerId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT i.slot_1, i.slot_2, i.slot_3, i.slot_4, i.slot_5, i.slot_6, i.slot_7,
                   i.slot_8, i.slot_9, i.slot_10, i.slot_11, i.slot_12, i.slot_13, i.slot_14,
                   i.slot_15, i.slot_16, i.slot_17, i.slot_18, i.slot_19, i.slot_20, i.slot_21,
                   i.slot_22, i.slot_23, i.slot_24, i.slot_25, i.slot_26, i.slot_27, i.slot_28
            FROM players p
            JOIN player_inventory i ON p.inventory_id = i.id
            WHERE p.id = $pid;";
        cmd.Parameters.AddWithValue("$pid", playerId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var slots = new long[28];
        for (int i = 0; i < 28; i++) slots[i] = r.GetInt64(i);
        return slots;
    });

    public Task<long> RecordLoginAsync(long playerId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE players
            SET login_count = login_count + 1, last_login = datetime('now')
            WHERE id = $id
            RETURNING login_count;";
        cmd.Parameters.AddWithValue("$id", playerId);
        return (long)cmd.ExecuteScalar();
    });

    // TEMP-DEV: tile position read/write.
    public Task<(int X, int Y, int Z)?> GetPlayerTileAsync(long playerId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tile_x, tile_y, tile_z FROM players WHERE id = $id AND tile_x IS NOT NULL;";
        cmd.Parameters.AddWithValue("$id", playerId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return ((int, int, int)?)null;
        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2));
    });

    public Task SetPlayerTileAsync(long playerId, int x, int y, int z) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE players SET tile_x = $x, tile_y = $y, tile_z = $z WHERE id = $id;";
        cmd.Parameters.AddWithValue("$x", x);
        cmd.Parameters.AddWithValue("$y", y);
        cmd.Parameters.AddWithValue("$z", z);
        cmd.Parameters.AddWithValue("$id", playerId);
        cmd.ExecuteNonQuery();
    });

    public Task<PlayerRecord> GetFirstAdminAsync() => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, is_admin FROM players WHERE is_admin = 1 ORDER BY id LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new PlayerRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0);
    });

    public Task<bool> TryCreateDevAdminAsync(string username, string passwordHash) => Task.Run(() =>
    {
        return TryCreatePlayer(username, passwordHash, isAdmin: true).Created;
    });

public long GetMaxSid()
{
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COALESCE(MAX(sid), 0) FROM players;";
    return (long)cmd.ExecuteScalar();
}

public Task SetPlayerSidAsync(long playerId, long sid) => Task.Run(() =>
{
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE players SET sid = $sid WHERE id = $id;";
    cmd.Parameters.AddWithValue("$sid", sid);
    cmd.Parameters.AddWithValue("$id", playerId);
    cmd.ExecuteNonQuery();
});

    // ---------- Combat ----------

    public record PlayerCombatData(
        int Accuracy, int Strength, int Defence,
        int MeleeBonus, int MeleeDefense,
        int CurrentHp, int MaxHp, int FinalLevel);
    
    public Task SetNpcHpAsync(long npcId, int hp) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE combat_levels SET hitpoints = $hp
            WHERE id = (SELECT combat_levels_id FROM npcs WHERE id = $id);";
        cmd.Parameters.AddWithValue("$hp", hp);
        cmd.Parameters.AddWithValue("$id", npcId);
        cmd.ExecuteNonQuery();
    });

    public Task SetPlayerHpAsync(long playerId, int hp) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE combat_levels SET hitpoints = $hp
            WHERE id = (SELECT combat_levels_id FROM players WHERE id = $id);";
        cmd.Parameters.AddWithValue("$hp", hp);
        cmd.Parameters.AddWithValue("$id", playerId);
        cmd.ExecuteNonQuery();
    });

    public Task AwardCombatXpAsync(long playerId, int damage, int accuracyLevel, int hitpointsLevel)
        => Task.Run(() =>
    {
        if (damage <= 0) return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        try
        {
            using (var xp = conn.CreateCommand())
            {
                xp.Transaction = tx;
                xp.CommandText = @"
                    UPDATE player_xp SET
                        accuracy = accuracy + $accuracyXp,
                        hitpoints = hitpoints + $hitpointsXp
                    WHERE id = (SELECT xp_id FROM players WHERE id = $playerId);";
                xp.Parameters.AddWithValue("$accuracyXp", (long)damage * SkillExperience.AccuracyXpTenthsPerDamage);
                xp.Parameters.AddWithValue("$hitpointsXp", (long)damage * SkillExperience.HitpointsXpTenthsPerDamage);
                xp.Parameters.AddWithValue("$playerId", playerId);
                if (xp.ExecuteNonQuery() == 0)
                {
                    tx.Rollback();
                    return;
                }
            }

            using (var levels = conn.CreateCommand())
            {
                levels.Transaction = tx;
                levels.CommandText = @"
                    UPDATE combat_levels SET
                        accuracy = MAX(accuracy, $accuracyLevel),
                        max_accuracy = MAX(max_accuracy, $accuracyLevel),
                        max_hitpoints = MAX(max_hitpoints, $hitpointsLevel)
                    WHERE id = (SELECT combat_levels_id FROM players WHERE id = $playerId);";
                levels.Parameters.AddWithValue("$accuracyLevel", accuracyLevel);
                levels.Parameters.AddWithValue("$hitpointsLevel", hitpointsLevel);
                levels.Parameters.AddWithValue("$playerId", playerId);
                levels.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    });

public Task<long[]?> GetInventorySlotsAsync(long playerId) => Task.Run(() =>
{
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT i.slot_1, i.slot_2, i.slot_3, i.slot_4, i.slot_5, i.slot_6, i.slot_7,
               i.slot_8, i.slot_9, i.slot_10, i.slot_11, i.slot_12, i.slot_13, i.slot_14,
               i.slot_15, i.slot_16, i.slot_17, i.slot_18, i.slot_19, i.slot_20, i.slot_21,
               i.slot_22, i.slot_23, i.slot_24, i.slot_25, i.slot_26, i.slot_27, i.slot_28
        FROM players p
        JOIN player_inventory i ON p.inventory_id = i.id
        WHERE p.id = $pid;";
    cmd.Parameters.AddWithValue("$pid", playerId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return null;
    var slots = new long[28];
    for (int i = 0; i < 28; i++) slots[i] = r.GetInt64(i);
    return slots;
});

public Task SaveInventorySlotAsync(long playerId, int slotIndex, long packed) => Task.Run(() =>
{
    if (slotIndex < 0 || slotIndex >= 28) return;
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $@"
        UPDATE player_inventory SET slot_{slotIndex + 1} = $v
        WHERE id = (SELECT inventory_id FROM players WHERE id = $pid);";
    cmd.Parameters.AddWithValue("$v", packed);
    cmd.Parameters.AddWithValue("$pid", playerId);
    cmd.ExecuteNonQuery();
});

public bool SaveInventorySlotSync(long playerId, int slotIndex, long packed)
{
    if (slotIndex < 0 || slotIndex >= 28) return false;
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $@"
        UPDATE player_inventory SET slot_{slotIndex + 1} = $v
        WHERE id = (SELECT inventory_id FROM players WHERE id = $pid);";
    cmd.Parameters.AddWithValue("$v", packed);
    cmd.Parameters.AddWithValue("$pid", playerId);
    return cmd.ExecuteNonQuery() == 1;
}

public bool CommitTradeSync(long playerAId, long[] inventoryA, int creditsA,
    long playerBId, long[] inventoryB, int creditsB)
{
    if (playerAId == playerBId || inventoryA.Length != 28 || inventoryB.Length != 28 || creditsA < 0 || creditsB < 0)
        return false;

    using var conn = Open();
    using var tx = conn.BeginTransaction();
    try
    {
        string slotAssignments = string.Join(", ", Enumerable.Range(1, 28).Select(slot => $"slot_{slot} = $slot{slot}"));
        if (!SaveTradeInventory(conn, tx, playerAId, inventoryA, slotAssignments)
            || !SaveTradeInventory(conn, tx, playerBId, inventoryB, slotAssignments)
            || !SaveTradeCredits(conn, tx, playerAId, creditsA)
            || !SaveTradeCredits(conn, tx, playerBId, creditsB))
        {
            tx.Rollback();
            return false;
        }

        tx.Commit();
        return true;
    }
    catch
    {
        try { tx.Rollback(); } catch { }
        throw;
    }
}

private static bool SaveTradeInventory(SqliteConnection conn, SqliteTransaction tx,
    long playerId, long[] inventory, string slotAssignments)
{
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = $"UPDATE player_inventory SET {slotAssignments} WHERE id = (SELECT inventory_id FROM players WHERE id = $playerId);";
    for (int i = 0; i < inventory.Length; i++)
        cmd.Parameters.AddWithValue($"$slot{i + 1}", inventory[i]);
    cmd.Parameters.AddWithValue("$playerId", playerId);
    return cmd.ExecuteNonQuery() == 1;
}

private static bool SaveTradeCredits(SqliteConnection conn, SqliteTransaction tx, long playerId, int credits)
{
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = "UPDATE players SET credits = $credits WHERE id = $playerId;";
    cmd.Parameters.AddWithValue("$credits", credits);
    cmd.Parameters.AddWithValue("$playerId", playerId);
    return cmd.ExecuteNonQuery() == 1;
}

public record ItemMeta(
    long Id, string Slug, string Display, string Type, string TypeSlug,
    bool Stackable, bool CanTrade, string CertOfSlug,
    bool NonCertable, bool NonBankable);

public Task<List<ItemMeta>> GetAllItemsSync() => Task.Run(() =>
{
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT id, slug_id, display_name, type, type_slug,
                            stackable, can_trade, cert_of_slug,
                            non_certable, non_bankable FROM items;";
    using var r = cmd.ExecuteReader();
    var list = new List<ItemMeta>();
    while (r.Read())
        list.Add(new ItemMeta(
            r.GetInt64(0), r.GetString(1), r.GetString(2),
            r.GetString(3), r.GetString(4),
            r.GetInt64(5) != 0, r.GetInt64(6) != 0,
            r.IsDBNull(7) ? "" : r.GetString(7),
            r.GetInt64(8) != 0, r.GetInt64(9) != 0));
    return list;
});

public Dictionary<string, Dictionary<string, (int Min, int Max)>> GetAllLootTablesSync()
{
    var result = new Dictionary<string, Dictionary<string, (int Min, int Max)>>();
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT slug_id, item_bag FROM loot_lists;";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        string slug = r.GetString(0);
        string json = r.GetString(1);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var bag = new Dictionary<string, (int Min, int Max)>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                    && prop.Value.TryGetInt32(out int fixedQty)
                    && fixedQty > 0)
                {
                    bag[prop.Name] = (fixedQty, fixedQty);
                }
                else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array
                    && prop.Value.GetArrayLength() == 2
                    && prop.Value[0].TryGetInt32(out int min)
                    && prop.Value[1].TryGetInt32(out int max)
                    && min >= 0 && max >= min)
                {
                    bag[prop.Name] = (min, max);
                }
                else
                {
                    GD.PrintErr($"[Database] Loot table '{slug}': invalid entry '{prop.Name}', skipping");
                }
            }
            result[slug] = bag;
        }
        catch (System.Text.Json.JsonException e)
        {
            GD.PrintErr($"[Database] Loot table '{slug}' parse error: {e.Message}");
        }
    }
    return result;
}

public Task<int> GetPlayerCreditsAsync(long playerId) => Task.Run(() =>
{
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT credits FROM players WHERE id = $id;";
    cmd.Parameters.AddWithValue("$id", playerId);
    var r = cmd.ExecuteScalar();
    return r != null ? Convert.ToInt32(r) : 0;
});

public Task SetPlayerCreditsAsync(long playerId, int credits) => Task.Run(() =>
{
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE players SET credits = $c WHERE id = $id;";
    cmd.Parameters.AddWithValue("$c", credits);
    cmd.Parameters.AddWithValue("$id", playerId);
    cmd.ExecuteNonQuery();
});

public bool SetPlayerCreditsSync(long playerId, int credits)
{
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE players SET credits = $c WHERE id = $id;";
    cmd.Parameters.AddWithValue("$c", credits);
    cmd.Parameters.AddWithValue("$id", playerId);
    return cmd.ExecuteNonQuery() == 1;
}

public Dictionary<string, (int Min, int Max)> GetAllCreditsLootSync()
{
    var result = new Dictionary<string, (int, int)>();
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT slug_id, min_credits, max_credits FROM credits_loot;";
    using var r = cmd.ExecuteReader();
    while (r.Read())
        result[r.GetString(0)] = (r.GetInt32(1), r.GetInt32(2));
    return result;
}

    public Task SetPlayerCombatStatsAsync(long playerId, int accuracy, int strength, int defence,
        int meleeBonus, int meleeDefense) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE combat_levels SET
                accuracy = $acc, max_accuracy = $acc,
                strength = $str, max_strength = $str,
                defence  = $def, max_defence  = $def,
                melee_bonus = $mb, melee_defense = $md
            WHERE id = (SELECT combat_levels_id FROM players WHERE id = $pid);";
        cmd.Parameters.AddWithValue("$acc", accuracy);
        cmd.Parameters.AddWithValue("$str", strength);
        cmd.Parameters.AddWithValue("$def", defence);
        cmd.Parameters.AddWithValue("$mb", meleeBonus);
        cmd.Parameters.AddWithValue("$md", meleeDefense);
        cmd.Parameters.AddWithValue("$pid", playerId);
        cmd.ExecuteNonQuery();
    });

    public Task<PlayerCombatData?> GetPlayerCombatAsync(long playerId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.accuracy, c.strength, c.defence,
                c.melee_bonus, c.melee_defense,
                c.hitpoints, c.max_hitpoints, c.final_level
            FROM players p
            JOIN combat_levels c ON p.combat_levels_id = c.id
            WHERE p.id = $id;";
        cmd.Parameters.AddWithValue("$id", playerId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new PlayerCombatData(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4),
            reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7));
    });

    /// <summary>Skill ids in display order, with combat skills before gathering skills.</summary>
    public static readonly string[] SkillIds =
        { "hitpoints", "accuracy", "strength", "defence", "ether", "woodcutting", "mining", "fishing" };

    public record SkillLevel(string Id, int Level, int Max, long CurrentXpTenths);

    public Task<List<SkillLevel>> GetPlayerSkillsAsync(long playerId) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.hitpoints, c.max_hitpoints, COALESCE(x.hitpoints, 0),
                   c.accuracy, c.max_accuracy, COALESCE(x.accuracy, 0),
                   c.strength, c.max_strength, COALESCE(x.strength, 0),
                   c.defence, c.max_defence, COALESCE(x.defence, 0),
                   c.ether, c.max_ether, COALESCE(x.ether, 0),
                   s.woodcutting, s.max_woodcutting, COALESCE(x.woodcutting, 0),
                   s.mining, s.max_mining, COALESCE(x.mining, 0),
                   s.fishing, s.max_fishing, COALESCE(x.fishing, 0)
            FROM players p
            JOIN combat_levels c ON p.combat_levels_id = c.id
            JOIN skill_levels s ON p.skill_levels_id = s.id
            LEFT JOIN player_xp x ON p.xp_id = x.id
            WHERE p.id = $id;";
        cmd.Parameters.AddWithValue("$id", playerId);

        var result = new List<SkillLevel>();
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return result;

        for (int i = 0; i < SkillIds.Length; i++)
        {
            int column = i * 3;
            result.Add(new SkillLevel(
                SkillIds[i],
                reader.GetInt32(column),
                reader.GetInt32(column + 1),
                reader.GetInt64(column + 2)));
        }
        return result;
    });

    public Task<List<ActiveNpc>> GetAllNpcsAsync() => Task.Run(GetAllNpcsSync);

public List<ActiveNpc> GetAllNpcsSync()
{
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT n.id, n.name, n.display, n.tile_x, n.tile_y, n.tile_z,
               n.playing_animation, n.is_dead, n.attackable,
               n.combat_levels_id, COALESCE(c.final_level, 1) AS final_level,
               n.render_slug, n.dialogue_script, n.ai_package_state, n.spawn_timer,
               COALESCE(c.hitpoints, 3) AS hitpoints,
               COALESCE(c.max_hitpoints, 3) AS max_hitpoints,
               COALESCE(c.accuracy, 1) AS accuracy,
               COALESCE(c.strength, 1) AS strength,
               COALESCE(c.defence, 1) AS defence,
               COALESCE(c.ether, 1) AS ether,
               COALESCE(c.max_ether, 1) AS max_ether,
               COALESCE(c.melee_bonus, 0) AS melee_bonus,
               COALESCE(c.ranged_bonus, 0) AS ranged_bonus,
               COALESCE(c.ether_bonus, 0) AS ether_bonus,
               COALESCE(c.melee_defense, 0) AS melee_defense,
               COALESCE(c.ranged_defense, 0) AS ranged_defense,
               COALESCE(c.ether_defense, 0) AS ether_defense,
               COALESCE(c.attack_speed, 10) AS attack_speed,
               n.loot_table_name, n.ai_action_state,
               n.aggressive, n.aggro_range, n.is_banker AS is_banker, n.credits_table_name
        FROM npcs n
        LEFT JOIN combat_levels c ON n.combat_levels_id = c.id;";
    using var reader = cmd.ExecuteReader();
    var list = new List<ActiveNpc>();
    while (reader.Read())
    {
        var npc = new ActiveNpc
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Display = reader.GetString(reader.GetOrdinal("display")),
            TileX = (int)reader.GetInt64(reader.GetOrdinal("tile_x")),
            TileY = (int)reader.GetInt64(reader.GetOrdinal("tile_y")),
            TileZ = (int)reader.GetInt64(reader.GetOrdinal("tile_z")),
            PlayingAnimation = reader.GetString(reader.GetOrdinal("playing_animation")),
            IsDead = reader.GetInt64(reader.GetOrdinal("is_dead")) != 0,
            Attackable = reader.GetInt64(reader.GetOrdinal("attackable")) != 0,
            CombatLevelsId = reader.IsDBNull(reader.GetOrdinal("combat_levels_id"))
                ? null : reader.GetInt64(reader.GetOrdinal("combat_levels_id")),
            FinalLevel = reader.GetInt32(reader.GetOrdinal("final_level")),
            RenderSlug = reader.IsDBNull(reader.GetOrdinal("render_slug"))
                ? "" : reader.GetString(reader.GetOrdinal("render_slug")),
            DialogueScript = reader.IsDBNull(reader.GetOrdinal("dialogue_script"))
                ? "" : reader.GetString(reader.GetOrdinal("dialogue_script")),
            AiPackageState = reader.IsDBNull(reader.GetOrdinal("ai_package_state"))
                ? "idle" : reader.GetString(reader.GetOrdinal("ai_package_state")),
            SpawnTimerSeconds = (int)reader.GetInt64(reader.GetOrdinal("spawn_timer")),
            Hitpoints = reader.GetInt32(reader.GetOrdinal("hitpoints")),
            MaxHitpoints = reader.GetInt32(reader.GetOrdinal("max_hitpoints")),
            Accuracy = reader.GetInt32(reader.GetOrdinal("accuracy")),
            Strength = reader.GetInt32(reader.GetOrdinal("strength")),
            Defence = reader.GetInt32(reader.GetOrdinal("defence")),
            Ether = reader.GetInt32(reader.GetOrdinal("ether")),
            MaxEther = reader.GetInt32(reader.GetOrdinal("max_ether")),
            MeleeBonus = reader.GetInt32(reader.GetOrdinal("melee_bonus")),
            RangedBonus = reader.GetInt32(reader.GetOrdinal("ranged_bonus")),
            EtherBonus = reader.GetInt32(reader.GetOrdinal("ether_bonus")),
            MeleeDefense = reader.GetInt32(reader.GetOrdinal("melee_defense")),
            RangedDefense = reader.GetInt32(reader.GetOrdinal("ranged_defense")),
            EtherDefense = reader.GetInt32(reader.GetOrdinal("ether_defense")),
            AttackSpeedTicks = reader.GetInt32(reader.GetOrdinal("attack_speed")),
            LootTableName = reader.IsDBNull(reader.GetOrdinal("loot_table_name"))
                ? "common_loot_1" : reader.GetString(reader.GetOrdinal("loot_table_name")),
            AiActionState = reader.IsDBNull(reader.GetOrdinal("ai_action_state"))
                ? "none" : reader.GetString(reader.GetOrdinal("ai_action_state")),
            Aggressive = reader.GetInt64(reader.GetOrdinal("aggressive")) != 0,
            AggroRangeTiles = reader.GetInt32(reader.GetOrdinal("aggro_range")),
            IsBanker = reader.GetInt64(reader.GetOrdinal("is_banker")) != 0,
            CreditsTableName = reader.IsDBNull(reader.GetOrdinal("credits_table_name"))
                ? "" : reader.GetString(reader.GetOrdinal("credits_table_name"))
        };
        npc.ResetPositionToHome();
        list.Add(npc);
    }
    return list;
}

    /// <summary>
    /// On server startup: any NPC flagged is_dead or is_dying from a previous
    /// session is brought back to life. Full HP, no status effects, no died_at.
    /// Chunk activation re-evaluates normally afterwards.
    /// </summary>
    public int RespawnAllDeadNpcsSync()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE combat_levels SET
                    hitpoints = max_hitpoints,
                    accuracy  = max_accuracy,
                    strength  = max_strength,
                    defence   = max_defence,
                    ether     = max_ether,
                    poisoned  = 0, burning = 0, frozen = 0
                WHERE id IN (
                    SELECT combat_levels_id FROM npcs
                    WHERE (is_dead = 1 OR is_dying = 1) AND combat_levels_id IS NOT NULL
                );";
            cmd.ExecuteNonQuery();
        }

        int count;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE npcs SET is_dead = 0, is_dying = 0, died_at = NULL
                WHERE is_dead = 1 OR is_dying = 1;";
            count = cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return count;
    }


    public Task MarkNpcDeadAsync(long npcId) => Task.Run(() =>
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE npcs SET is_dead = 1, is_dying = 0, died_at = datetime('now') WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", npcId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE combat_levels SET hitpoints = 0, poisoned = 0, burning = 0, frozen = 0
                WHERE id = (SELECT combat_levels_id FROM npcs WHERE id = $id);";
            cmd.Parameters.AddWithValue("$id", npcId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    });

    public Task ReviveNpcAsync(long npcId) => Task.Run(() =>
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE npcs SET is_dead = 0, is_dying = 0, died_at = NULL WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", npcId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE combat_levels SET
                    hitpoints = max_hitpoints,
                    accuracy = max_accuracy,
                    strength = max_strength,
                    defence = max_defence,
                    ether = max_ether,
                    poisoned = 0, burning = 0, frozen = 0
                WHERE id = (SELECT combat_levels_id FROM npcs WHERE id = $id);";
            cmd.Parameters.AddWithValue("$id", npcId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    });

    public Task<bool> SaveAdminNpcEditAsync(long npcId, Dictionary<string, object> u) => Task.Run(() =>
{
    using var conn = Open();
    using var tx = conn.BeginTransaction();
    try
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE npcs SET
                    attackable = $atk,
                    aggressive = $aggr,
                    aggro_range = $arange,
                    ai_package_state = $pkg,
                    ai_action_state  = $act,
                    loot_table_name  = $loot,
                    spawn_timer      = $stimer,
                    display          = $display,
                    dialogue_script  = $dialogue_script,
                    is_banker        = $is_banker
                WHERE id = $id;";
            cmd.Parameters.AddWithValue("$atk", (bool)u["attackable"] ? 1 : 0);
            cmd.Parameters.AddWithValue("$aggr", (bool)u["aggressive"] ? 1 : 0);
            cmd.Parameters.AddWithValue("$arange", Convert.ToInt32(u["aggro_range"]));
            cmd.Parameters.AddWithValue("$pkg", (string)u["ai_package_state"]);
            cmd.Parameters.AddWithValue("$act", (string)u["ai_action_state"]);
            cmd.Parameters.AddWithValue("$loot", (string)u["loot_table_name"]);
            cmd.Parameters.AddWithValue("$stimer", Convert.ToInt32(u["spawn_timer"]));
            cmd.Parameters.AddWithValue("$display", (string)u["display"]);
            cmd.Parameters.AddWithValue("$dialogue_script", (string)u["dialogue_script"]);
            cmd.Parameters.AddWithValue("$is_banker", (bool)u["is_banker"] ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", npcId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE combat_levels SET
                    hitpoints     = $hp, max_hitpoints = $mhp,
                    accuracy      = $acc, max_accuracy  = $acc,
                    strength      = $str, max_strength  = $str,
                    defence       = $def, max_defence   = $def,
                    ether         = $eth, max_ether     = $meth,
                    melee_bonus   = $mb,  ranged_bonus  = $rb,
                    ether_bonus   = $eb,
                    melee_defense = $md,  ranged_defense = $rd,
                    ether_defense = $ed,
                    attack_speed  = $aspeed
                WHERE id = (SELECT combat_levels_id FROM npcs WHERE id = $id);";
            cmd.Parameters.AddWithValue("$hp",     Convert.ToInt32(u["hitpoints"]));
            cmd.Parameters.AddWithValue("$mhp",    Convert.ToInt32(u["max_hitpoints"]));
            cmd.Parameters.AddWithValue("$acc",    Convert.ToInt32(u["accuracy"]));
            cmd.Parameters.AddWithValue("$str",    Convert.ToInt32(u["strength"]));
            cmd.Parameters.AddWithValue("$def",    Convert.ToInt32(u["defence"]));
            cmd.Parameters.AddWithValue("$eth",    Convert.ToInt32(u["ether"]));
            cmd.Parameters.AddWithValue("$meth",   Convert.ToInt32(u["max_ether"]));
            cmd.Parameters.AddWithValue("$mb",     Convert.ToInt32(u["melee_bonus"]));
            cmd.Parameters.AddWithValue("$rb",     Convert.ToInt32(u["ranged_bonus"]));
            cmd.Parameters.AddWithValue("$eb",     Convert.ToInt32(u["ether_bonus"]));
            cmd.Parameters.AddWithValue("$md",     Convert.ToInt32(u["melee_defense"]));
            cmd.Parameters.AddWithValue("$rd",     Convert.ToInt32(u["ranged_defense"]));
            cmd.Parameters.AddWithValue("$ed",     Convert.ToInt32(u["ether_defense"]));
            cmd.Parameters.AddWithValue("$aspeed", Convert.ToInt32(u["attack_speed"]));
            cmd.Parameters.AddWithValue("$id", npcId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }
    catch
    {
        try { tx.Rollback(); } catch { }
        throw;
    }
});

/// <summary>
/// For every non-stackable base item with no cert twin, insert a stackable
/// cert version. Idempotent — safe to run on every startup. Cert rows inherit
/// the base item's type, type_slug, and can_trade.
/// </summary>
private static void EnsureCertItems(SqliteConnection conn)
{
    var toCreate = new List<(long Id, string Slug, string Display, string Type, string TypeSlug, bool CanTrade)>();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = @"
            SELECT i.id, i.slug_id, i.display_name, i.type, i.type_slug, i.can_trade
            FROM items i
            WHERE i.stackable = 0
              AND i.cert_of_slug = ''
              AND i.non_certable = 0
              AND NOT EXISTS (
                SELECT 1 FROM items c WHERE c.cert_of_slug = i.slug_id
              );";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            toCreate.Add((r.GetInt64(0), r.GetString(1), r.GetString(2),
                          r.GetString(3), r.GetString(4), r.GetInt64(5) != 0));
    }
    foreach (var item in toCreate)
    {
        using var ins = conn.CreateCommand();
        ins.CommandText = @"
            INSERT INTO items
                (slug_id, display_name, type, type_slug, stackable, max_stack, can_trade, cert_of_slug)
            VALUES
                ($slug, $disp, $type, $tslug, 1, 2147483647, $trade, $cert_of);";
        ins.Parameters.AddWithValue("$slug",    item.Slug + "_cert");
        ins.Parameters.AddWithValue("$disp",    item.Display);
        ins.Parameters.AddWithValue("$type",    item.Type);
        ins.Parameters.AddWithValue("$tslug",   item.TypeSlug);
        ins.Parameters.AddWithValue("$trade",   item.CanTrade ? 1 : 0);
        ins.Parameters.AddWithValue("$cert_of", item.Slug);
        try { ins.ExecuteNonQuery(); }
        catch (SqliteException) { /* already exists, ignore */ }
    }
    if (toCreate.Count > 0)
        GD.Print($"[Server] Auto-generated {toCreate.Count} cert item(s)");
}

private static void MigratePlayerBankToJson(SqliteConnection conn)
{
    if (TableExists(conn, "player_bank"))
    {
        Exec(conn, "DROP TABLE player_bank;");
        GD.Print("[Server] Dropped table: player_bank (superseded by players.bank_data)");
    }
    if (ColumnExists(conn, "players", "bank_id"))
    {
        try
        {
            Exec(conn, "ALTER TABLE players DROP COLUMN bank_id;");
            GD.Print("[Server] Dropped column: players.bank_id");
        }
        catch (SqliteException) { /* older SQLite, leave the dead column */ }
    }
}

private static bool ColumnExists(SqliteConnection conn, string table, string column)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"PRAGMA table_info({table});";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        if (reader.GetString(1) == column) return true;
    return false;
}

public Task<(string Json, int Capacity)?> GetBankAsync(long playerId) => Task.Run(() =>
{
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT bank_data, bank_capacity FROM players WHERE id = $id;";
    cmd.Parameters.AddWithValue("$id", playerId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return ((string, int)?)null;
    string json = r.IsDBNull(0) ? "[]" : r.GetString(0);
    int capacity = r.GetInt32(1);
    return (json, capacity);
});

public Task SaveBankAsync(long playerId, string bankJson) => Task.Run(() =>
{
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE players SET bank_data = $j WHERE id = $id;";
    cmd.Parameters.AddWithValue("$j", bankJson);
    cmd.Parameters.AddWithValue("$id", playerId);
    cmd.ExecuteNonQuery();
});

public Task SetBankCapacityAsync(long playerId, int capacity) => Task.Run(() =>
{
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE players SET bank_capacity = $c WHERE id = $id;";
    cmd.Parameters.AddWithValue("$c", capacity);
    cmd.Parameters.AddWithValue("$id", playerId);
    cmd.ExecuteNonQuery();
});
}