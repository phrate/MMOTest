using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

internal static class MigratePlayerXpToTenths
{
    private const string MigrationId = "players_xp_tenths_v1";

    public static void Apply(SqliteConnection conn)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM schema_migrations WHERE migration_id = $id;";
            check.Parameters.AddWithValue("$id", MigrationId);
            if (check.ExecuteScalar() != null) return;
        }

        using var tx = conn.BeginTransaction();
        try
        {
            using (var check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT 1 FROM schema_migrations WHERE migration_id = $id;";
                check.Parameters.AddWithValue("$id", MigrationId);
                if (check.ExecuteScalar() != null) { tx.Commit(); return; }
            }

            using (var scale = conn.CreateCommand())
            {
                scale.Transaction = tx;
                scale.CommandText = @"
                    UPDATE player_xp SET
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
                    FROM player_xp x
                    LEFT JOIN players p ON p.xp_id = x.id
                    LEFT JOIN levels_combat c ON c.id = p.levels_combat_id
                    LEFT JOIN levels_skill s ON s.id = p.levels_skill_id;";
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
                    UPDATE player_xp SET
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
                mark.Parameters.AddWithValue("$id", MigrationId);
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
}