using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public record PlayerCombatData(
    int Accuracy, int Strength, int Defence,
    int MeleeBonus, int MeleeDefense,
    int CurrentHp, int MaxHp, int FinalLevel);

public record SkillLevel(string Id, int Level, int Max, long CurrentXpTenths);

public sealed class PlayerStatsRepository
{
    public static readonly string[] SkillIds =
        { "hitpoints", "accuracy", "strength", "defence", "ether",
          "woodcutting", "mining", "fishing", "medicine", "engineering" };

    private readonly System.Func<SqliteConnection> _open;
    public PlayerStatsRepository(System.Func<SqliteConnection> open) { _open = open; }

    public Task<bool> EnsureStatsAsync(long playerId) => Task.Run(() =>
    {
        using var conn = _open();
        using var tx = conn.BeginTransaction();
        try
        {
            long? currentCombatId = null, currentSkillId = null, currentXpId = null;
            using (var check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT levels_combat_id, levels_skill_id, xp_id FROM players WHERE id = $pid;";
                check.Parameters.AddWithValue("$pid", playerId);
                using var reader = check.ExecuteReader();
                if (!reader.Read()) { tx.Rollback(); return false; }
                currentCombatId = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
                currentSkillId  = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                currentXpId     = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
            }

            long finalCombatId = currentCombatId.HasValue && SqlHelpers.RowExists(conn, tx, "levels_combat", currentCombatId.Value)
                ? currentCombatId.Value : GetOrCreateCombatRow(conn, tx, playerId);
            long finalSkillId = currentSkillId.HasValue && SqlHelpers.RowExists(conn, tx, "levels_skill", currentSkillId.Value)
                ? currentSkillId.Value : GetOrCreateSkillRow(conn, tx, playerId);
            long finalXpId = GetOrCreateXpRow(conn, tx, currentXpId, finalCombatId, finalSkillId);

            using (var link = conn.CreateCommand())
            {
                link.Transaction = tx;
                link.CommandText = "UPDATE players SET levels_combat_id = $cid, levels_skill_id = $sid, xp_id = $xid WHERE id = $pid;";
                link.Parameters.AddWithValue("$cid", finalCombatId);
                link.Parameters.AddWithValue("$sid", finalSkillId);
                link.Parameters.AddWithValue("$xid", finalXpId);
                link.Parameters.AddWithValue("$pid", playerId);
                link.ExecuteNonQuery();
            }

            using (var verify = conn.CreateCommand())
            {
                verify.Transaction = tx;
                verify.CommandText = "SELECT levels_combat_id, levels_skill_id, xp_id FROM players WHERE id = $pid;";
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

    public Task<bool> EnsureInventoryAsync(long playerId) => Task.Run(() =>
    {
        using var conn = _open();
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
            long finalInv = invId.HasValue && SqlHelpers.RowExists(conn, tx, "player_inventory", invId.Value)
                ? invId.Value : SqlHelpers.InsertEmptyRow(conn, tx, "player_inventory");
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

    private static long GetOrCreateCombatRow(SqliteConnection conn, SqliteTransaction tx, long playerId)
    {
        string slug = $"player_{playerId}";
        using (var existing = conn.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT id FROM levels_combat WHERE slug_id = $slug;";
            existing.Parameters.AddWithValue("$slug", slug);
            var r = existing.ExecuteScalar();
            if (r != null) return (long)r;
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO levels_combat
                (slug_id, template_slug,
                hitpoints, max_hitpoints, accuracy, max_accuracy, strength, max_strength,
                defence, max_defence, ether, max_ether,
                melee_bonus, ranged_bonus, ether_bonus,
                melee_defense, ranged_defense, ether_defense,
                poisoned, burning, frozen)
            VALUES ($slug, NULL, 10, 10, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0)
            RETURNING id;";
        cmd.Parameters.AddWithValue("$slug", slug);
        return (long)cmd.ExecuteScalar()!;
    }

    private static long GetOrCreateSkillRow(SqliteConnection conn, SqliteTransaction tx, long playerId)
    {
        using (var existing = conn.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT id FROM levels_skill WHERE player_id = $pid;";
            existing.Parameters.AddWithValue("$pid", playerId);
            var r = existing.ExecuteScalar();
            if (r != null) return (long)r;
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO levels_skill
                (player_id, woodcutting, max_woodcutting, mining, max_mining, fishing, max_fishing,
                 medicine, max_medicine, engineering, max_engineering)
            VALUES ($pid, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1)
            RETURNING id;";
        cmd.Parameters.AddWithValue("$pid", playerId);
        return (long)cmd.ExecuteScalar()!;
    }

    private static long GetOrCreateXpRow(
        SqliteConnection conn, SqliteTransaction tx, long? currentXpId, long combatId, long skillId)
    {
        if (currentXpId.HasValue && SqlHelpers.RowExists(conn, tx, "player_xp", currentXpId.Value))
            return currentXpId.Value;
        var levels = new int[10];
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = @"
                SELECT c.max_hitpoints, c.max_accuracy, c.max_strength, c.max_defence,
                       c.max_ether, s.max_woodcutting, s.max_mining, s.max_fishing,
                       s.max_medicine, s.max_engineering
                FROM levels_combat c CROSS JOIN levels_skill s
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
                (hitpoints, accuracy, strength, defence, ether, woodcutting, mining, fishing, medicine, engineering)
            VALUES ($hp, $accuracy, $strength, $defence, $ether, $woodcutting, $mining, $fishing, $medicine, $engineering)
            RETURNING id;";
        insert.Parameters.AddWithValue("$hp", xp[0]);
        insert.Parameters.AddWithValue("$accuracy", xp[1]);
        insert.Parameters.AddWithValue("$strength", xp[2]);
        insert.Parameters.AddWithValue("$defence", xp[3]);
        insert.Parameters.AddWithValue("$ether", xp[4]);
        insert.Parameters.AddWithValue("$woodcutting", xp[5]);
        insert.Parameters.AddWithValue("$mining", xp[6]);
        insert.Parameters.AddWithValue("$fishing", xp[7]);
        insert.Parameters.AddWithValue("$medicine", xp[8]);
        insert.Parameters.AddWithValue("$engineering", xp[9]);
        return (long)insert.ExecuteScalar()!;
    }

    public Task<PlayerCombatData?> GetCombatAsync(long playerId) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.accuracy, c.strength, c.defence, c.melee_bonus, c.melee_defense,
                   c.hitpoints, c.max_hitpoints, c.final_level
            FROM players p JOIN levels_combat c ON p.levels_combat_id = c.id
            WHERE p.id = $id;";
        cmd.Parameters.AddWithValue("$id", playerId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new PlayerCombatData(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4),
            reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7));
    });

    public Task SetNpcHpAsync(long npcId, int hp) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE levels_combat SET hitpoints = $hp WHERE id = (SELECT levels_combat_id FROM npcs WHERE id = $id);";
        cmd.Parameters.AddWithValue("$hp", hp);
        cmd.Parameters.AddWithValue("$id", npcId);
        cmd.ExecuteNonQuery();
    });

    public Task SetPlayerHpAsync(long playerId, int hp) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE levels_combat SET hitpoints = $hp WHERE id = (SELECT levels_combat_id FROM players WHERE id = $id);";
        cmd.Parameters.AddWithValue("$hp", hp);
        cmd.Parameters.AddWithValue("$id", playerId);
        cmd.ExecuteNonQuery();
    });

    public Task AwardCombatXpAsync(long playerId, int damage, int accuracyLevel, int hitpointsLevel) => Task.Run(() =>
    {
        if (damage <= 0) return;
        using var conn = _open();
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
                if (xp.ExecuteNonQuery() == 0) { tx.Rollback(); return; }
            }
            using (var levels = conn.CreateCommand())
            {
                levels.Transaction = tx;
                levels.CommandText = @"
                    UPDATE levels_combat SET
                        accuracy = MAX(accuracy, $accuracyLevel),
                        max_accuracy = MAX(max_accuracy, $accuracyLevel),
                        max_hitpoints = MAX(max_hitpoints, $hitpointsLevel)
                    WHERE id = (SELECT levels_combat_id FROM players WHERE id = $playerId);";
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

    public Task SetCombatStatsAsync(long playerId, int accuracy, int strength, int defence,
        int meleeBonus, int meleeDefense) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE levels_combat SET
                accuracy = $acc, max_accuracy = $acc,
                strength = $str, max_strength = $str,
                defence  = $def, max_defence  = $def,
                melee_bonus = $mb, melee_defense = $md
            WHERE id = (SELECT levels_combat_id FROM players WHERE id = $pid);";
        cmd.Parameters.AddWithValue("$acc", accuracy);
        cmd.Parameters.AddWithValue("$str", strength);
        cmd.Parameters.AddWithValue("$def", defence);
        cmd.Parameters.AddWithValue("$mb", meleeBonus);
        cmd.Parameters.AddWithValue("$md", meleeDefense);
        cmd.Parameters.AddWithValue("$pid", playerId);
        cmd.ExecuteNonQuery();
    });

    public Task<List<SkillLevel>> GetSkillsAsync(long playerId) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.hitpoints, c.max_hitpoints, COALESCE(x.hitpoints, 0),
                   c.accuracy, c.max_accuracy, COALESCE(x.accuracy, 0),
                   c.strength, c.max_strength, COALESCE(x.strength, 0),
                   c.defence, c.max_defence, COALESCE(x.defence, 0),
                   c.ether, c.max_ether, COALESCE(x.ether, 0),
                   s.woodcutting, s.max_woodcutting, COALESCE(x.woodcutting, 0),
                   s.mining, s.max_mining, COALESCE(x.mining, 0),
                   s.fishing, s.max_fishing, COALESCE(x.fishing, 0),
                   s.medicine, s.max_medicine, COALESCE(x.medicine, 0),
                   s.engineering, s.max_engineering, COALESCE(x.engineering, 0)
            FROM players p
            JOIN levels_combat c ON p.levels_combat_id = c.id
            JOIN levels_skill s ON p.levels_skill_id = s.id
            LEFT JOIN player_xp x ON p.xp_id = x.id
            WHERE p.id = $id;";
        cmd.Parameters.AddWithValue("$id", playerId);
        var result = new List<SkillLevel>();
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return result;
        for (int i = 0; i < SkillIds.Length; i++)
        {
            int column = i * 3;
            result.Add(new SkillLevel(SkillIds[i],
                reader.GetInt32(column), reader.GetInt32(column + 1), reader.GetInt64(column + 2)));
        }
        return result;
    });

    public Task AwardSkillXpAsync(long playerId, string skillId, long tenths, int newLevel)
    {
        string? col = skillId switch
        {
            "hitpoints" => "hitpoints", "accuracy" => "accuracy", "strength" => "strength",
            "defence" => "defence", "ether" => "ether", "woodcutting" => "woodcutting",
            "mining" => "mining", "fishing" => "fishing", "medicine" => "medicine",
            "engineering" => "engineering", _ => null
        };
        if (col == null || tenths <= 0) return Task.CompletedTask;
        bool isCombat = skillId is "hitpoints" or "accuracy" or "strength" or "defence" or "ether";
        return Task.Run(() =>
        {
            using var conn = _open();
            using var tx = conn.BeginTransaction();
            try
            {
                using (var xp = conn.CreateCommand())
                {
                    xp.Transaction = tx;
                    xp.CommandText = $"UPDATE player_xp SET {col} = {col} + $t WHERE id = (SELECT xp_id FROM players WHERE id = $pid);";
                    xp.Parameters.AddWithValue("$t", tenths);
                    xp.Parameters.AddWithValue("$pid", playerId);
                    xp.ExecuteNonQuery();
                }
                using (var lv = conn.CreateCommand())
                {
                    lv.Transaction = tx;
                    lv.CommandText = isCombat
                        ? $"UPDATE levels_combat SET {col} = MAX({col}, $level), max_{col} = MAX(max_{col}, $level) WHERE id = (SELECT levels_combat_id FROM players WHERE id = $pid);"
                        : $"UPDATE levels_skill SET {col} = MAX({col}, $level), max_{col} = MAX(max_{col}, $level) WHERE player_id = $pid;";
                    lv.Parameters.AddWithValue("$level", newLevel);
                    lv.Parameters.AddWithValue("$pid", playerId);
                    lv.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        });
    }
}