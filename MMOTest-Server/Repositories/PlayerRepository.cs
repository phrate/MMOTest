using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public record PlayerRecord(long Id, string Username, string PasswordHash, bool IsAdmin);

public sealed class PlayerRepository
{
    private readonly System.Func<SqliteConnection> _open;
    public PlayerRepository(System.Func<SqliteConnection> open) { _open = open; }

    public Task<PlayerRecord?> GetByUsernameAsync(string username) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, is_admin FROM players WHERE username = $u;";
        cmd.Parameters.AddWithValue("$u", username);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new PlayerRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0);
    });

    public Task<long> CountAsync() => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM players;";
        return (long)cmd.ExecuteScalar()!;
    });

    private (bool Created, long Id) TryCreate(string username, string passwordHash, bool isAdmin)
    {
        using var conn = _open();
        using var tx = conn.BeginTransaction();
        try
        {
            long xpId;
            using (var xp = conn.CreateCommand())
            {
                xp.Transaction = tx;
                xp.CommandText = "INSERT INTO player_xp (hitpoints) VALUES ($hpXp) RETURNING id;";
                xp.Parameters.AddWithValue("$hpXp", SkillExperience.GetXpForLevelTenths(10));
                xpId = (long)xp.ExecuteScalar()!;
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = isAdmin
                ? "INSERT INTO players (username, password_hash, is_admin, xp_id) VALUES ($u, $h, 1, $xpId) RETURNING id;"
                : "INSERT INTO players (username, password_hash, xp_id) VALUES ($u, $h, $xpId) RETURNING id;";
            cmd.Parameters.AddWithValue("$u", username);
            cmd.Parameters.AddWithValue("$h", passwordHash);
            cmd.Parameters.AddWithValue("$xpId", xpId);
            long playerId = (long)cmd.ExecuteScalar()!;
            tx.Commit();
            return (true, playerId);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
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

    public Task<(bool Created, long Id)> TryCreateAsync(string username, string passwordHash, bool isAdmin = false)
        => Task.Run(() => TryCreate(username, passwordHash, isAdmin));

    public Task<bool> TryCreateDevAdminAsync(string username, string passwordHash) => Task.Run(() =>
        TryCreate(username, passwordHash, isAdmin: true).Created);

    public Task<long> RecordLoginAsync(long playerId) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE players SET login_count = login_count + 1, last_login = datetime('now')
            WHERE id = $id RETURNING login_count;";
        cmd.Parameters.AddWithValue("$id", playerId);
        return (long)cmd.ExecuteScalar()!;
    });

    public Task<(int X, int Y, int Z)?> GetTileAsync(long playerId) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tile_x, tile_y, tile_z FROM players WHERE id = $id AND tile_x IS NOT NULL;";
        cmd.Parameters.AddWithValue("$id", playerId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return ((int, int, int)?)null;
        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2));
    });

    public Task SetTileAsync(long playerId, int x, int y, int z) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE players SET tile_x = $x, tile_y = $y, tile_z = $z WHERE id = $id;";
        cmd.Parameters.AddWithValue("$x", x);
        cmd.Parameters.AddWithValue("$y", y);
        cmd.Parameters.AddWithValue("$z", z);
        cmd.Parameters.AddWithValue("$id", playerId);
        cmd.ExecuteNonQuery();
    });

    public Task<PlayerRecord?> GetFirstAdminAsync() => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, is_admin FROM players WHERE is_admin = 1 ORDER BY id LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new PlayerRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0);
    });

    public bool SetAdminSync(long playerId, bool isAdmin)
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE players SET is_admin = $a WHERE id = $id;";
        cmd.Parameters.AddWithValue("$a", isAdmin ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", playerId);
        return cmd.ExecuteNonQuery() == 1;
    }

    public long GetMaxSid()
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(sid), 0) FROM players;";
        return (long)cmd.ExecuteScalar()!;
    }

    public Task SetSidAsync(long playerId, long sid) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE players SET sid = $sid WHERE id = $id;";
        cmd.Parameters.AddWithValue("$sid", sid);
        cmd.Parameters.AddWithValue("$id", playerId);
        cmd.ExecuteNonQuery();
    });

    public Task<int> GetCreditsAsync(long playerId) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT credits FROM players WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", playerId);
        var r = cmd.ExecuteScalar();
        return r != null ? System.Convert.ToInt32(r) : 0;
    });

    public Task SetCreditsAsync(long playerId, int credits) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE players SET credits = $c WHERE id = $id;";
        cmd.Parameters.AddWithValue("$c", credits);
        cmd.Parameters.AddWithValue("$id", playerId);
        cmd.ExecuteNonQuery();
    });

    public bool SetCreditsSync(long playerId, int credits)
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE players SET credits = $c WHERE id = $id;";
        cmd.Parameters.AddWithValue("$c", credits);
        cmd.Parameters.AddWithValue("$id", playerId);
        return cmd.ExecuteNonQuery() == 1;
    }

    public int WipeAllExceptSync(string preserveUsername)
    {
        using var conn = _open();
        using var tx = conn.BeginTransaction();
        try
        {
            var invIds = new List<long>();
            var combatIds = new List<long>();
            var xpIds = new List<long>();
            using (var select = conn.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = @"
                    SELECT inventory_id, levels_combat_id, xp_id FROM players
                    WHERE username != $keep COLLATE NOCASE;";
                select.Parameters.AddWithValue("$keep", preserveUsername);
                using var r = select.ExecuteReader();
                while (r.Read())
                {
                    if (!r.IsDBNull(0)) invIds.Add(r.GetInt64(0));
                    if (!r.IsDBNull(1)) combatIds.Add(r.GetInt64(1));
                    if (!r.IsDBNull(2)) xpIds.Add(r.GetInt64(2));
                }
            }
            int deleted;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM players WHERE username != $keep COLLATE NOCASE;";
                cmd.Parameters.AddWithValue("$keep", preserveUsername);
                deleted = cmd.ExecuteNonQuery();
            }
            if (invIds.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM player_inventory WHERE id IN ({string.Join(",", invIds)});";
                cmd.ExecuteNonQuery();
            }
            if (combatIds.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM levels_combat WHERE id IN ({string.Join(",", combatIds)});";
                cmd.ExecuteNonQuery();
            }
            if (xpIds.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM player_xp WHERE id IN ({string.Join(",", xpIds)});";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return deleted;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }
}