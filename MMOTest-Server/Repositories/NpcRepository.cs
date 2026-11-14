using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public sealed class NpcRepository
{
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
                cmd.Parameters.AddWithValue("$dialogue_script",
                    u.TryGetValue("dialogue_script", out var ds) ? (string)ds : "");
                cmd.Parameters.AddWithValue("$is_banker",
                    u.TryGetValue("is_banker", out var ib) && (bool)ib ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", npcId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE levels_combat SET
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
                    WHERE id = (SELECT levels_combat_id FROM npcs WHERE id = $id);";
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
}