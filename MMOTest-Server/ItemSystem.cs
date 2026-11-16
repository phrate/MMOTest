using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns inventory mutation, item giving, item consumption, HP healing via
/// consumables, and inventory slot reordering.
///
/// Coupling: needs TradeSystem to gate mutations during active trades.
/// </summary>
internal sealed class ItemSystem
{
    private const int ConsumeCooldownTicks = 1;

    private readonly ServerState _state;
    private readonly OutboundChannel _outbound;
    private readonly ItemLookup _items;
    private readonly InventoryRepository _inventory;
    private readonly PlayerStatsRepository _stats;
    private readonly TradeSystem _trade;
    private readonly Random _combatRng;
    private readonly PlayerRepository _players;
    private readonly ItemRepository _itemRepo;
    private readonly ItemIconManager _icons;

    private readonly Dictionary<long, long> _lastConsumeTick = new();

    public ItemSystem(ServerState state, OutboundChannel outbound, ItemLookup items,
                    InventoryRepository inventory, PlayerStatsRepository stats,
                    TradeSystem trade, Random combatRng,
                    PlayerRepository players, ItemRepository itemRepo,
                    ItemIconManager icons)
    {
        _state = state;
        _outbound = outbound;
        _items = items;
        _inventory = inventory;
        _stats = stats;
        _trade = trade;
        _combatRng = combatRng;
        _players = players;
        _itemRepo = itemRepo;
        _icons = icons;
    }

    internal void ClearPlayer(long playerId) => _lastConsumeTick.Remove(playerId);

    // ============================================================
    // Slot reads
    // ============================================================

    internal string? GetSlotItemSlug(long playerId, int slot)
    {
        if (!_state.Inventories.TryGetValue(playerId, out var inv)) return null;
        if (slot < 0 || slot >= 28) return null;
        long packed = inv[slot];
        if (packed == 0) return null;
        int itemId = ItemStack.ItemId(packed);
        return _items.TryGetById(itemId, out var meta) ? meta.Slug : null;
    }

    internal int GetSlotQuantity(long playerId, int slot)
    {
        if (!_state.Inventories.TryGetValue(playerId, out var inv)) return 0;
        if (slot < 0 || slot >= 28) return 0;
        long packed = inv[slot];
        return packed == 0 ? 0 : ItemStack.Quantity(packed);
    }

    // ============================================================
    // Slot mutation
    // ============================================================

    internal bool ConsumeInventorySlot(long playerId, int slot, int qty)
    {
        if (_trade.IsTradeInventoryLocked(playerId)) return false;
        if (!_state.Inventories.TryGetValue(playerId, out var inv)) return false;
        if (slot < 0 || slot >= 28) return false;
        long packed = inv[slot];
        if (packed == 0) return false;
        int currentQty = ItemStack.Quantity(packed);
        if (currentQty < qty) return false;
        int itemId = ItemStack.ItemId(packed);
        int newQty = currentQty - qty;
        inv[slot] = newQty > 0 ? ItemStack.Encode(itemId, newQty) : 0;
        _inventory.SaveSlotSync(playerId, slot, inv[slot]);
        _outbound.BroadcastInventory(playerId);
        return true;
    }

    internal bool TryConsumeItem(long playerId, int slot, int healAmount)
    {
        if (_lastConsumeTick.TryGetValue(playerId, out long lastTick)
            && _state.CurrentTick - lastTick < ConsumeCooldownTicks)
            return false;

        if (!ConsumeInventorySlot(playerId, slot, 1)) return false;

        _lastConsumeTick[playerId] = _state.CurrentTick;
        if (healAmount > 0) HealPlayer(playerId, healAmount);
        return true;
    }

    internal void HealPlayer(long playerId, int amount)
    {
        if (!_state.PlayerCombat.TryGetValue(playerId, out var pd)) return;
        int newHp = Math.Min(pd.MaxHp, pd.CurrentHp + amount);
        if (newHp == pd.CurrentHp) return;
        _state.PlayerCombat[playerId] = pd with { CurrentHp = newHp };
        _ = _stats.SetPlayerHpAsync(playerId, newHp);
        _outbound.BroadcastPlayerHp(playerId, newHp, pd.MaxHp);
    }

    internal void HandleInventorySwap(long peerId, JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (_trade.IsTradeInventoryLocked(session.PlayerId)) return;
        if (!root.TryGetProperty("slot_a", out var aEl) || !aEl.TryGetInt32(out int slotA)) return;
        if (!root.TryGetProperty("slot_b", out var bEl) || !bEl.TryGetInt32(out int slotB)) return;
        if (slotA == slotB) return;
        if (slotA < 0 || slotA >= 28 || slotB < 0 || slotB >= 28) return;
        if (!_state.Inventories.TryGetValue(session.PlayerId, out var inv)) return;

        long tmp = inv[slotA];
        inv[slotA] = inv[slotB];
        inv[slotB] = tmp;

        _inventory.SaveSlotSync(session.PlayerId, slotA, inv[slotA]);
        _inventory.SaveSlotSync(session.PlayerId, slotB, inv[slotB]);
        _outbound.BroadcastInventory(session.PlayerId);
        Diag.Log("diag_item", $"Swap player {session.PlayerId}: slot {slotA} <-> {slotB}");
    }

    // ============================================================
    // Item giving
    // ============================================================

    internal bool GiveItem(long playerId, string itemSlug, int qty)
    {
        if (_trade.IsTradeInventoryLocked(playerId)) return false;
        if (qty < 1) return false;
        if (!_items.ContainsSlug(itemSlug)) return false;
        if (!_state.Inventories.ContainsKey(playerId)) return false;
        _ = GiveItemAsync(playerId, itemSlug, qty);
        return true;
    }

    internal async Task<bool> GiveItemAsync(long playerId, string itemSlug, int qty)
    {
        if (_trade.IsTradeInventoryLocked(playerId)) return false;
        if (qty < 1) return false;
        if (!_items.TryGetIdBySlug(itemSlug, out long itemId))
        {
            GD.PrintErr($"[GiveItem] Unknown slug '{itemSlug}' (player {playerId})");
            return false;
        }
        if (!_items.TryGetById(itemId, out var meta))
        {
            GD.PrintErr($"[GiveItem] Item id {itemId} ('{itemSlug}') missing from _itemsById");
            return false;
        }

        if (!_state.Inventories.TryGetValue(playerId, out var inv))
        {
            inv = await _inventory.GetSlotsAsync(playerId);
            if (inv == null) inv = new long[28];
            _state.Inventories[playerId] = inv;
        }

        if (meta.Stackable)
        {
            for (int i = 0; i < 28; i++)
            {
                long packed = inv[i];
                if (packed == 0) continue;
                if (ItemStack.ItemId(packed) != itemId) continue;
                int newQty = ItemStack.Quantity(packed) + qty;
                inv[i] = ItemStack.Encode((int)itemId, newQty);
                _inventory.SaveSlotSync(playerId, i, inv[i]);
                _outbound.BroadcastInventory(playerId);
                return true;
            }
            for (int i = 0; i < 28; i++)
            {
                if (inv[i] != 0) continue;
                inv[i] = ItemStack.Encode((int)itemId, qty);
                _inventory.SaveSlotSync(playerId, i, inv[i]);
                _outbound.BroadcastInventory(playerId);
                return true;
            }
            return false;
        }
        else
        {
            int placed = 0;
            for (int i = 0; i < 28 && placed < qty; i++)
            {
                if (inv[i] != 0) continue;
                inv[i] = ItemStack.Encode((int)itemId, 1);
                _inventory.SaveSlotSync(playerId, i, inv[i]);
                placed++;
            }
            if (placed > 0) _outbound.BroadcastInventory(playerId);
            return placed == qty;
        }
    }

    internal async Task GiveLootTableAsync(long playerId, string lootSlug,
                                           Dictionary<string, Dictionary<string, (int Min, int Max)>> lootTables,
                                           bool forceAll = false)
    {
        if (!lootTables.TryGetValue(lootSlug, out var table))
        {
            GD.PrintErr($"[GiveLootTable] Loot table '{lootSlug}' not loaded (was the server restarted after DB insert?)");
            return;
        }
        if (table.Count == 0)
        {
            GD.PrintErr($"[GiveLootTable] Loot table '{lootSlug}' is empty");
            return;
        }

        if (forceAll)
        {
            foreach (var kv in table)
            {
                int qty = RollQuantity(kv.Value);
                if (qty <= 0) continue;
                bool ok = await GiveItemAsync(playerId, kv.Key, qty);
                if (!ok) GD.PrintErr($"[GiveLootTable] Failed to give '{kv.Key}' x{qty} from '{lootSlug}'");
            }
            Diag.Log("diag_login", $"Gave full loot table '{lootSlug}' to player {playerId}");
        }
        else
        {
            var keys = new List<string>(table.Keys);
            string pick = keys[_combatRng.Next(keys.Count)];
            int qty = RollQuantity(table[pick]);
            if (qty > 0) await GiveItemAsync(playerId, pick, qty);
            Diag.Log("diag_login", $"Rolled '{pick}' x{qty} from loot table '{lootSlug}' for player {playerId}");
        }
    }

    internal int RollQuantity((int Min, int Max) range)
    {
        if (range.Min <= 0 && range.Max <= 0) return 0;
        if (range.Min == range.Max) return range.Min;
        return _combatRng.Next(range.Min, range.Max + 1);
    }

    internal void GiveCredits(long playerId, int amount)
    {
        if (_trade.IsTradeInventoryLocked(playerId)) return;
        if (amount <= 0) return;
        if (!_state.Credits.TryGetValue(playerId, out int current)) return;
        int newTotal = current + amount;
        _state.Credits[playerId] = newTotal;
        _players.SetCreditsSync(playerId, newTotal);
        _outbound.BroadcastCredits(playerId, newTotal);
    }

    // ============================================================
    // Item editor CRUD
    // ============================================================

    internal void HandleDevItemList(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;

        bool includeInactive = root.TryGetProperty("include_inactive", out var incEl)
            && incEl.ValueKind == System.Text.Json.JsonValueKind.True;

        var items = _itemRepo.GetForEditorSync(includeInactive);
        var list = new List<object>();
        foreach (var it in items)
        {
            list.Add(new
            {
                id = it.Id,
                slug_id = it.Slug,
                display = it.Display,
                type = it.Type,
                stackable = it.Stackable,
                can_trade = it.CanTrade,
                base_price = it.BasePrice,
                is_active = it.IsActive,
                tags = it.Tags,
            });
        }
        _outbound.Send(peerId, new { type = "dev_item_list_result", ok = true, items = list });
    }

    internal void HandleDevItemTagsList(long peerId)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        _outbound.Send(peerId, new
        {
            type = "dev_item_tags_list_result",
            ok = true,
            tags = _itemRepo.GetAllTagsSync(),
        });
    }

    internal void HandleDevItemGet(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        if (!root.TryGetProperty("item_id", out var idEl) || !idEl.TryGetInt64(out long itemId)) return;

        var item = _itemRepo.GetByIdSync(itemId);
        if (item == null)
        {
            _outbound.Send(peerId, new { type = "dev_item_data", ok = false });
            return;
        }
        _outbound.Send(peerId, new { type = "dev_item_data", ok = true, data = ItemMetaToDict(item) });
    }

    internal void HandleDevItemCreate(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        if (!root.TryGetProperty("data", out var dataEl)
            || dataEl.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        var u = ParseItemPayload(dataEl);
        string slug = GetStr(u, "slug_id", "").Trim();
        string display = GetStr(u, "display", "").Trim();

        string? error = null;
        if (string.IsNullOrEmpty(slug))         error = "Slug is required.";
        else if (string.IsNullOrEmpty(display)) error = "Display is required.";
        else if (_itemRepo.SlugExistsSync(slug)) error = "That slug is already taken.";

        if (error != null)
        {
            _outbound.Send(peerId, new { type = "dev_item_created", ok = false, item_id = 0L, error });
            return;
        }

        u["slug_id"] = slug;
        u["display"] = display;
        _ = CreateItemAsync(peerId, u);
    }

    private async Task CreateItemAsync(long peerId, Dictionary<string, object> u)
    {
        try
        {
            long newId = await _itemRepo.CreateAsync(u);
            await ReloadItemLookupAsync();
            _outbound.Send(peerId, new { type = "dev_item_created", ok = true, item_id = newId });
        }
        catch (Exception e)
        {
            GD.PrintErr($"[ItemSystem] CreateItem failed: {e.Message}");
            _outbound.Send(peerId, new { type = "dev_item_created", ok = false, item_id = 0L, error = e.Message });
        }
    }

    internal void HandleDevItemUpdate(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        if (!root.TryGetProperty("item_id", out var idEl) || !idEl.TryGetInt64(out long itemId)) return;
        if (!root.TryGetProperty("data", out var dataEl)
            || dataEl.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        var u = ParseItemPayload(dataEl);
        _ = UpdateItemAsync(peerId, itemId, u);
    }

    private async Task UpdateItemAsync(long peerId, long itemId, Dictionary<string, object> u)
    {
        try
        {
            bool ok = await _itemRepo.UpdateAsync(itemId, u);
            if (ok) await ReloadItemLookupAsync();
            _outbound.Send(peerId, new { type = "dev_item_updated", ok, item_id = itemId });
        }
        catch (Exception e)
        {
            GD.PrintErr($"[ItemSystem] UpdateItem failed: {e.Message}");
            _outbound.Send(peerId, new { type = "dev_item_updated", ok = false, item_id = itemId });
        }
    }

    internal void HandleDevItemDelete(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        if (!root.TryGetProperty("item_id", out var idEl) || !idEl.TryGetInt64(out long itemId)) return;
        _ = DeleteItemAsync(peerId, itemId);
    }

    private async Task DeleteItemAsync(long peerId, long itemId)
    {
        bool ok;
        try { ok = await _itemRepo.SoftDeleteAsync(itemId); }
        catch (Exception e)
        {
            GD.PrintErr($"[ItemSystem] DeleteItem failed: {e.Message}");
            ok = false;
        }
        if (ok) await ReloadItemLookupAsync();
        _outbound.Send(peerId, new { type = "dev_item_deleted", ok, item_id = itemId });
    }

    internal void HandleDevItemGive(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;

        if (!root.TryGetProperty("item_id", out var idEl) || !idEl.TryGetInt64(out long itemId)) return;
        if (!root.TryGetProperty("target_username", out var unEl)
            || unEl.ValueKind != System.Text.Json.JsonValueKind.String) return;
        string targetUsername = (unEl.GetString() ?? "").Trim();
        if (string.IsNullOrEmpty(targetUsername)) return;
        if (!root.TryGetProperty("amount", out var amEl)
            || !amEl.TryGetInt32(out int amount) || amount < 1) return;
        bool isCert = root.TryGetProperty("is_cert", out var cEl)
            && cEl.ValueKind == System.Text.Json.JsonValueKind.True;

        _ = ProcessGiveAsync(peerId, itemId, targetUsername, amount, isCert);
    }

    private async System.Threading.Tasks.Task ProcessGiveAsync(
        long peerId, long itemId, string targetUsername, int amount, bool isCert)
    {
        var item = _itemRepo.GetByIdSync(itemId);
        if (item == null) { SendGiveError(peerId, ErrorKeys.ItemGiveFailed, null); return; }

        string outputSlug = item.Slug;
        if (isCert)
        {
            if (item.Stackable || item.NonCertable || !string.IsNullOrEmpty(item.CertOfSlug))
            {
                SendGiveError(peerId, ErrorKeys.ItemGiveCertUnavailable, null);
                return;
            }
            outputSlug = item.Slug + "_cert";
            if (!_items.ContainsSlug(outputSlug))
            {
                SendGiveError(peerId, ErrorKeys.ItemGiveCertUnavailable, null);
                return;
            }
        }

        var targetPlayer = await _players.GetByUsernameAsync(targetUsername);
        if (targetPlayer == null)
        {
            SendGiveError(peerId, ErrorKeys.ItemGivePlayerNotFound,
                new { username = targetUsername });
            return;
        }

        if (!_items.TryGetById(_items.GetIdBySlug(outputSlug), out var outMeta))
        {
            SendGiveError(peerId, ErrorKeys.ItemGiveFailed, null);
            return;
        }

        if (_trade.IsTradeInventoryLocked(targetPlayer.Id))
        {
            SendGiveError(peerId, ErrorKeys.ItemGivePlayerBusy, null);
            return;
        }

        long[] inv;
        if (_state.Inventories.TryGetValue(targetPlayer.Id, out var liveInv))
        {
            inv = liveInv;
        }
        else
        {
            var loaded = await _inventory.GetSlotsAsync(targetPlayer.Id);
            if (loaded == null)
            {
                SendGiveError(peerId, ErrorKeys.ItemGiveInventoryFull, null);
                return;
            }
            inv = loaded;
        }

        if (!HasSpaceFor(inv, outMeta, amount))
        {
            SendGiveError(peerId, ErrorKeys.ItemGiveInventoryFull,
                new { amount });
            return;
        }

        bool ok = await GiveItemAsync(targetPlayer.Id, outputSlug, amount);
        if (!ok)
        {
            SendGiveError(peerId, ErrorKeys.ItemGiveFailed, null);
            return;
        }

        _outbound.Send(peerId, new
        {
            type = "dev_item_give_result",
            ok = true,
            item_slug = outputSlug,
            target_username = targetPlayer.Username,
            amount,
        });
        Diag.Log("diag_item",
            $"Dev give: {outputSlug} x{amount} to {targetPlayer.Username} (id {targetPlayer.Id})");
    }

    private static bool HasSpaceFor(long[] inv, ItemMeta meta, int amount)
    {
        if (meta.Stackable)
        {
            for (int i = 0; i < inv.Length; i++)
                if (inv[i] != 0 && ItemStack.ItemId(inv[i]) == meta.Id) return true;
            for (int i = 0; i < inv.Length; i++)
                if (inv[i] == 0) return true;
            return false;
        }
        int empty = 0;
        for (int i = 0; i < inv.Length; i++) if (inv[i] == 0) empty++;
        return empty >= amount;
    }

    private void SendGiveError(long peerId, string errorKey, object? errorParams)
    {
        _outbound.Send(peerId, new
        {
            type = "dev_item_give_result",
            ok = false,
            error_key = errorKey,
            error_params = errorParams ?? new { },
        });
    }

    internal void HandleItemIconInfo(long peerId)
    {
        if (!_state.Sessions.ContainsKey(peerId)) return;
        _outbound.Send(peerId, new
        {
            type = "item_icon_info_result",
            ok = true,
            manifest = _icons.GetManifest(),
        });
    }

    internal void HandleItemIconFetch(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.ContainsKey(peerId)) return;
        if (!root.TryGetProperty("names", out var namesEl)
            || namesEl.ValueKind != System.Text.Json.JsonValueKind.Array) return;

        foreach (var nameEl in namesEl.EnumerateArray())
        {
            if (nameEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
            string name = nameEl.GetString() ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            var bytes = _icons.GetIconBytes(name);
            if (bytes == null) continue;
            _outbound.Send(peerId, new
            {
                type = "item_icon_data",
                name,
                hash = _icons.GetIconHash(name) ?? "",
                data = Convert.ToBase64String(bytes),
            });
        }
    }

    private async Task ReloadItemLookupAsync()
    {
        var all = await _itemRepo.GetAllSync();
        _items.Load(all);
    }

    private static Dictionary<string, object> ParseItemPayload(System.Text.Json.JsonElement obj)
    {
        var result = new Dictionary<string, object>();
        foreach (var prop in obj.EnumerateObject())
        {
            switch (prop.Value.ValueKind)
            {
                case System.Text.Json.JsonValueKind.True:   result[prop.Name] = true; break;
                case System.Text.Json.JsonValueKind.False:  result[prop.Name] = false; break;
                case System.Text.Json.JsonValueKind.String: result[prop.Name] = prop.Value.GetString() ?? ""; break;
                case System.Text.Json.JsonValueKind.Number: result[prop.Name] = prop.Value.TryGetInt64(out long l) ? l : 0L; break;
                case System.Text.Json.JsonValueKind.Array:
                    var list = new List<string>();
                    foreach (var el in prop.Value.EnumerateArray())
                        if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                            list.Add(el.GetString() ?? "");
                    result[prop.Name] = list;
                    break;
                case System.Text.Json.JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var inner in prop.Value.EnumerateObject())
                    {
                        dict[inner.Name] = inner.Value.ValueKind switch
                        {
                            System.Text.Json.JsonValueKind.True => true,
                            System.Text.Json.JsonValueKind.False => false,
                            System.Text.Json.JsonValueKind.String => inner.Value.GetString() ?? "",
                            System.Text.Json.JsonValueKind.Number => inner.Value.TryGetInt64(out long il) ? il : 0L,
                            _ => (object)0L,
                        };
                    }
                    result[prop.Name] = dict;
                    break;
                case System.Text.Json.JsonValueKind.Null:
                    result[prop.Name] = null!;
                    break;
            }
        }
        return result;
    }

    private static string GetStr(Dictionary<string, object> u, string key, string fallback)
    {
        if (!u.TryGetValue(key, out var v) || v == null) return fallback;
        return v.ToString() ?? fallback;
    }

    private static Dictionary<string, object> ItemMetaToDict(ItemMeta it)
    {
        var d = new Dictionary<string, object>
        {
            ["id"] = it.Id,
            ["slug_id"] = it.Slug,
            ["display"] = it.Display,
            ["type"] = it.Type,
            ["stackable"] = it.Stackable,
            ["can_trade"] = it.CanTrade,
            ["non_certable"] = it.NonCertable,
            ["non_bankable"] = it.NonBankable,
            ["is_active"] = it.IsActive,
            ["base_price"] = it.BasePrice,
            ["description"] = it.Description,
            ["icon_path"] = it.IconPath,
            ["tags"] = it.Tags,
        };
        if (it.Equipment != null)
        {
            d["equipment"] = new Dictionary<string, object>
            {
                ["slot"] = it.Equipment.Slot,
                ["attack_speed_override"] = it.Equipment.AttackSpeedOverride,
                ["attack_bonus_melee"] = it.Equipment.AttackMelee,
                ["attack_bonus_ranged"] = it.Equipment.AttackRanged,
                ["attack_bonus_ether"] = it.Equipment.AttackEther,
                ["defense_bonus_melee"] = it.Equipment.DefenseMelee,
                ["defense_bonus_ranged"] = it.Equipment.DefenseRanged,
                ["defense_bonus_ether"] = it.Equipment.DefenseEther,
                ["bonus_melee_strength"] = it.Equipment.StrengthMelee,
                ["bonus_ranged_strength"] = it.Equipment.StrengthRanged,
                ["bonus_ether_strength"] = it.Equipment.StrengthEther,
            };
        }
        if (it.Consumable != null)
        {
            d["consumable"] = new Dictionary<string, object>
            {
                ["effect_type"] = it.Consumable.EffectType,
                ["heal_amount"] = it.Consumable.HealAmount,
            };
        }
        return d;
    }
}