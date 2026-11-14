using System.Collections.Generic;
using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public record RecipeRecord(
    long Id,
    string Slug,
    string Pair,
    string ScriptPath,
    Dictionary<string, int> XpRewards);

public sealed class RecipeRepository
{
    private readonly System.Func<SqliteConnection> _open;
    public RecipeRepository(System.Func<SqliteConnection> open) { _open = open; }

    public List<RecipeRecord> GetAllSync()
    {
        var result = new List<RecipeRecord>();
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, slug_id, pair, script, xp_rewards FROM recipes;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var xpDict = new Dictionary<string, int>();
            string xpJson = r.IsDBNull(4) ? "{}" : r.GetString(4);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(xpJson);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (prop.Value.TryGetInt32(out var v) && v > 0)
                        xpDict[prop.Name] = v;
            }
            catch (System.Text.Json.JsonException e)
            {
                GD.PrintErr($"[Database] Recipe '{r.GetString(1)}' xp_rewards parse error: {e.Message}");
            }
            result.Add(new RecipeRecord(
                r.GetInt64(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                xpDict));
        }
        return result;
    }
}