using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

internal static class SchemaCreator
{
    public static void CreateAll(SqliteConnection conn)
    {
        CreateTables(conn);
        CreateIndices(conn);
        EnsureColumns(conn);
    }

    private static void CreateTables(SqliteConnection conn)
    {
        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS levels_combat (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                slug_id         TEXT    NOT NULL UNIQUE,
                template_slug   TEXT    REFERENCES levels_combat(slug_id) ON DELETE SET NULL,
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
                fishing     INTEGER NOT NULL DEFAULT 0 CHECK (fishing     >= 0),
                medicine    INTEGER NOT NULL DEFAULT 0 CHECK (medicine    >= 0),
                engineering INTEGER NOT NULL DEFAULT 0 CHECK (engineering >= 0)
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
                levels_combat_id  INTEGER REFERENCES levels_combat(id)    ON DELETE SET NULL,
                levels_skill_id   INTEGER REFERENCES levels_skill(id)     ON DELETE SET NULL,
                xp_id             INTEGER REFERENCES player_xp(id)        ON DELETE SET NULL,
                inventory_id      INTEGER REFERENCES player_inventory(id) ON DELETE SET NULL,
                sid               INTEGER NOT NULL DEFAULT 0,
                bank_data         TEXT    NOT NULL DEFAULT '[]',
                bank_capacity     INTEGER NOT NULL DEFAULT 100,
                credits           INTEGER NOT NULL DEFAULT 0
            );");

        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS levels_skill (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                player_id       INTEGER NOT NULL UNIQUE REFERENCES players(id) ON DELETE CASCADE,
                woodcutting     INTEGER NOT NULL DEFAULT 1 CHECK (woodcutting     >= 0),
                max_woodcutting INTEGER NOT NULL DEFAULT 1 CHECK (max_woodcutting >= 1),
                mining          INTEGER NOT NULL DEFAULT 1 CHECK (mining          >= 0),
                max_mining      INTEGER NOT NULL DEFAULT 1 CHECK (max_mining      >= 1),
                fishing         INTEGER NOT NULL DEFAULT 1 CHECK (fishing         >= 0),
                max_fishing     INTEGER NOT NULL DEFAULT 1 CHECK (max_fishing     >= 1),
                medicine        INTEGER NOT NULL DEFAULT 1 CHECK (medicine        >= 0),
                max_medicine    INTEGER NOT NULL DEFAULT 1 CHECK (max_medicine    >= 1),
                engineering     INTEGER NOT NULL DEFAULT 1 CHECK (engineering     >= 0),
                max_engineering INTEGER NOT NULL DEFAULT 1 CHECK (max_engineering >= 1),
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
                levels_combat_id  INTEGER REFERENCES levels_combat(id) ON DELETE SET NULL,
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
                can_trade       INTEGER NOT NULL DEFAULT 1 CHECK (can_trade IN (0, 1)),
                cert_of_slug    TEXT    NOT NULL DEFAULT '',
                non_certable    INTEGER NOT NULL DEFAULT 0 CHECK (non_certable IN (0, 1)),
                non_bankable    INTEGER NOT NULL DEFAULT 0 CHECK (non_bankable IN (0, 1))
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
        CREATE TABLE IF NOT EXISTS loot_lists (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            slug_id   TEXT    NOT NULL UNIQUE,
            item_bag  TEXT    NOT NULL DEFAULT '{}'
        );");

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS loot_lists_credits (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            slug_id      TEXT    NOT NULL UNIQUE,
            min_credits  INTEGER NOT NULL DEFAULT 0 CHECK (min_credits >= 0),
            max_credits  INTEGER NOT NULL DEFAULT 0 CHECK (max_credits >= 0)
        );");

        Exec(conn, @"
        CREATE TABLE IF NOT EXISTS recipes (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            slug_id     TEXT    NOT NULL UNIQUE,
            pair        TEXT    NOT NULL UNIQUE,
            script      TEXT    NOT NULL,
            xp_rewards  TEXT    NOT NULL DEFAULT '{}',
            created_at  TEXT    NOT NULL DEFAULT (datetime('now'))
        );");
    }

    private static void CreateIndices(SqliteConnection conn)
    {
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_levels_combat_slug     ON levels_combat (slug_id);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_levels_combat_template ON levels_combat (template_slug);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_levels_skill_player    ON levels_skill  (player_id);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_npcs_tile              ON npcs (tile_x, tile_y);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_npcs_combat            ON npcs (levels_combat_id);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_items_type             ON items (type, type_slug);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_items_slug             ON items (slug_id);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_recipes_pair           ON recipes (pair);");
    }

    private static void EnsureColumns(SqliteConnection conn)
    {
        EnsureColumn(conn, "players", "is_admin",         "INTEGER NOT NULL DEFAULT 0 CHECK (is_admin IN (0, 1))");
        EnsureColumn(conn, "players", "login_count",      "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "players", "last_login",       "TEXT");
        EnsureColumn(conn, "players", "tile_x",           "INTEGER");
        EnsureColumn(conn, "players", "tile_y",           "INTEGER");
        EnsureColumn(conn, "players", "tile_z",           "INTEGER");
        EnsureColumn(conn, "players", "levels_combat_id", "INTEGER");
        EnsureColumn(conn, "players", "levels_skill_id",  "INTEGER");
        EnsureColumn(conn, "players", "xp_id",            "INTEGER");
        EnsureColumn(conn, "players", "inventory_id",     "INTEGER");
        EnsureColumn(conn, "npcs",    "spawn_flags",      "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(conn, "npcs",    "ai_package_state", "TEXT NOT NULL DEFAULT 'idle'");
        EnsureColumn(conn, "npcs",    "ai_action_state",  "TEXT NOT NULL DEFAULT 'none'");
        EnsureColumn(conn, "npcs",    "attackable",       "INTEGER NOT NULL DEFAULT 1 CHECK (attackable IN (0, 1))");
        EnsureColumn(conn, "npcs",    "render_slug",      "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "npcs",    "levels_combat_id", "INTEGER");
        EnsureColumn(conn, "levels_combat", "final_level",  "INTEGER NOT NULL DEFAULT 3 CHECK (final_level >= 1)");
        EnsureColumn(conn, "players", "sid",              "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "npcs",    "aggressive",       "INTEGER NOT NULL DEFAULT 0 CHECK (aggressive IN (0, 1))");
        EnsureColumn(conn, "npcs",    "aggro_range",      "INTEGER NOT NULL DEFAULT 8 CHECK (aggro_range >= 1)");
        EnsureColumn(conn, "levels_combat", "attack_speed", "INTEGER NOT NULL DEFAULT 10 CHECK (attack_speed >= 1)");
        EnsureColumn(conn, "players", "credits",          "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "npcs",    "credits_table_name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "items",   "can_trade",        "INTEGER NOT NULL DEFAULT 1 CHECK (can_trade IN (0, 1))");
        EnsureColumn(conn, "items",   "cert_of_slug",     "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "players", "bank_data",        "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(conn, "players", "bank_capacity",    "INTEGER NOT NULL DEFAULT 100");
        EnsureColumn(conn, "items",   "non_certable",     "INTEGER NOT NULL DEFAULT 0 CHECK (non_certable IN (0, 1))");
        EnsureColumn(conn, "items",   "non_bankable",     "INTEGER NOT NULL DEFAULT 0 CHECK (non_bankable IN (0, 1))");
        EnsureColumn(conn, "npcs",    "is_banker",        "INTEGER NOT NULL DEFAULT 0 CHECK (is_banker IN (0, 1))");
        EnsureColumn(conn, "npcs",    "dialogue_script",  "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "levels_skill", "medicine",        "INTEGER NOT NULL DEFAULT 1 CHECK (medicine        >= 0)");
        EnsureColumn(conn, "levels_skill", "max_medicine",    "INTEGER NOT NULL DEFAULT 1 CHECK (max_medicine    >= 1)");
        EnsureColumn(conn, "levels_skill", "engineering",     "INTEGER NOT NULL DEFAULT 1 CHECK (engineering     >= 0)");
        EnsureColumn(conn, "levels_skill", "max_engineering", "INTEGER NOT NULL DEFAULT 1 CHECK (max_engineering >= 1)");
        EnsureColumn(conn, "player_xp",    "medicine",        "INTEGER NOT NULL DEFAULT 0 CHECK (medicine    >= 0)");
        EnsureColumn(conn, "player_xp",    "engineering",     "INTEGER NOT NULL DEFAULT 0 CHECK (engineering >= 0)");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
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
}