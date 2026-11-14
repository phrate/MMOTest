using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public sealed class InventoryRepository
{
    private readonly System.Func<SqliteConnection> _open;
    public InventoryRepository(System.Func<SqliteConnection> open) { _open = open; }

    public Task<long[]?> GetSlotsAsync(long playerId) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT i.slot_1, i.slot_2, i.slot_3, i.slot_4, i.slot_5, i.slot_6, i.slot_7,
                   i.slot_8, i.slot_9, i.slot_10, i.slot_11, i.slot_12, i.slot_13, i.slot_14,
                   i.slot_15, i.slot_16, i.slot_17, i.slot_18, i.slot_19, i.slot_20, i.slot_21,
                   i.slot_22, i.slot_23, i.slot_24, i.slot_25, i.slot_26, i.slot_27, i.slot_28
            FROM players p JOIN player_inventory i ON p.inventory_id = i.id
            WHERE p.id = $pid;";
        cmd.Parameters.AddWithValue("$pid", playerId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var slots = new long[28];
        for (int i = 0; i < 28; i++) slots[i] = r.GetInt64(i);
        return slots;
    });

    public Task SaveSlotAsync(long playerId, int slotIndex, long packed) => Task.Run(() =>
    {
        if (slotIndex < 0 || slotIndex >= 28) return;
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE player_inventory SET slot_{slotIndex + 1} = $v WHERE id = (SELECT inventory_id FROM players WHERE id = $pid);";
        cmd.Parameters.AddWithValue("$v", packed);
        cmd.Parameters.AddWithValue("$pid", playerId);
        cmd.ExecuteNonQuery();
    });

    public bool SaveSlotSync(long playerId, int slotIndex, long packed)
    {
        if (slotIndex < 0 || slotIndex >= 28) return false;
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE player_inventory SET slot_{slotIndex + 1} = $v WHERE id = (SELECT inventory_id FROM players WHERE id = $pid);";
        cmd.Parameters.AddWithValue("$v", packed);
        cmd.Parameters.AddWithValue("$pid", playerId);
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool CommitTradeSync(long playerAId, long[] inventoryA, int creditsA,
        long playerBId, long[] inventoryB, int creditsB)
    {
        if (playerAId == playerBId || inventoryA.Length != 28 || inventoryB.Length != 28
            || creditsA < 0 || creditsB < 0) return false;
        using var conn = _open();
        using var tx = conn.BeginTransaction();
        try
        {
            string slotAssignments = string.Join(", ", Enumerable.Range(1, 28).Select(slot => $"slot_{slot} = $slot{slot}"));
            if (!SaveInventoryTx(conn, tx, playerAId, inventoryA, slotAssignments)
                || !SaveInventoryTx(conn, tx, playerBId, inventoryB, slotAssignments)
                || !SaveCreditsTx(conn, tx, playerAId, creditsA)
                || !SaveCreditsTx(conn, tx, playerBId, creditsB))
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

    private static bool SaveInventoryTx(SqliteConnection conn, SqliteTransaction tx,
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

    private static bool SaveCreditsTx(SqliteConnection conn, SqliteTransaction tx, long playerId, int credits)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE players SET credits = $credits WHERE id = $playerId;";
        cmd.Parameters.AddWithValue("$credits", credits);
        cmd.Parameters.AddWithValue("$playerId", playerId);
        return cmd.ExecuteNonQuery() == 1;
    }
}