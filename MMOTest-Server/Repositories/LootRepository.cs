using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

	// ============================================================
	// PlayerEditor-style paged access + save (used by LootEditor)
	// ============================================================

	public record LootListRow(long Id, string Slug);

	public Task<(List<LootListRow> Items, int TotalCount)> GetPagedItemsAsync(
		string search, int page, int pageSize) => Task.Run(() =>
	{
		using var conn = _open();
		bool hasSearch = !string.IsNullOrWhiteSpace(search);
		string whereSql = hasSearch ? "WHERE slug_id LIKE $search COLLATE NOCASE" : "";

		int totalCount;
		using (var countCmd = conn.CreateCommand())
		{
			countCmd.CommandText = $"SELECT COUNT(*) FROM loot_lists {whereSql};";
			if (hasSearch) countCmd.Parameters.AddWithValue("$search", "%" + search + "%");
			totalCount = (int)(long)countCmd.ExecuteScalar()!;
		}

		int offset = Math.Max(0, page) * pageSize;
		var items = new List<LootListRow>();
		using (var cmd = conn.CreateCommand())
		{
			cmd.CommandText = $@"
				SELECT id, slug_id FROM loot_lists {whereSql}
				ORDER BY slug_id COLLATE NOCASE
				LIMIT $limit OFFSET $offset;";
			cmd.Parameters.AddWithValue("$limit", pageSize);
			cmd.Parameters.AddWithValue("$offset", offset);
			if (hasSearch) cmd.Parameters.AddWithValue("$search", "%" + search + "%");
			using var r = cmd.ExecuteReader();
			while (r.Read()) items.Add(new LootListRow(r.GetInt64(0), r.GetString(1)));
		}
		return (items, totalCount);
	});

	public Task<(List<LootListRow> Items, int TotalCount)> GetPagedCreditsAsync(
		string search, int page, int pageSize) => Task.Run(() =>
	{
		using var conn = _open();
		bool hasSearch = !string.IsNullOrWhiteSpace(search);
		string whereSql = hasSearch ? "WHERE slug_id LIKE $search COLLATE NOCASE" : "";

		int totalCount;
		using (var countCmd = conn.CreateCommand())
		{
			countCmd.CommandText = $"SELECT COUNT(*) FROM loot_lists_credits {whereSql};";
			if (hasSearch) countCmd.Parameters.AddWithValue("$search", "%" + search + "%");
			totalCount = (int)(long)countCmd.ExecuteScalar()!;
		}

		int offset = Math.Max(0, page) * pageSize;
		var items = new List<LootListRow>();
		using (var cmd = conn.CreateCommand())
		{
			cmd.CommandText = $@"
				SELECT id, slug_id FROM loot_lists_credits {whereSql}
				ORDER BY slug_id COLLATE NOCASE
				LIMIT $limit OFFSET $offset;";
			cmd.Parameters.AddWithValue("$limit", pageSize);
			cmd.Parameters.AddWithValue("$offset", offset);
			if (hasSearch) cmd.Parameters.AddWithValue("$search", "%" + search + "%");
			using var r = cmd.ExecuteReader();
			while (r.Read()) items.Add(new LootListRow(r.GetInt64(0), r.GetString(1)));
		}
		return (items, totalCount);
	});

	public record LootBagEntry(string Slug, int Min, int Max);

	public Task<(long Id, string Slug, List<LootBagEntry> Entries)?> GetItemsByIdAsync(long id)
		=> Task.Run(() =>
	{
		using var conn = _open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT id, slug_id, item_bag FROM loot_lists WHERE id = $id;";
		cmd.Parameters.AddWithValue("$id", id);
		using var r = cmd.ExecuteReader();
		if (!r.Read()) return ((long, string, List<LootBagEntry>)?)null;
		long realId = r.GetInt64(0);
		string slug = r.GetString(1);
		string json = r.IsDBNull(2) ? "{}" : r.GetString(2);
		var entries = new List<LootBagEntry>();
		try
		{
			using var doc = System.Text.Json.JsonDocument.Parse(json);
			foreach (var prop in doc.RootElement.EnumerateObject())
			{
				if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number
					&& prop.Value.TryGetInt32(out int qty) && qty > 0)
				{
					entries.Add(new LootBagEntry(prop.Name, qty, qty));
				}
				else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array
					&& prop.Value.GetArrayLength() == 2
					&& prop.Value[0].TryGetInt32(out int min)
					&& prop.Value[1].TryGetInt32(out int max)
					&& min >= 0 && max >= min)
				{
					entries.Add(new LootBagEntry(prop.Name, min, max));
				}
			}
		}
		catch (System.Text.Json.JsonException e)
		{
			GD.PrintErr($"[LootRepository] item_bag parse error on id {id}: {e.Message}");
		}
		return (realId, slug, entries);
	});

	public Task<(long Id, string Slug, int MinCredits, int MaxCredits)?> GetCreditsByIdAsync(long id)
		=> Task.Run(() =>
	{
		using var conn = _open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT id, slug_id, min_credits, max_credits FROM loot_lists_credits WHERE id = $id;";
		cmd.Parameters.AddWithValue("$id", id);
		using var r = cmd.ExecuteReader();
		if (!r.Read()) return ((long, string, int, int)?)null;
		return (r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3));
	});

	/// <summary>
	/// Update slug_id + item_bag together. Returns (ok, finalSlug, error).
	/// Validates slug uniqueness when it changes; on failure the row is untouched.
	/// </summary>
	public Task<(bool Ok, string Slug, string Error)> SaveItemsAsync(
		long id, string slug, List<LootBagEntry> entries) => Task.Run(() =>
	{
		slug = (slug ?? "").Trim();
		if (string.IsNullOrEmpty(slug)) return (false, "", "slug_required");

		using var conn = _open();
		using var tx = conn.BeginTransaction();
		try
		{
			string? currentSlug;
			using (var check = conn.CreateCommand())
			{
				check.Transaction = tx;
				check.CommandText = "SELECT slug_id FROM loot_lists WHERE id = $id;";
				check.Parameters.AddWithValue("$id", id);
				currentSlug = check.ExecuteScalar()?.ToString();
			}
			if (currentSlug == null) { tx.Rollback(); return (false, "", "not_found"); }

			if (currentSlug != slug)
			{
				using var dup = conn.CreateCommand();
				dup.Transaction = tx;
				dup.CommandText = "SELECT 1 FROM loot_lists WHERE slug_id = $slug AND id != $id;";
				dup.Parameters.AddWithValue("$slug", slug);
				dup.Parameters.AddWithValue("$id", id);
				if (dup.ExecuteScalar() != null) { tx.Rollback(); return (false, "", "slug_taken"); }
			}

			using var stream = new System.IO.MemoryStream();
			using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
			{
				writer.WriteStartObject();
				foreach (var entry in entries)
				{
					if (entry.Min < 0 || entry.Max < entry.Min) continue;
					if (entry.Min == entry.Max)
						writer.WriteNumber(entry.Slug, entry.Min);
					else
					{
						writer.WritePropertyName(entry.Slug);
						writer.WriteStartArray();
						writer.WriteNumberValue(entry.Min);
						writer.WriteNumberValue(entry.Max);
						writer.WriteEndArray();
					}
				}
				writer.WriteEndObject();
			}
			string json = System.Text.Encoding.UTF8.GetString(stream.ToArray());

			using (var cmd = conn.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = "UPDATE loot_lists SET slug_id = $slug, item_bag = $bag WHERE id = $id;";
				cmd.Parameters.AddWithValue("$slug", slug);
				cmd.Parameters.AddWithValue("$bag", json);
				cmd.Parameters.AddWithValue("$id", id);
				if (cmd.ExecuteNonQuery() != 1) { tx.Rollback(); return (false, "", "save_failed"); }
			}

			tx.Commit();
			return (true, slug, "");
		}
		catch (SqliteException e)
		{
			try { tx.Rollback(); } catch { }
			return (false, "", e.Message);
		}
		catch
		{
			try { tx.Rollback(); } catch { }
			throw;
		}
	});

	public Task<(bool Ok, string Slug, string Error)> SaveCreditsAsync(
		long id, string slug, int minCredits, int maxCredits) => Task.Run(() =>
	{
		slug = (slug ?? "").Trim();
		if (string.IsNullOrEmpty(slug)) return (false, "", "slug_required");
		if (minCredits < 0 || maxCredits < minCredits) return (false, "", "invalid_range");

		using var conn = _open();
		using var tx = conn.BeginTransaction();
		try
		{
			string? currentSlug;
			using (var check = conn.CreateCommand())
			{
				check.Transaction = tx;
				check.CommandText = "SELECT slug_id FROM loot_lists_credits WHERE id = $id;";
				check.Parameters.AddWithValue("$id", id);
				currentSlug = check.ExecuteScalar()?.ToString();
			}
			if (currentSlug == null) { tx.Rollback(); return (false, "", "not_found"); }

			if (currentSlug != slug)
			{
				using var dup = conn.CreateCommand();
				dup.Transaction = tx;
				dup.CommandText = "SELECT 1 FROM loot_lists_credits WHERE slug_id = $slug AND id != $id;";
				dup.Parameters.AddWithValue("$slug", slug);
				dup.Parameters.AddWithValue("$id", id);
				if (dup.ExecuteScalar() != null) { tx.Rollback(); return (false, "", "slug_taken"); }
			}

			using (var cmd = conn.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = "UPDATE loot_lists_credits SET slug_id = $slug, min_credits = $min, max_credits = $max WHERE id = $id;";
				cmd.Parameters.AddWithValue("$slug", slug);
				cmd.Parameters.AddWithValue("$min", minCredits);
				cmd.Parameters.AddWithValue("$max", maxCredits);
				cmd.Parameters.AddWithValue("$id", id);
				if (cmd.ExecuteNonQuery() != 1) { tx.Rollback(); return (false, "", "save_failed"); }
			}

			tx.Commit();
			return (true, slug, "");
		}
		catch (SqliteException e)
		{
			try { tx.Rollback(); } catch { }
			return (false, "", e.Message);
		}
		catch
		{
			try { tx.Rollback(); } catch { }
			throw;
		}
	});

	// ---------- Create / delete ----------

	public Task<long> CreateItemsAsync(string slug) => Task.Run(() =>
	{
		using var conn = _open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "INSERT INTO loot_lists (slug_id, item_bag) VALUES ($slug, '{}') RETURNING id;";
		cmd.Parameters.AddWithValue("$slug", slug);
		try
		{
			return (long)cmd.ExecuteScalar()!;
		}
		catch (SqliteException e) when (e.SqliteErrorCode == 19)
		{
			return -1L;
		}
	});

	public Task<bool> DeleteItemsAsync(long id) => Task.Run(() =>
	{
		using var conn = _open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "DELETE FROM loot_lists WHERE id = $id;";
		cmd.Parameters.AddWithValue("$id", id);
		return cmd.ExecuteNonQuery() == 1;
	});

	public Task<long> CreateCreditsAsync(string slug) => Task.Run(() =>
	{
		using var conn = _open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "INSERT INTO loot_lists_credits (slug_id, min_credits, max_credits) VALUES ($slug, 0, 0) RETURNING id;";
		cmd.Parameters.AddWithValue("$slug", slug);
		try
		{
			return (long)cmd.ExecuteScalar()!;
		}
		catch (SqliteException e) when (e.SqliteErrorCode == 19)
		{
			return -1L;
		}
	});

	public Task<bool> DeleteCreditsAsync(long id) => Task.Run(() =>
	{
		using var conn = _open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "DELETE FROM loot_lists_credits WHERE id = $id;";
		cmd.Parameters.AddWithValue("$id", id);
		return cmd.ExecuteNonQuery() == 1;
	});
}