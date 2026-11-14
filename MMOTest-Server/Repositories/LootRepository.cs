using System.Collections.Generic;
using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public sealed class LootRepository
{
    private readonly System.Func<SqliteConnection> _open;
    public LootRepository(System.Func<SqliteConnection> open) { _open = open; }

    public Dictionary<string, Dictionary<string, (int Min, int Max)>> GetLootTablesSync()
    {
        var result = new Dictionary<string, Dictionary<string, (int Min, int Max)>>();
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT slug_id, item_bag FROM loot_lists;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string slug = r.GetString(0);
            string json = r.GetString(1);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var bag = new Dictionary<string, (int Min, int Max)>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                        && prop.Value.TryGetInt32(out int fixedQty)
                        && fixedQty > 0)
                    {
                        bag[prop.Name] = (fixedQty, fixedQty);
                    }
                    else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array
                        && prop.Value.GetArrayLength() == 2
                        && prop.Value[0].TryGetInt32(out int min)
                        && prop.Value[1].TryGetInt32(out int max)
                        && min >= 0 && max >= min)
                    {
                        bag[prop.Name] = (min, max);
                    }
                    else
                    {
                        GD.PrintErr($"[Database] Loot table '{slug}': invalid entry '{prop.Name}', skipping");
                    }
                }
                result[slug] = bag;
            }
            catch (System.Text.Json.JsonException e)
            {
                GD.PrintErr($"[Database] Loot table '{slug}' parse error: {e.Message}");
            }
        }
        return result;
    }

    public Dictionary<string, (int Min, int Max)> GetCreditsLootSync()
    {
        var result = new Dictionary<string, (int, int)>();
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT slug_id, min_credits, max_credits FROM loot_lists_credits;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetString(0)] = (r.GetInt32(1), r.GetInt32(2));
        return result;
    }
}