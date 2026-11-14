using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public sealed class BankRepository
{
    private readonly System.Func<SqliteConnection> _open;
    public BankRepository(System.Func<SqliteConnection> open) { _open = open; }

    public Task<(string Json, int Capacity)?> GetAsync(long playerId) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT bank_data, bank_capacity FROM players WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", playerId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return ((string, int)?)null;
        string json = r.IsDBNull(0) ? "[]" : r.GetString(0);
        int capacity = r.GetInt32(1);
        return (json, capacity);
    });

    public Task SaveAsync(long playerId, string bankJson) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE players SET bank_data = $j WHERE id = $id;";
        cmd.Parameters.AddWithValue("$j", bankJson);
        cmd.Parameters.AddWithValue("$id", playerId);
        cmd.ExecuteNonQuery();
    });

    public Task SetCapacityAsync(long playerId, int capacity) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE players SET bank_capacity = $c WHERE id = $id;";
        cmd.Parameters.AddWithValue("$c", capacity);
        cmd.Parameters.AddWithValue("$id", playerId);
        cmd.ExecuteNonQuery();
    });
}