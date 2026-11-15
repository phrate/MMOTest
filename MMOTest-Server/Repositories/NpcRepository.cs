using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public sealed class NpcRepository
{
    private readonly System.Func<SqliteConnection> _open;
    public NpcRepository(System.Func<SqliteConnection> open) { _open = open; }

    // ============================================================
    // Types (definitions)
    // ============================================================

    public Task<List<NpcTypeRecord>> GetAllTypesAsync() => Task.Run(GetAllTypesSync);

    public List<NpcTypeRecord> GetAllTypesSync()
    {
        var result = new List<NpcTypeRecord>();
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT t.id, t.slug_id, t.display, t.render_slug, t.dialogue_script,
                   t.is_banker, t.attackable, t.aggressive, t.aggro_range,
                   t.ai_package_state, t.spawn_timer, t.spawn_flags,
                   t.loot_table_name, t.credits_table_name,
                   COALESCE(c.final_level, 1),
                   COALESCE(c.max_hitpoints, 10),
                   COALESCE(c.max_accuracy, 1),
                   COALESCE(c.max_strength, 1),
                   COALESCE(c.max_defence, 1),
                   COALESCE(c.max_ether, 1),
                   COALESCE(c.melee_bonus, 0),
                   COALESCE(c.ranged_bonus, 0),
                   COALESCE(c.ether_bonus, 0),
                   COALESCE(c.melee_defense, 0),
                   COALESCE(c.ranged_defense, 0),
                   COALESCE(c.ether_defense, 0),
                   COALESCE(c.attack_speed, 10)
            FROM npc_types t
            LEFT JOIN levels_combat c ON t.levels_combat_id = c.id
            ORDER BY t.slug_id COLLATE NOCASE;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new NpcTypeRecord
            {
                Id = r.GetInt64(0),
                SlugId = r.GetString(1),
                Display = r.GetString(2),
                RenderSlug = r.GetString(3),
                DialogueScript = r.GetString(4),
                IsBanker = r.GetInt64(5) != 0,
                Attackable = r.GetInt64(6) != 0,
                Aggressive = r.GetInt64(7) != 0,
                AggroRangeTiles = r.GetInt32(8),
                AiPackageState = r.GetString(9),
                SpawnTimerSeconds = r.GetInt32(10),
                SpawnFlags = r.GetString(11),
                LootTableName = r.GetString(12),
                CreditsTableName = r.GetString(13),
                FinalLevel = r.GetInt32(14),
                MaxHitpoints = r.GetInt32(15),
                MaxAccuracy = r.GetInt32(16),
                MaxStrength = r.GetInt32(17),
                MaxDefence = r.GetInt32(18),
                MaxEther = r.GetInt32(19),
                MeleeBonus = r.GetInt32(20),
                RangedBonus = r.GetInt32(21),
                EtherBonus = r.GetInt32(22),
                MeleeDefense = r.GetInt32(23),
                RangedDefense = r.GetInt32(24),
                EtherDefense = r.GetInt32(25),
                AttackSpeedTicks = r.GetInt32(26),
            });
        }
        return result;
    }

    public NpcTypeRecord? GetTypeByIdSync(long typeId)
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT t.id, t.slug_id, t.display, t.render_slug, t.dialogue_script,
                t.is_banker, t.attackable, t.aggressive, t.aggro_range,
                t.ai_package_state, t.spawn_timer, t.spawn_flags,
                t.loot_table_name, t.credits_table_name,
                COALESCE(c.final_level, 1),
                COALESCE(c.max_hitpoints, 10),
                COALESCE(c.max_accuracy, 1),
                COALESCE(c.max_strength, 1),
                COALESCE(c.max_defence, 1),
                COALESCE(c.max_ether, 1),
                COALESCE(c.melee_bonus, 0),
                COALESCE(c.ranged_bonus, 0),
                COALESCE(c.ether_bonus, 0),
                COALESCE(c.melee_defense, 0),
                COALESCE(c.ranged_defense, 0),
                COALESCE(c.ether_defense, 0),
                COALESCE(c.attack_speed, 10)
            FROM npc_types t
            LEFT JOIN levels_combat c ON t.levels_combat_id = c.id
            WHERE t.id = $id;";
        cmd.Parameters.AddWithValue("$id", typeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new NpcTypeRecord
        {
            Id = r.GetInt64(0),
            SlugId = r.GetString(1),
            Display = r.GetString(2),
            RenderSlug = r.GetString(3),
            DialogueScript = r.GetString(4),
            IsBanker = r.GetInt64(5) != 0,
            Attackable = r.GetInt64(6) != 0,
            Aggressive = r.GetInt64(7) != 0,
            AggroRangeTiles = r.GetInt32(8),
            AiPackageState = r.GetString(9),
            SpawnTimerSeconds = r.GetInt32(10),
            SpawnFlags = r.GetString(11),
            LootTableName = r.GetString(12),
            CreditsTableName = r.GetString(13),
            FinalLevel = r.GetInt32(14),
            MaxHitpoints = r.GetInt32(15),
            MaxAccuracy = r.GetInt32(16),
            MaxStrength = r.GetInt32(17),
            MaxDefence = r.GetInt32(18),
            MaxEther = r.GetInt32(19),
            MeleeBonus = r.GetInt32(20),
            RangedBonus = r.GetInt32(21),
            EtherBonus = r.GetInt32(22),
            MeleeDefense = r.GetInt32(23),
            RangedDefense = r.GetInt32(24),
            EtherDefense = r.GetInt32(25),
            AttackSpeedTicks = r.GetInt32(26),
        };
    }

    public bool SlugExistsSync(string slug)
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM npc_types WHERE slug_id = $s;";
        cmd.Parameters.AddWithValue("$s", slug);
        return cmd.ExecuteScalar() != null;
    }

    public Task<long> CreateTypeAsync(Dictionary<string, object> u) => Task.Run(() =>
    {
        using var conn = _open();
        using var tx = conn.BeginTransaction();
        try
        {
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
                    VALUES ($slug, $fl, $hp, $hp, $acc, $acc, $str, $str, $def, $def,
                            $eth, $eth, $mb, $rb, $eb, $md, $rd, $ed, $aspd)
                    RETURNING id;";
                cmd.Parameters.AddWithValue("$slug", "npc_" + GetString(u, "slug_id", "unknown"));
                cmd.Parameters.AddWithValue("$fl",   GetInt(u, "final_level", 1));
                cmd.Parameters.AddWithValue("$hp",   GetInt(u, "max_hitpoints", 10));
                cmd.Parameters.AddWithValue("$acc",  GetInt(u, "max_accuracy", 1));
                cmd.Parameters.AddWithValue("$str",  GetInt(u, "max_strength", 1));
                cmd.Parameters.AddWithValue("$def",  GetInt(u, "max_defence", 1));
                cmd.Parameters.AddWithValue("$eth",  GetInt(u, "max_ether", 1));
                cmd.Parameters.AddWithValue("$mb",   GetInt(u, "melee_bonus", 0));
                cmd.Parameters.AddWithValue("$rb",   GetInt(u, "ranged_bonus", 0));
                cmd.Parameters.AddWithValue("$eb",   GetInt(u, "ether_bonus", 0));
                cmd.Parameters.AddWithValue("$md",   GetInt(u, "melee_defense", 0));
                cmd.Parameters.AddWithValue("$rd",   GetInt(u, "ranged_defense", 0));
                cmd.Parameters.AddWithValue("$ed",   GetInt(u, "ether_defense", 0));
                cmd.Parameters.AddWithValue("$aspd", GetInt(u, "attack_speed", 10));
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
                    VALUES ($slug, $disp, $rs, $ds, $bk, $atk, $aggr, $arange,
                            $ai, $stimer, $sf, $lt, $ct, $cid)
                    RETURNING id;";
                cmd.Parameters.AddWithValue("$slug", GetString(u, "slug_id", ""));
                cmd.Parameters.AddWithValue("$disp", GetString(u, "display", ""));
                cmd.Parameters.AddWithValue("$rs",   GetString(u, "render_slug", ""));
                cmd.Parameters.AddWithValue("$ds",   GetString(u, "dialogue_script", ""));
                cmd.Parameters.AddWithValue("$bk",   GetBool(u, "is_banker", false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$atk",  GetBool(u, "attackable", true) ? 1 : 0);
                cmd.Parameters.AddWithValue("$aggr", GetBool(u, "aggressive", false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$arange", GetInt(u, "aggro_range", 8));
                cmd.Parameters.AddWithValue("$ai",   GetString(u, "ai_package_state", "idle"));
                cmd.Parameters.AddWithValue("$stimer", GetInt(u, "spawn_timer", 3));
                cmd.Parameters.AddWithValue("$sf",   GetString(u, "spawn_flags", "{}"));
                cmd.Parameters.AddWithValue("$lt",   GetString(u, "loot_table_name", "common_loot_1"));
                cmd.Parameters.AddWithValue("$ct",   GetString(u, "credits_table_name", ""));
                cmd.Parameters.AddWithValue("$cid",  combatId);
                typeId = (long)cmd.ExecuteScalar()!;
            }

            tx.Commit();
            return typeId;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    });

    public Task<bool> UpdateTypeAsync(long typeId, Dictionary<string, object> u) => Task.Run(() =>
    {
        using var conn = _open();
        using var tx = conn.BeginTransaction();
        try
        {
            long combatId;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT levels_combat_id FROM npc_types WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", typeId);
                var r = cmd.ExecuteScalar();
                if (r == null || r == DBNull.Value) { tx.Rollback(); return false; }
                combatId = (long)r;
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE npc_types SET
                        display = $disp, render_slug = $rs, dialogue_script = $ds,
                        is_banker = $bk, attackable = $atk, aggressive = $aggr,
                        aggro_range = $arange, ai_package_state = $ai,
                        spawn_timer = $stimer, spawn_flags = $sf,
                        loot_table_name = $lt, credits_table_name = $ct
                    WHERE id = $id;";
                cmd.Parameters.AddWithValue("$disp", GetString(u, "display", ""));
                cmd.Parameters.AddWithValue("$rs",   GetString(u, "render_slug", ""));
                cmd.Parameters.AddWithValue("$ds",   GetString(u, "dialogue_script", ""));
                cmd.Parameters.AddWithValue("$bk",   GetBool(u, "is_banker", false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$atk",  GetBool(u, "attackable", true) ? 1 : 0);
                cmd.Parameters.AddWithValue("$aggr", GetBool(u, "aggressive", false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$arange", GetInt(u, "aggro_range", 8));
                cmd.Parameters.AddWithValue("$ai",   GetString(u, "ai_package_state", "idle"));
                cmd.Parameters.AddWithValue("$stimer", GetInt(u, "spawn_timer", 3));
                cmd.Parameters.AddWithValue("$sf",   GetString(u, "spawn_flags", "{}"));
                cmd.Parameters.AddWithValue("$lt",   GetString(u, "loot_table_name", "common_loot_1"));
                cmd.Parameters.AddWithValue("$ct",   GetString(u, "credits_table_name", ""));
                cmd.Parameters.AddWithValue("$id",   typeId);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE levels_combat SET
                        final_level = $fl,
                        hitpoints = $hp, max_hitpoints = $hp,
                        accuracy = $acc, max_accuracy = $acc,
                        strength = $str, max_strength = $str,
                        defence = $def, max_defence = $def,
                        ether = $eth, max_ether = $eth,
                        melee_bonus = $mb, ranged_bonus = $rb, ether_bonus = $eb,
                        melee_defense = $md, ranged_defense = $rd, ether_defense = $ed,
                        attack_speed = $aspd
                    WHERE id = $id;";
                cmd.Parameters.AddWithValue("$fl",   GetInt(u, "final_level", 1));
                cmd.Parameters.AddWithValue("$hp",   GetInt(u, "max_hitpoints", 10));
                cmd.Parameters.AddWithValue("$acc",  GetInt(u, "max_accuracy", 1));
                cmd.Parameters.AddWithValue("$str",  GetInt(u, "max_strength", 1));
                cmd.Parameters.AddWithValue("$def",  GetInt(u, "max_defence", 1));
                cmd.Parameters.AddWithValue("$eth",  GetInt(u, "max_ether", 1));
                cmd.Parameters.AddWithValue("$mb",   GetInt(u, "melee_bonus", 0));
                cmd.Parameters.AddWithValue("$rb",   GetInt(u, "ranged_bonus", 0));
                cmd.Parameters.AddWithValue("$eb",   GetInt(u, "ether_bonus", 0));
                cmd.Parameters.AddWithValue("$md",   GetInt(u, "melee_defense", 0));
                cmd.Parameters.AddWithValue("$rd",   GetInt(u, "ranged_defense", 0));
                cmd.Parameters.AddWithValue("$ed",   GetInt(u, "ether_defense", 0));
                cmd.Parameters.AddWithValue("$aspd", GetInt(u, "attack_speed", 10));
                cmd.Parameters.AddWithValue("$id",   combatId);
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

    public Task<bool> DeleteTypeAsync(long typeId) => Task.Run(() =>
    {
        using var conn = _open();
        using var tx = conn.BeginTransaction();
        try
        {
            long combatId;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT levels_combat_id FROM npc_types WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", typeId);
                var r = cmd.ExecuteScalar();
                if (r == null || r == DBNull.Value) { tx.Rollback(); return false; }
                combatId = (long)r;
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM npc_types WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", typeId);
                if (cmd.ExecuteNonQuery() == 0) { tx.Rollback(); return false; }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM levels_combat WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", combatId);
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

    private static int GetInt(Dictionary<string, object> u, string key, int fallback)
    {
        if (!u.TryGetValue(key, out var v) || v == null) return fallback;
        try { return System.Convert.ToInt32(v); } catch { return fallback; }
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

    // ============================================================
    // Instances (placements)
    // ============================================================

    public Task<List<(long Id, long TypeId, string? NameOverride, int X, int Y, int Z,
                       string Anim, bool IsDead, bool IsDying, long DiedAtMs)>>
        GetAllInstancesAsync() => Task.Run(GetAllInstancesSync);

    public List<(long Id, long TypeId, string? NameOverride, int X, int Y, int Z,
                 string Anim, bool IsDead, bool IsDying, long DiedAtMs)>
        GetAllInstancesSync()
    {
        var result = new List<(long, long, string?, int, int, int, string, bool, bool, long)>();
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, npc_type_id, name_override, tile_x, tile_y, tile_z,
                   playing_animation, is_dead, is_dying, died_at
            FROM npc_instances;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long diedAtMs = 0;
            if (!r.IsDBNull(9))
            {
                // died_at stored as ISO-8601 UTC text; convert to Unix ms for
                // comparison against Time.GetTicksMsec-style elapsed logic.
                try { diedAtMs = (long)(DateTimeOffset.Parse(r.GetString(9)).ToUnixTimeMilliseconds()); }
                catch { diedAtMs = 0; }
            }
            result.Add((
                r.GetInt64(0),
                r.GetInt64(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                (int)r.GetInt64(3),
                (int)r.GetInt64(4),
                (int)r.GetInt64(5),
                r.GetString(6),
                r.GetInt64(7) != 0,
                r.GetInt64(8) != 0,
                diedAtMs));
        }
        return result;
    }

    public int CountInstancesByTypeSync(long typeId)
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM npc_instances WHERE npc_type_id = $tid;";
        cmd.Parameters.AddWithValue("$tid", typeId);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    // ============================================================
    // Runtime state updates
    // ============================================================

    public int RespawnAllDeadSync()
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE npc_instances SET is_dead = 0, is_dying = 0, died_at = NULL
            WHERE is_dead = 1 OR is_dying = 1;";
        return cmd.ExecuteNonQuery();
    }

    public Task MarkDeadAsync(long instanceId) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE npc_instances
            SET is_dead = 1, is_dying = 0, died_at = datetime('now')
            WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", instanceId);
        cmd.ExecuteNonQuery();
    });

    public Task ReviveAsync(long instanceId) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE npc_instances
            SET is_dead = 0, is_dying = 0, died_at = NULL
            WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", instanceId);
        cmd.ExecuteNonQuery();
    });
}