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
            ORDER BY t.display;";
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