using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public record PlayerRecord(long Id, string Username, string PasswordHash, bool IsAdmin);

public class Database
{
    private const int SqliteConstraintError = 19;

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
    /// Runs with foreign_keys=OFF so forward references (players ↔ skill_levels,
    /// combat_levels self-reference) don't fail at CREATE time. Re-enabled after.
    /// </summary>
    public void Initialize()
    {
        using var conn = Open();

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
                inventory_id      INTEGER REFERENCES player_inventory(id) ON DELETE SET NULL,
                bank_id           INTEGER REFERENCES player_bank(id)      ON DELETE SET NULL,
                sid               INTEGER NOT NULL DEFAULT 0
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
                created_at        TEXT    NOT NULL DEFAULT (datetime('now')),
                aggressive        INTEGER NOT NULL DEFAULT 0 CHECK (aggressive IN (0, 1)),
                aggro_range       INTEGER NOT NULL DEFAULT 8 CHECK (aggro_range >= 1)
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
                max_stack       INTEGER NOT NULL DEFAULT 1 CHECK (max_stack >= 1)
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

        // Auto-add columns for older DBs that predate newer schema fields.
        EnsureColumn(conn, "players", "is_admin",         "INTEGER NOT NULL DEFAULT 0 CHECK (is_admin IN (0, 1))");
        EnsureColumn(conn, "players", "login_count",      "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "players", "last_login",       "TEXT");
        EnsureColumn(conn, "players", "tile_x",           "INTEGER");
        EnsureColumn(conn, "players", "tile_y",           "INTEGER");
        EnsureColumn(conn, "players", "tile_z",           "INTEGER");
        EnsureColumn(conn, "players", "combat_levels_id", "INTEGER");
        EnsureColumn(conn, "players", "skill_levels_id",  "INTEGER");
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

        Exec(conn, "PRAGMA foreign_keys = ON;");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }


    /// <summary>
    /// Ensures the player has combat_levels and skill_levels rows and that players
    /// links to them. Idempotent: safe to call on any player, any number of times.
    /// Missing rows are created with base stats at 1/1 (hitpoints 10/10), all
    /// bonuses/defenses at 0, status flags false.
    ///
    /// Handles the case where players.combat_levels_id points at a row that
    /// doesn't actually exist (orphan) by re-creating and relinking. Reuses any
    /// existing `player_<id>` combat row rather than duplicating.
    /// Returns true if the player has both links after the call, false on failure.
    /// </summary>
    public Task<bool> EnsurePlayerStatsAsync(long playerId) => Task.Run(() =>
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        try
        {
            long? currentCombatId = null;
            long? currentSkillId = null;
            using (var check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT combat_levels_id, skill_levels_id FROM players WHERE id = $pid;";
                check.Parameters.AddWithValue("$pid", playerId);
                using var reader = check.ExecuteReader();
                if (!reader.Read()) { tx.Rollback(); return false; }
                currentCombatId = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
                currentSkillId  = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
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

            // Link both back onto players
            using (var link = conn.CreateCommand())
            {
                link.Transaction = tx;
                link.CommandText = "UPDATE players SET combat_levels_id = $cid, skill_levels_id = $sid WHERE id = $pid;";
                link.Parameters.AddWithValue("$cid", finalCombatId);
                link.Parameters.AddWithValue("$sid", finalSkillId);
                link.Parameters.AddWithValue("$pid", playerId);
                link.ExecuteNonQuery();
            }

            // Verify both links are populated
            using (var verify = conn.CreateCommand())
            {
                verify.Transaction = tx;
                verify.CommandText = "SELECT combat_levels_id, skill_levels_id FROM players WHERE id = $pid;";
                verify.Parameters.AddWithValue("$pid", playerId);
                using var reader = verify.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
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

    public Task<bool> EnsurePlayerInventoryAndBankAsync(long playerId) => Task.Run(() =>
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        try
        {
            long? invId = null, bankId = null;
            using (var check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT inventory_id, bank_id FROM players WHERE id = $pid;";
                check.Parameters.AddWithValue("$pid", playerId);
                using var r = check.ExecuteReader();
                if (!r.Read()) { tx.Rollback(); return false; }
                invId = r.IsDBNull(0) ? null : r.GetInt64(0);
                bankId = r.IsDBNull(1) ? null : r.GetInt64(1);
            }

            long finalInv = invId.HasValue && RowExists(conn, tx, "player_inventory", invId.Value)
                ? invId.Value
                : InsertEmptyRow(conn, tx, "player_inventory");
            long finalBank = bankId.HasValue && RowExists(conn, tx, "player_bank", bankId.Value)
                ? bankId.Value
                : InsertEmptyRow(conn, tx, "player_bank");

            using (var link = conn.CreateCommand())
            {
                link.Transaction = tx;
                link.CommandText = "UPDATE players SET inventory_id = $i, bank_id = $b WHERE id = $pid;";
                link.Parameters.AddWithValue("$i", finalInv);
                link.Parameters.AddWithValue("$b", finalBank);
                link.Parameters.AddWithValue("$pid", playerId);
                link.ExecuteNonQuery();
            }

            using (var verify = conn.CreateCommand())
            {
                verify.Transaction = tx;
                verify.CommandText = "SELECT inventory_id, bank_id FROM players WHERE id = $pid;";
                verify.Parameters.AddWithValue("$pid", playerId);
                using var r = verify.ExecuteReader();
                if (!r.Read() || r.IsDBNull(0) || r.IsDBNull(1))
                { tx.Rollback(); return false; }
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

    public Task<(bool Created, long Id)> TryCreatePlayerAsync(string username, string passwordHash, bool isAdmin = false) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = isAdmin
            ? "INSERT INTO players (username, password_hash, is_admin) VALUES ($u, $h, 1) RETURNING id;"
            : "INSERT INTO players (username, password_hash) VALUES ($u, $h) RETURNING id;";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", passwordHash);
        try { return (true, (long)cmd.ExecuteScalar()); }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraintError) { return (false, 0L); }
    });

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
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO players (username, password_hash, is_admin) VALUES ($u, $h, 1);";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", passwordHash);
        try { cmd.ExecuteNonQuery(); return true; }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraintError) { return false; }
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

    public Task<List<ActiveNpc>> GetAllNpcsAsync() => Task.Run(GetAllNpcsSync);

public List<ActiveNpc> GetAllNpcsSync()
{
    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT n.id, n.name, n.display, n.tile_x, n.tile_y, n.tile_z,
               n.playing_animation, n.is_dead, n.attackable,
               n.combat_levels_id, COALESCE(c.final_level, 1),
               n.render_slug, n.ai_package_state, n.spawn_timer,
               COALESCE(c.hitpoints, 3), COALESCE(c.max_hitpoints, 3),
               COALESCE(c.accuracy, 1), COALESCE(c.defence, 1),
               COALESCE(c.melee_bonus, 0), COALESCE(c.melee_defense, 0),
               n.loot_table_name, n.ai_action_state,
               n.aggressive, n.aggro_range
        FROM npcs n
        LEFT JOIN combat_levels c ON n.combat_levels_id = c.id;";
    using var reader = cmd.ExecuteReader();
    var list = new List<ActiveNpc>();
    while (reader.Read())
    {
        var npc = new ActiveNpc
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Display = reader.GetString(2),
            TileX = (int)reader.GetInt64(3),
            TileY = (int)reader.GetInt64(4),
            TileZ = (int)reader.GetInt64(5),
            PlayingAnimation = reader.GetString(6),
            IsDead = reader.GetInt64(7) != 0,
            Attackable = reader.GetInt64(8) != 0,
            CombatLevelsId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            FinalLevel = reader.GetInt32(10),
            RenderSlug = reader.IsDBNull(11) ? "" : reader.GetString(11),
            AiPackageState = reader.IsDBNull(12) ? "idle" : reader.GetString(12),
            SpawnTimerSeconds = (int)reader.GetInt64(13),
            Hitpoints = reader.GetInt32(14),
            MaxHitpoints = reader.GetInt32(15),
            Accuracy = reader.GetInt32(16),
            Defence = reader.GetInt32(17),
            MeleeBonus = reader.GetInt32(18),
            MeleeDefense = reader.GetInt32(19),
            LootTableName = reader.IsDBNull(20) ? "common_loot_1" : reader.GetString(20),
            AiActionState = reader.IsDBNull(21) ? "none" : reader.GetString(21),
            Aggressive = reader.GetInt64(22) != 0,
            AggroRangeTiles = reader.GetInt32(23),
            Strength = reader.GetInt32(15),
            AttackSpeedTicks = 10,
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
                        ai_package_state = $pkg,
                        ai_action_state  = $act,
                        loot_table_name  = $loot,
                        spawn_timer      = $stimer,
                        display          = $display,
                        aggressive       = $aggr,
                        aggro_range      = $arange
                    WHERE id = $id;";
                cmd.Parameters.AddWithValue("$atk", (bool)u["attackable"] ? 1 : 0);
                cmd.Parameters.AddWithValue("$pkg", (string)u["ai_package_state"]);
                cmd.Parameters.AddWithValue("$act", (string)u["ai_action_state"]);
                cmd.Parameters.AddWithValue("$loot", (string)u["loot_table_name"]);
                cmd.Parameters.AddWithValue("$stimer", Convert.ToInt32(u["spawn_timer"]));
                cmd.Parameters.AddWithValue("$display", (string)u["display"]);
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
                        melee_bonus   = $mb,  melee_defense = $md
                    WHERE id = (SELECT combat_levels_id FROM npcs WHERE id = $id);";
                cmd.Parameters.AddWithValue("$hp",  Convert.ToInt32(u["hitpoints"]));
                cmd.Parameters.AddWithValue("$mhp", Convert.ToInt32(u["max_hitpoints"]));
                cmd.Parameters.AddWithValue("$acc", Convert.ToInt32(u["accuracy"]));
                cmd.Parameters.AddWithValue("$str", Convert.ToInt32(u["strength"]));
                cmd.Parameters.AddWithValue("$def", Convert.ToInt32(u["defence"]));
                cmd.Parameters.AddWithValue("$mb",  Convert.ToInt32(u["melee_bonus"]));
                cmd.Parameters.AddWithValue("$md",  Convert.ToInt32(u["melee_defense"]));
                cmd.Parameters.AddWithValue("$id", npcId);
                cmd.Parameters.AddWithValue("$aggr",   (bool)u["aggressive"] ? 1 : 0);
                cmd.Parameters.AddWithValue("$arange", Convert.ToInt32(u["aggro_range"]));
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
}