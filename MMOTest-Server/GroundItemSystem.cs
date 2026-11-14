using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns ground-item lifecycle: spawn on drop / NPC death, approach-to-pickup,
/// despawn sweep, per-peer visibility diffing.
///
/// Coupling: needs ItemSystem for the final give-on-pickup, TradeSystem for
/// inventory-lock gating, Pathfinder for approach paths.
/// </summary>
internal sealed class GroundItemSystem
{
    private const long GroundItemDespawnMs = 90 * 1000;

    internal sealed class GroundItem
    {
        public long Id { get; init; }
        public string ItemSlug { get; init; } = "";
        public int Quantity { get; init; }
        public int TileX { get; init; }
        public int TileY { get; init; }
        public int TileZ { get; init; }
        public long SpawnedAtMs { get; init; }
        public bool IsStatic { get; init; } = false;
    }

    private readonly ServerState _state;
    private readonly WorldService _world;
    private readonly OutboundChannel _outbound;
    private readonly ItemLookup _items;
    private readonly ItemSystem _inv;
    private readonly TradeSystem _trade;
    private readonly InventoryRepository _inventory;
    private readonly Pathfinder _pathfinder;
    private readonly int _npcViewChunkRadius;

    private readonly Dictionary<long, GroundItem> _groundItems = new();
    private readonly Dictionary<long, HashSet<long>> _visibleGroundItems = new();
    private long _nextGroundItemId = 1;

    public GroundItemSystem(ServerState state, WorldService world, OutboundChannel outbound,
                            ItemLookup items, ItemSystem inv, TradeSystem trade,
                            InventoryRepository inventory, Pathfinder pathfinder,
                            int npcViewChunkRadius)
    {
        _state = state;
        _world = world;
        _outbound = outbound;
        _items = items;
        _inv = inv;
        _trade = trade;
        _inventory = inventory;
        _pathfinder = pathfinder;
        _npcViewChunkRadius = npcViewChunkRadius;
    }

    internal int Count => _groundItems.Count;

    internal void ClearPeer(long peerId) => _visibleGroundItems.Remove(peerId);

    // ============================================================
    // Spawn / drop
    // ============================================================

    internal bool SpawnGroundItemAt(string itemSlug, int quantity, int tileX, int tileY, int tileZ)
    {
        if (quantity < 1) return false;
        if (!_items.ContainsSlug(itemSlug)) return false;

        var item = new GroundItem
        {
            Id = _nextGroundItemId++,
            ItemSlug = itemSlug,
            Quantity = quantity,
            TileX = tileX,
            TileY = tileY,
            TileZ = tileZ,
            SpawnedAtMs = (long)Time.GetTicksMsec(),
        };
        _groundItems[item.Id] = item;
        BroadcastGroundItemToNearby(item);
        return true;
    }

    internal bool DropGroundItem(long playerId, string itemSlug, int quantity)
    {
        if (quantity < 1) return false;
        if (!_items.ContainsSlug(itemSlug)) return false;
        if (!_state.Positions.TryGetValue(playerId, out var pos)) return false;

        int tx = (int)MathF.Round(pos.X);
        int ty = (int)MathF.Round(pos.Z);
        int tz = (int)MathF.Round(pos.Y);

        var item = new GroundItem
        {
            Id = _nextGroundItemId++,
            ItemSlug = itemSlug,
            Quantity = quantity,
            TileX = tx,
            TileY = ty,
            TileZ = tz,
            SpawnedAtMs = (long)Time.GetTicksMsec(),
        };
        _groundItems[item.Id] = item;
        BroadcastGroundItemToNearby(item);
        Diag.Log("diag_item", $"Dropped {itemSlug} x{quantity} (id {item.Id}) at ({tx},{ty})");
        return true;
    }

    internal bool TryDropFromSlot(long playerId, int slot, int qty)
    {
        if (_trade.IsTradeInventoryLocked(playerId)) return false;
        if (!_state.Inventories.TryGetValue(playerId, out var inv)) return false;
        if (slot < 0 || slot >= 28) return false;
        long packed = inv[slot];
        if (packed == 0) return false;
        int currentQty = ItemStack.Quantity(packed);
        if (currentQty < qty) return false;
        int itemId = ItemStack.ItemId(packed);
        if (!_items.TryGetById(itemId, out var meta)) return false;

        int newQty = currentQty - qty;
        inv[slot] = newQty > 0 ? ItemStack.Encode(itemId, newQty) : 0;
        _inventory.SaveSlotSync(playerId, slot, inv[slot]);
        _outbound.BroadcastInventory(playerId);
        return DropGroundItem(playerId, meta.Slug, qty);
    }

    // ============================================================
    // Pickup
    // ============================================================

    internal void RequestPickup(long playerId, long groundItemId)
    {
        if (_trade.IsTradeInventoryLocked(playerId)) return;
        if (!_groundItems.TryGetValue(groundItemId, out var item)) return;
        if (!_state.Positions.TryGetValue(playerId, out var pos)) return;

        int px = (int)MathF.Round(pos.X);
        int py = (int)MathF.Round(pos.Z);
        if (px == item.TileX && py == item.TileY)
        {
            ExecutePickup(playerId, item);
            return;
        }

        _state.PendingPickup[playerId] = groundItemId;
        MovePlayerToTile(playerId, item.TileX, item.TileY);
        Diag.Log("diag_item", $"Player {playerId} approaching ground item {groundItemId}");
    }

    internal void ProcessPendingPickup()
    {
        if (_state.PendingPickup.Count == 0) return;
        var toRemove = new List<long>();
        foreach (var kv in _state.PendingPickup)
        {
            long playerId = kv.Key;
            long itemId = kv.Value;
            if (!_groundItems.TryGetValue(itemId, out var item)) { toRemove.Add(playerId); continue; }
            if (!_state.Positions.TryGetValue(playerId, out var pos)) { toRemove.Add(playerId); continue; }
            int px = (int)MathF.Round(pos.X);
            int py = (int)MathF.Round(pos.Z);
            if (px == item.TileX && py == item.TileY)
            {
                ExecutePickup(playerId, item);
                toRemove.Add(playerId);
            }
        }
        foreach (var p in toRemove) _state.PendingPickup.Remove(p);
    }

    private void ExecutePickup(long playerId, GroundItem item)
    {
        if (_trade.IsTradeInventoryLocked(playerId)) return;
        if (!_groundItems.Remove(item.Id)) return;
        foreach (var kv in _visibleGroundItems)
        {
            if (kv.Value.Remove(item.Id))
                _outbound.Send(kv.Key, new { type = "ground_item_removed", id = item.Id });
        }
        Diag.Log("diag_item", $"Player {playerId} picked up {item.ItemSlug} x{item.Quantity}");
        _ = _inv.GiveItemAsync(playerId, item.ItemSlug, item.Quantity);
    }

    internal void HandlePickupGroundItem(long peerId, JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long groundItemId)) return;
        RequestPickup(session.PlayerId, groundItemId);
    }

    private void MovePlayerToTile(long playerId, int tx, int ty)
    {
        long peerId = -1;
        foreach (var kv in _state.Sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
        if (peerId < 0) return;
        if (_state.Intents.ContainsKey(peerId)) return;
        if (!_state.Positions.TryGetValue(playerId, out var pos)) return;

        int px = (int)MathF.Round(pos.X);
        int py = (int)MathF.Round(pos.Z);
        if (px == tx && py == ty) return;

        var route = _pathfinder.FindPath(px, py, tx, ty);
        if (route.Count == 0) return;
        _state.Intents[peerId] = route;
    }

    // ============================================================
    // Despawn
    // ============================================================

    internal void ProcessGroundItemDespawn()
    {
        long now = (long)Time.GetTicksMsec();
        var toRemove = new List<long>();
        foreach (var kv in _groundItems)
        {
            if (kv.Value.IsStatic) continue;
            if (now - kv.Value.SpawnedAtMs >= GroundItemDespawnMs)
                toRemove.Add(kv.Key);
        }
        foreach (var id in toRemove)
        {
            _groundItems.Remove(id);
            foreach (var vis in _visibleGroundItems)
                if (vis.Value.Remove(id))
                    _outbound.Send(vis.Key, new { type = "ground_item_removed", id = id });
        }
    }

    // ============================================================
    // Visibility
    // ============================================================

    private object GroundItemToPacket(GroundItem item)
    {
        string display = "";
        bool isCert = false;
        if (_items.TryGetIdBySlug(item.ItemSlug, out long iid) && _items.TryGetById(iid, out var meta))
        {
            display = meta.Display;
            isCert = !string.IsNullOrEmpty(meta.CertOfSlug);
        }
        return new
        {
            id = item.Id,
            slug = item.ItemSlug,
            display = display,
            is_cert = isCert,
            quantity = item.Quantity,
            x = item.TileX,
            y = item.TileZ,
            z = item.TileY,
        };
    }

    private void BroadcastGroundItemToNearby(GroundItem item)
    {
        foreach (long peerId in _state.Sessions.Keys)
        {
            if (!_state.Sessions.TryGetValue(peerId, out var sess)) continue;
            if (!_state.Positions.TryGetValue(sess.PlayerId, out var pos)) continue;
            int pcx = ActiveNpc.FloorDiv((int)MathF.Round(pos.X), _world.ChunkSize);
            int pcy = ActiveNpc.FloorDiv((int)MathF.Round(pos.Z), _world.ChunkSize);
            int icx = ActiveNpc.FloorDiv(item.TileX, _world.ChunkSize);
            int icy = ActiveNpc.FloorDiv(item.TileY, _world.ChunkSize);
            if (Math.Abs(pcx - icx) > _npcViewChunkRadius) continue;
            if (Math.Abs(pcy - icy) > _npcViewChunkRadius) continue;

            _outbound.Send(peerId, new { type = "ground_item_dropped", item = GroundItemToPacket(item) });
            if (!_visibleGroundItems.TryGetValue(peerId, out var set))
            {
                set = new HashSet<long>();
                _visibleGroundItems[peerId] = set;
            }
            set.Add(item.Id);
        }
    }

    internal void RefreshGroundItemVisibility(long peerId, int tx, int ty)
    {
        if (!_visibleGroundItems.TryGetValue(peerId, out var oldSet))
        {
            oldSet = new HashSet<long>();
            _visibleGroundItems[peerId] = oldSet;
        }
        var newSet = new HashSet<long>();
        int pcx = ActiveNpc.FloorDiv(tx, _world.ChunkSize);
        int pcy = ActiveNpc.FloorDiv(ty, _world.ChunkSize);
        foreach (var item in _groundItems.Values)
        {
            int icx = ActiveNpc.FloorDiv(item.TileX, _world.ChunkSize);
            int icy = ActiveNpc.FloorDiv(item.TileY, _world.ChunkSize);
            if (Math.Abs(pcx - icx) > _npcViewChunkRadius) continue;
            if (Math.Abs(pcy - icy) > _npcViewChunkRadius) continue;
            newSet.Add(item.Id);
        }
        foreach (var id in newSet)
        {
            if (oldSet.Contains(id)) continue;
            if (!_groundItems.TryGetValue(id, out var item)) continue;
            _outbound.Send(peerId, new { type = "ground_item_dropped", item = GroundItemToPacket(item) });
        }
        foreach (var id in oldSet)
        {
            if (newSet.Contains(id)) continue;
            _outbound.Send(peerId, new { type = "ground_item_removed", id = id });
        }
        _visibleGroundItems[peerId] = newSet;
    }
}