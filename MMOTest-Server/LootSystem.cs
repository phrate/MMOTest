using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Admin LootEditor backend. Mirrors NpcSystem / ItemSystem dev-handler
/// shape — session.IsAdmin gate, structured replies. All lists are DB-paged;
/// nothing is preloaded into memory.
///
/// Shares the loot-table dictionaries that NpcSystem / CombatSystem /
/// AuthSystem hold, so a reload after any create/delete propagates to
/// NPC-editor dropdowns, loot rolls, and dev account seeding in one go.
/// </summary>
internal sealed class LootSystem
{
	public const int PageSize = 100;

	private readonly ServerState _state;
	private readonly OutboundChannel _outbound;
	private readonly LootRepository _loot;
	private readonly ItemRepository _items;
	private readonly Dictionary<string, Dictionary<string, (int Min, int Max)>> _lootTablesBySlug;
	private readonly Dictionary<string, (int Min, int Max)> _creditsLootBySlug;

	public LootSystem(ServerState state, OutboundChannel outbound,
		LootRepository loot, ItemRepository items,
		Dictionary<string, Dictionary<string, (int Min, int Max)>> lootTablesBySlug,
		Dictionary<string, (int Min, int Max)> creditsLootBySlug)
	{
		_state = state;
		_outbound = outbound;
		_loot = loot;
		_items = items;
		_lootTablesBySlug = lootTablesBySlug;
		_creditsLootBySlug = creditsLootBySlug;
	}

	// ---------- Reload shared loot caches ----------

	private void ReloadLootCaches()
	{
		try
		{
			var freshItems = _loot.GetLootTablesSync();
			_lootTablesBySlug.Clear();
			foreach (var kv in freshItems) _lootTablesBySlug[kv.Key] = kv.Value;

			var freshCredits = _loot.GetCreditsLootSync();
			_creditsLootBySlug.Clear();
			foreach (var kv in freshCredits) _creditsLootBySlug[kv.Key] = kv.Value;
		}
		catch (Exception e)
		{
			GD.PrintErr($"[LootSystem] ReloadLootCaches: {e.Message}");
		}
	}

	// ---------- Items sub-tab ----------

	internal void HandleDevLootListItems(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		string search = GetStr(root, "search");
		int page = GetInt(root, "page", 0);
		_ = ListItemsAsync(peerId, search, page);
	}

	private async System.Threading.Tasks.Task ListItemsAsync(long peerId, string search, int page)
	{
		try
		{
			var (items, total) = await _loot.GetPagedItemsAsync(search, page, PageSize);
			var payload = new List<object>(items.Count);
			foreach (var item in items)
				payload.Add(new { id = item.Id, slug_id = item.Slug });
			_outbound.Send(peerId, new
			{
				type = "dev_loot_list_items_result",
				ok = true,
				items = payload,
				total_count = total,
				page,
				page_size = PageSize,
			});
		}
		catch (Exception e)
		{
			GD.PrintErr($"[LootSystem] ListItemsAsync: {e.Message}");
			_outbound.Send(peerId, new
			{
				type = "dev_loot_list_items_result",
				ok = false, items = new List<object>(),
				total_count = 0, page, page_size = PageSize,
			});
		}
	}

	internal void HandleDevLootGetItems(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id)) return;
		_ = GetItemsAsync(peerId, id);
	}

	private async System.Threading.Tasks.Task GetItemsAsync(long peerId, long id)
	{
		try
		{
			var result = await _loot.GetItemsByIdAsync(id);
			if (result == null)
			{
				_outbound.Send(peerId, new { type = "dev_loot_items_data", ok = false });
				return;
			}
			var (realId, slug, entries) = result.Value;
			var entriesPayload = new List<object>(entries.Count);
			foreach (var e in entries)
				entriesPayload.Add(new { slug = e.Slug, min = e.Min, max = e.Max });
			_outbound.Send(peerId, new
			{
				type = "dev_loot_items_data",
				ok = true,
				data = new { id = realId, slug_id = slug, item_bag = entriesPayload },
			});
		}
		catch (Exception e)
		{
			GD.PrintErr($"[LootSystem] GetItemsAsync: {e.Message}");
			_outbound.Send(peerId, new { type = "dev_loot_items_data", ok = false });
		}
	}

	internal void HandleDevLootSaveItems(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id)) return;
		string slug = GetStr(root, "slug_id");
		if (!root.TryGetProperty("item_bag", out var bagEl)
			|| bagEl.ValueKind != JsonValueKind.Array) return;

		var entries = new List<LootRepository.LootBagEntry>();
		foreach (var el in bagEl.EnumerateArray())
		{
			if (el.ValueKind != JsonValueKind.Object) continue;
			string eSlug = GetStr(el, "slug").Trim();
			if (string.IsNullOrEmpty(eSlug)) continue;
			int min = GetInt(el, "min", 0);
			int max = GetInt(el, "max", min);
			if (min < 0 || max < min) continue;
			entries.Add(new LootRepository.LootBagEntry(eSlug, min, max));
		}
		_ = SaveItemsAsync(peerId, id, slug, entries);
	}

	private async System.Threading.Tasks.Task SaveItemsAsync(long peerId, long id,
		string slug, List<LootRepository.LootBagEntry> entries)
	{
		try
		{
			var (ok, finalSlug, error) = await _loot.SaveItemsAsync(id, slug, entries);
			_outbound.Send(peerId, new
			{
				type = "dev_loot_items_saved",
				ok, id, slug = finalSlug, error,
			});
			if (ok) ReloadLootCaches();
		}
		catch (Exception e)
		{
			GD.PrintErr($"[LootSystem] SaveItemsAsync: {e.Message}");
			_outbound.Send(peerId, new
			{
				type = "dev_loot_items_saved",
				ok = false, id, slug = "", error = e.Message,
			});
		}
	}

	// ---------- Credits sub-tab ----------

	internal void HandleDevLootListCredits(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		string search = GetStr(root, "search");
		int page = GetInt(root, "page", 0);
		_ = ListCreditsAsync(peerId, search, page);
	}

	private async System.Threading.Tasks.Task ListCreditsAsync(long peerId, string search, int page)
	{
		try
		{
			var (items, total) = await _loot.GetPagedCreditsAsync(search, page, PageSize);
			var payload = new List<object>(items.Count);
			foreach (var item in items)
				payload.Add(new { id = item.Id, slug_id = item.Slug });
			_outbound.Send(peerId, new
			{
				type = "dev_loot_list_credits_result",
				ok = true, items = payload, total_count = total,
				page, page_size = PageSize,
			});
		}
		catch (Exception e)
		{
			GD.PrintErr($"[LootSystem] ListCreditsAsync: {e.Message}");
			_outbound.Send(peerId, new
			{
				type = "dev_loot_list_credits_result",
				ok = false, items = new List<object>(),
				total_count = 0, page, page_size = PageSize,
			});
		}
	}

	internal void HandleDevLootGetCredits(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id)) return;
		_ = GetCreditsAsync(peerId, id);
	}

	private async System.Threading.Tasks.Task GetCreditsAsync(long peerId, long id)
	{
		try
		{
			var result = await _loot.GetCreditsByIdAsync(id);
			if (result == null)
			{
				_outbound.Send(peerId, new { type = "dev_loot_credits_data", ok = false });
				return;
			}
			var (realId, slug, min, max) = result.Value;
			_outbound.Send(peerId, new
			{
				type = "dev_loot_credits_data",
				ok = true,
				data = new
				{
					id = realId, slug_id = slug,
					min_credits = min, max_credits = max,
				},
			});
		}
		catch (Exception e)
		{
			GD.PrintErr($"[LootSystem] GetCreditsAsync: {e.Message}");
			_outbound.Send(peerId, new { type = "dev_loot_credits_data", ok = false });
		}
	}

	internal void HandleDevLootSaveCredits(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id)) return;
		string slug = GetStr(root, "slug_id");
		int min = GetInt(root, "min_credits", 0);
		int max = GetInt(root, "max_credits", 0);
		_ = SaveCreditsAsync(peerId, id, slug, min, max);
	}

	private async System.Threading.Tasks.Task SaveCreditsAsync(long peerId, long id,
		string slug, int min, int max)
	{
		try
		{
			var (ok, finalSlug, error) = await _loot.SaveCreditsAsync(id, slug, min, max);
			_outbound.Send(peerId, new
			{
				type = "dev_loot_credits_saved",
				ok, id, slug = finalSlug, error,
			});
			if (ok) ReloadLootCaches();
		}
		catch (Exception e)
		{
			GD.PrintErr($"[LootSystem] SaveCreditsAsync: {e.Message}");
			_outbound.Send(peerId, new
			{
				type = "dev_loot_credits_saved",
				ok = false, id, slug = "", error = e.Message,
			});
		}
	}

	// ---------- Create / delete ----------

	internal void HandleDevLootCreateItems(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		string slug = GetStr(root, "slug_id").Trim();
		if (string.IsNullOrEmpty(slug))
		{
			_outbound.Send(peerId, new { type = "dev_loot_created_items", ok = false, error = "slug_required" });
			return;
		}
		_ = CreateItemsAsync(peerId, slug);
	}

	private async System.Threading.Tasks.Task CreateItemsAsync(long peerId, string slug)
	{
		try
		{
			long newId = await _loot.CreateItemsAsync(slug);
			if (newId < 0)
			{
				_outbound.Send(peerId, new { type = "dev_loot_created_items", ok = false, error = "slug_taken" });
				return;
			}
			ReloadLootCaches();
			_outbound.Send(peerId, new { type = "dev_loot_created_items", ok = true, id = newId });
		}
		catch (Exception e)
		{
			GD.PrintErr($"[LootSystem] CreateItemsAsync: {e.Message}");
			_outbound.Send(peerId, new { type = "dev_loot_created_items", ok = false, error = e.Message });
		}
	}

	internal void HandleDevLootDeleteItems(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id)) return;
		_ = DeleteItemsAsync(peerId, id);
	}

	private async System.Threading.Tasks.Task DeleteItemsAsync(long peerId, long id)
	{
		try
		{
			bool ok = await _loot.DeleteItemsAsync(id);
			if (ok) ReloadLootCaches();
			_outbound.Send(peerId, new { type = "dev_loot_deleted_items", ok, id });
		}
		catch (Exception e)
		{
			GD.PrintErr($"[LootSystem] DeleteItemsAsync: {e.Message}");
			_outbound.Send(peerId, new { type = "dev_loot_deleted_items", ok = false, id });
		}
	}

	internal void HandleDevLootCreateCredits(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		string slug = GetStr(root, "slug_id").Trim();
		if (string.IsNullOrEmpty(slug))
		{
			_outbound.Send(peerId, new { type = "dev_loot_created_credits", ok = false, error = "slug_required" });
			return;
		}
		_ = CreateCreditsAsync(peerId, slug);
	}

	private async System.Threading.Tasks.Task CreateCreditsAsync(long peerId, string slug)
	{
		try
		{
			long newId = await _loot.CreateCreditsAsync(slug);
			if (newId < 0)
			{
				_outbound.Send(peerId, new { type = "dev_loot_created_credits", ok = false, error = "slug_taken" });
				return;
			}
			ReloadLootCaches();
			_outbound.Send(peerId, new { type = "dev_loot_created_credits", ok = true, id = newId });
		}
		catch (Exception e)
		{
			GD.PrintErr($"[LootSystem] CreateCreditsAsync: {e.Message}");
			_outbound.Send(peerId, new { type = "dev_loot_created_credits", ok = false, error = e.Message });
		}
	}

	internal void HandleDevLootDeleteCredits(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id)) return;
		_ = DeleteCreditsAsync(peerId, id);
	}

	private async System.Threading.Tasks.Task DeleteCreditsAsync(long peerId, long id)
	{
		try
		{
			bool ok = await _loot.DeleteCreditsAsync(id);
			if (ok) ReloadLootCaches();
			_outbound.Send(peerId, new { type = "dev_loot_deleted_credits", ok, id });
		}
		catch (Exception e)
		{
			GD.PrintErr($"[LootSystem] DeleteCreditsAsync: {e.Message}");
			_outbound.Send(peerId, new { type = "dev_loot_deleted_credits", ok = false, id });
		}
	}

	// ---------- Item autosuggest ----------

	internal void HandleDevItemSearch(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		string search = GetStr(root, "search");
		var results = _items.SearchSync(search, 30);
		var payload = new List<object>(results.Count);
		foreach (var (id, slug, display) in results)
			payload.Add(new { id, slug_id = slug, display });
		_outbound.Send(peerId, new
		{
			type = "dev_item_search_result",
			ok = true, items = payload,
		});
	}

	// ---------- Helpers ----------

	private static string GetStr(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var el)) return "";
		if (el.ValueKind != JsonValueKind.String) return "";
		return el.GetString() ?? "";
	}

	private static int GetInt(JsonElement root, string name, int fallback)
	{
		if (!root.TryGetProperty(name, out var el)) return fallback;
		if (!el.TryGetInt32(out int v)) return fallback;
		return v;
	}
}