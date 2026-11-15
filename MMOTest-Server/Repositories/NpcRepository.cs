using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public sealed class NpcRepository
{
    public record TemplateRecord(long Id, string SlugId);
    private readonly System.Func<SqliteConnection> _open;
    public NpcRepository(System.Func<SqliteConnection> open) { _open = open; }

    public Task<List<ActiveNpc>> GetAllAsync() => Task.Run(GetAllSync);

    public List<ActiveNpc> GetAllSync()
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT n.id, n.name, n.display, n.tile_x, n.tile_y, n.tile_z,
                   n.playing_animation, n.is_dead, n.attackable,
                   n.levels_combat_id, COALESCE(c.final_level, 1) AS final_level,
                   n.render_slug, n.dialogue_script, n.ai_package_state, n.spawn_timer,
                   COALESCE(c.hitpoints, 3) AS hitpoints,
                   COALESCE(c.max_hitpoints, 3) AS max_hitpoints,
                   COALESCE(c.max_accuracy, 1) AS max_accuracy,
                   COALESCE(c.max_strength, 1) AS max_strength,
                   COALESCE(c.max_defence, 1) AS max_defence,
                   COALESCE(n.spawn_flags, '{}') AS spawn_flags,
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
                   n.aggressive, n.aggro_range,
                   COALESCE(n.credits_table_name, '') AS credits_table_name,
                   COALESCE(n.is_banker, 0) AS is_banker
            FROM npcs n
            LEFT JOIN levels_combat c ON n.levels_combat_id = c.id;";
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
                CombatLevelsId = reader.IsDBNull(reader.GetOrdinal("levels_combat_id"))
                    ? null : reader.GetInt64(reader.GetOrdinal("levels_combat_id")),
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
                MaxAccuracy = reader.GetInt32(reader.GetOrdinal("max_accuracy")),
                MaxStrength = reader.GetInt32(reader.GetOrdinal("max_strength")),
                MaxDefence = reader.GetInt32(reader.GetOrdinal("max_defence")),
                SpawnFlags = reader.IsDBNull(reader.GetOrdinal("spawn_flags"))
                    ? "{}" : reader.GetString(reader.GetOrdinal("spawn_flags")),
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
                CreditsTableName = reader.IsDBNull(reader.GetOrdinal("credits_table_name"))
                    ? "" : reader.GetString(reader.GetOrdinal("credits_table_name")),
                IsBanker = reader.GetInt64(reader.GetOrdinal("is_banker")) != 0,
            };
            npc.ResetPositionToHome();
            list.Add(npc);
        }
        return list;
    }

    public List<TemplateRecord> GetAllTemplatesSync()
    {
        var result = new List<TemplateRecord>();
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, slug_id FROM levels_combat
            WHERE template_slug IS NULL AND slug_id LIKE 'npc\_%' ESCAPE '\'
            ORDER BY slug_id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new TemplateRecord(r.GetInt64(0), r.GetString(1)));
        return result;
    }

    public Dictionary<string, object>? GetTemplateSync(long templateId)
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, slug_id, final_level,
                   hitpoints, max_hitpoints, accuracy, max_accuracy,
                   strength, max_strength, defence, max_defence,
                   ether, max_ether,
                   melee_bonus, ranged_bonus, ether_bonus,
                   melee_defense, ranged_defense, ether_defense,
                   attack_speed
            FROM levels_combat WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", templateId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Dictionary<string, object>
        {
            ["id"] = r.GetInt64(0),
            ["slug_id"] = r.GetString(1),
            ["final_level"] = r.GetInt32(2),
            ["hitpoints"] = r.GetInt32(3),
            ["max_hitpoints"] = r.GetInt32(4),
            ["accuracy"] = r.GetInt32(5),
            ["max_accuracy"] = r.GetInt32(6),
            ["strength"] = r.GetInt32(7),
            ["max_strength"] = r.GetInt32(8),
            ["defence"] = r.GetInt32(9),
            ["max_defence"] = r.GetInt32(10),
            ["ether"] = r.GetInt32(11),
            ["max_ether"] = r.GetInt32(12),
            ["melee_bonus"] = r.GetInt32(13),
            ["ranged_bonus"] = r.GetInt32(14),
            ["ether_bonus"] = r.GetInt32(15),
            ["melee_defense"] = r.GetInt32(16),
            ["ranged_defense"] = r.GetInt32(17),
            ["ether_defense"] = r.GetInt32(18),
            ["attack_speed"] = r.GetInt32(19),
        };
    }

    public Dictionary<string, object>? GetCombatSnapshotSync(long npcId)
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.final_level,
                c.hitpoints, c.max_hitpoints,
                c.accuracy, c.max_accuracy,
                c.strength, c.max_strength,
                c.defence, c.max_defence,
                c.ether, c.max_ether,
                c.melee_bonus, c.ranged_bonus, c.ether_bonus,
                c.melee_defense, c.ranged_defense, c.ether_defense,
                c.attack_speed
            FROM npcs n
            LEFT JOIN levels_combat c ON n.levels_combat_id = c.id
            WHERE n.id = $id;";
        cmd.Parameters.AddWithValue("$id", npcId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        if (r.IsDBNull(0)) return null;   // no levels_combat linked
        return new Dictionary<string, object>
        {
            ["final_level"]   = r.GetInt32(0),
            ["hitpoints"]     = r.GetInt32(1),
            ["max_hitpoints"] = r.GetInt32(2),
            ["accuracy"]      = r.GetInt32(3),
            ["max_accuracy"]  = r.GetInt32(4),
            ["strength"]      = r.GetInt32(5),
            ["max_strength"]  = r.GetInt32(6),
            ["defence"]       = r.GetInt32(7),
            ["max_defence"]   = r.GetInt32(8),
            ["ether"]         = r.GetInt32(9),
            ["max_ether"]     = r.GetInt32(10),
            ["melee_bonus"]   = r.GetInt32(11),
            ["ranged_bonus"]  = r.GetInt32(12),
            ["ether_bonus"]   = r.GetInt32(13),
            ["melee_defense"] = r.GetInt32(14),
            ["ranged_defense"]= r.GetInt32(15),
            ["ether_defense"] = r.GetInt32(16),
            ["attack_speed"]  = r.GetInt32(17),
        };
    }

    public Task<bool> SaveTemplateAsync(long templateId, Dictionary<string, object> u) => Task.Run(() =>
    {
        try
        {
            using var conn = _open();

            // Fetch slug_id once, before the update (also validates the template exists)
            string? slugId;
            using (var getSlug = conn.CreateCommand())
            {
                getSlug.CommandText = "SELECT slug_id FROM levels_combat WHERE id = $id;";
                getSlug.Parameters.AddWithValue("$id", templateId);
                var s = getSlug.ExecuteScalar() as string;
                if (string.IsNullOrEmpty(s)) return false;
                slugId = s;
            }

            using var tx = conn.BeginTransaction();

            // Update the template row itself
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE levels_combat SET
                        final_level   = $fl,
                        hitpoints     = $hp, max_hitpoints = $mhp,
                        accuracy      = $acc, max_accuracy  = $mac,
                        strength      = $str, max_strength  = $mst,
                        defence       = $def, max_defence   = $mdf,
                        ether         = $eth, max_ether     = $met,
                        melee_bonus   = $mb,  ranged_bonus  = $rb,
                        ether_bonus   = $eb,
                        melee_defense = $md,  ranged_defense = $rd,
                        ether_defense = $ed,
                        attack_speed  = $aspeed
                    WHERE id = $id;";
                AddTemplateParams(cmd, u);
                cmd.Parameters.AddWithValue("$id", templateId);
                if (cmd.ExecuteNonQuery() == 0) { tx.Rollback(); return false; }
            }

            // Propagate to unique rows that reference this template
            using (var prop = conn.CreateCommand())
            {
                prop.Transaction = tx;
                prop.CommandText = @"
                    UPDATE levels_combat SET
                        final_level   = $fl,
                        hitpoints     = $hp, max_hitpoints = $mhp,
                        accuracy      = $acc, max_accuracy  = $mac,
                        strength      = $str, max_strength  = $mst,
                        defence       = $def, max_defence   = $mdf,
                        ether         = $eth, max_ether     = $met,
                        melee_bonus   = $mb,  ranged_bonus  = $rb,
                        ether_bonus   = $eb,
                        melee_defense = $md,  ranged_defense = $rd,
                        ether_defense = $ed,
                        attack_speed  = $aspeed
                    WHERE template_slug = $slug;";
                AddTemplateParams(prop, u);
                prop.Parameters.AddWithValue("$slug", slugId);
                prop.ExecuteNonQuery();   // 0 is fine — means no uniques use this template yet
            }

            tx.Commit();
            return true;
        }
        catch (System.Exception e)
        {
            Godot.GD.PrintErr($"[NpcRepository] SaveTemplateAsync({templateId}): {e}");
            return false;
        }
    });

    private static void AddTemplateParams(Microsoft.Data.Sqlite.SqliteCommand cmd, Dictionary<string, object> u)
    {
        cmd.Parameters.AddWithValue("$fl",     ToInt(u, "final_level", 3));
        cmd.Parameters.AddWithValue("$hp",     ToInt(u, "hitpoints", 10));
        cmd.Parameters.AddWithValue("$mhp",    ToInt(u, "max_hitpoints", 10));
        cmd.Parameters.AddWithValue("$acc",    ToInt(u, "accuracy", 1));
        cmd.Parameters.AddWithValue("$mac",    ToInt(u, "max_accuracy", 1));
        cmd.Parameters.AddWithValue("$str",    ToInt(u, "strength", 1));
        cmd.Parameters.AddWithValue("$mst",    ToInt(u, "max_strength", 1));
        cmd.Parameters.AddWithValue("$def",    ToInt(u, "defence", 1));
        cmd.Parameters.AddWithValue("$mdf",    ToInt(u, "max_defence", 1));
        cmd.Parameters.AddWithValue("$eth",    ToInt(u, "ether", 1));
        cmd.Parameters.AddWithValue("$met",    ToInt(u, "max_ether", 1));
        cmd.Parameters.AddWithValue("$mb",     ToInt(u, "melee_bonus", 0));
        cmd.Parameters.AddWithValue("$rb",     ToInt(u, "ranged_bonus", 0));
        cmd.Parameters.AddWithValue("$eb",     ToInt(u, "ether_bonus", 0));
        cmd.Parameters.AddWithValue("$md",     ToInt(u, "melee_defense", 0));
        cmd.Parameters.AddWithValue("$rd",     ToInt(u, "ranged_defense", 0));
        cmd.Parameters.AddWithValue("$ed",     ToInt(u, "ether_defense", 0));
        cmd.Parameters.AddWithValue("$aspeed", ToInt(u, "attack_speed", 10));
    }

    private static int ToInt(Dictionary<string, object> u, string key, int fallback)
    {
        if (!u.TryGetValue(key, out var v) || v == null) return fallback;
        try { return System.Convert.ToInt32(v); } catch { return fallback; }
    }

    public int RespawnAllDeadSync()
    {
        using var conn = _open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE levels_combat SET
                    hitpoints = max_hitpoints,
                    accuracy  = max_accuracy,
                    strength  = max_strength,
                    defence   = max_defence,
                    ether     = max_ether,
                    poisoned  = 0, burning = 0, frozen = 0
                WHERE id IN (
                    SELECT levels_combat_id FROM npcs
                    WHERE (is_dead = 1 OR is_dying = 1) AND levels_combat_id IS NOT NULL
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

    public Task MarkDeadAsync(long npcId) => Task.Run(() =>
    {
        using var conn = _open();
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
                UPDATE levels_combat SET hitpoints = 0, poisoned = 0, burning = 0, frozen = 0
                WHERE id = (SELECT levels_combat_id FROM npcs WHERE id = $id);";
            cmd.Parameters.AddWithValue("$id", npcId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    });

    public Task ReviveAsync(long npcId) => Task.Run(() =>
    {
        using var conn = _open();
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
                UPDATE levels_combat SET
                    hitpoints = max_hitpoints,
                    accuracy = max_accuracy,
                    strength = max_strength,
                    defence = max_defence,
                    ether = max_ether,
                    poisoned = 0, burning = 0, frozen = 0
                WHERE id = (SELECT levels_combat_id FROM npcs WHERE id = $id);";
            cmd.Parameters.AddWithValue("$id", npcId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    });

    public Task<bool> SaveAdminEditAsync(long npcId, Dictionary<string, object> u) => Task.Run(() =>
    {
        using var conn = _open();
        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE npcs SET
                        attackable       = $atk,
                        aggressive       = $aggr,
                        aggro_range      = $arange,
                        ai_package_state = $pkg,
                        ai_action_state  = $act,
                        loot_table_name  = $loot,
                        spawn_timer      = $stimer,
                        display          = $display,
                        dialogue_script  = $dialogue_script,
                        is_banker        = $is_banker,
                        credits_table_name = $credits_table,
                        render_slug      = $render_slug,
                        tile_x           = $tx,
                        tile_y           = $ty,
                        tile_z           = $tz,
                        spawn_flags      = $sf
                    WHERE id = $id;";
                cmd.Parameters.AddWithValue("$atk",             GetBool(u, "attackable", true) ? 1 : 0);
                cmd.Parameters.AddWithValue("$aggr",            GetBool(u, "aggressive", false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$arange",          GetInt(u, "aggro_range", 8));
                cmd.Parameters.AddWithValue("$pkg",             GetString(u, "ai_package_state", "idle"));
                cmd.Parameters.AddWithValue("$act",             GetString(u, "ai_action_state", "none"));
                cmd.Parameters.AddWithValue("$loot",            GetString(u, "loot_table_name", "common_loot_1"));
                cmd.Parameters.AddWithValue("$stimer",          GetInt(u, "spawn_timer", 3));
                cmd.Parameters.AddWithValue("$display",         GetString(u, "display", ""));
                cmd.Parameters.AddWithValue("$dialogue_script", GetString(u, "dialogue_script", ""));
                cmd.Parameters.AddWithValue("$is_banker",       GetBool(u, "is_banker", false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$credits_table", GetString(u, "credits_table_name", ""));
                cmd.Parameters.AddWithValue("$render_slug",     GetString(u, "render_slug", ""));
                cmd.Parameters.AddWithValue("$tx",              GetLong(u, "tile_x", 0));
                cmd.Parameters.AddWithValue("$ty",              GetLong(u, "tile_y", 0));
                cmd.Parameters.AddWithValue("$tz",              GetLong(u, "tile_z", 100));
                cmd.Parameters.AddWithValue("$sf",              GetString(u, "spawn_flags", "{}"));
                cmd.Parameters.AddWithValue("$id",              npcId);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE levels_combat SET
                        hitpoints     = $hp,  max_hitpoints = $mhp,
                        accuracy      = $acc, max_accuracy  = $mac,
                        strength      = $str, max_strength  = $mst,
                        defence       = $def, max_defence   = $mdf,
                        ether         = $eth, max_ether     = $meth,
                        melee_bonus   = $mb,  ranged_bonus  = $rb,
                        ether_bonus   = $eb,
                        melee_defense = $md,  ranged_defense = $rd,
                        ether_defense = $ed,
                        attack_speed  = $aspeed
                    WHERE id = (SELECT levels_combat_id FROM npcs WHERE id = $id);";
                cmd.Parameters.AddWithValue("$hp",     GetInt(u, "hitpoints", 3));
                cmd.Parameters.AddWithValue("$mhp",    GetInt(u, "max_hitpoints", 3));
                cmd.Parameters.AddWithValue("$acc",    GetInt(u, "accuracy", 1));
                cmd.Parameters.AddWithValue("$mac",    GetInt(u, "max_accuracy", GetInt(u, "accuracy", 1)));
                cmd.Parameters.AddWithValue("$str",    GetInt(u, "strength", 1));
                cmd.Parameters.AddWithValue("$mst",    GetInt(u, "max_strength", GetInt(u, "strength", 1)));
                cmd.Parameters.AddWithValue("$def",    GetInt(u, "defence", 1));
                cmd.Parameters.AddWithValue("$mdf",    GetInt(u, "max_defence", GetInt(u, "defence", 1)));
                cmd.Parameters.AddWithValue("$eth",    GetInt(u, "ether", 1));
                cmd.Parameters.AddWithValue("$meth",   GetInt(u, "max_ether", 1));
                cmd.Parameters.AddWithValue("$mb",     GetInt(u, "melee_bonus", 0));
                cmd.Parameters.AddWithValue("$rb",     GetInt(u, "ranged_bonus", 0));
                cmd.Parameters.AddWithValue("$eb",     GetInt(u, "ether_bonus", 0));
                cmd.Parameters.AddWithValue("$md",     GetInt(u, "melee_defense", 0));
                cmd.Parameters.AddWithValue("$rd",     GetInt(u, "ranged_defense", 0));
                cmd.Parameters.AddWithValue("$ed",     GetInt(u, "ether_defense", 0));
                cmd.Parameters.AddWithValue("$aspeed", GetInt(u, "attack_speed", 10));
                cmd.Parameters.AddWithValue("$id",     npcId);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return true;
        }
        catch (System.Exception e)
        {
            try { tx.Rollback(); } catch { }
            Godot.GD.PrintErr($"[NpcRepository] SaveAdminEditAsync({npcId}): {e.Message}");
            return false;
        }
    });

    private static int GetInt(Dictionary<string, object> u, string key, int fallback)
    {
        if (!u.TryGetValue(key, out var v) || v == null) return fallback;
        try { return System.Convert.ToInt32(v); } catch { return fallback; }
    }

    private static long GetLong(Dictionary<string, object> u, string key, long fallback)
    {
        if (!u.TryGetValue(key, out var v) || v == null) return fallback;
        try { return System.Convert.ToInt64(v); } catch { return fallback; }
    }

    private static bool GetBool(Dictionary<string, object> u, string key, bool fallback)
    {
        if (!u.TryGetValue(key, out var v) || v == null) return fallback;
        try { return System.Convert.ToBoolean(v); } catch { return fallback; }
    }

    private static string GetString(Dictionary<string, object> u, string key, string fallback)
    {
        if (!u.TryGetValue(key, out var v) || v == null) return fallback;
        return v.ToString() ?? fallback;
    }
}