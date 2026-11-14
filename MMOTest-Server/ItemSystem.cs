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

    private readonly Dictionary<long, long> _lastConsumeTick = new();

    public ItemSystem(ServerState state, OutboundChannel outbound, ItemLookup items,
                  InventoryRepository inventory, PlayerStatsRepository stats,
                  TradeSystem trade, Random combatRng,
                  PlayerRepository players)
    {
        _state = state;
        _outbound = outbound;
        _items = items;
        _inventory = inventory;
        _stats = stats;
        _trade = trade;
        _combatRng = combatRng;
        _players = players;
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
}