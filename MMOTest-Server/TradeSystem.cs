using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns player-to-player trade: invites, offers, credits, two-stage confirmation,
/// atomic commit via InventoryRepository.CommitTradeSync.
///
/// Coupling note (temporary): holds Main for Send / ChatToPlayer / BroadcastInventory
/// and the cross-cutting cancels for dialogue and bank. Phase 4 introduces
/// Coordinator + OutboundChannel which replace these — swapping `_main` for
/// `_coordinator` and `_outbound` will be a call-site rewrite, not a design change.
/// </summary>
internal sealed class TradeSystem
{
    private const long TradeInviteTimeoutTicks = 50;
    private const long TradeSessionTimeoutTicks = 1200;
    private const long TradeRequestCooldownTicks = 10;

    private enum TradePhase { Offering, Confirming }

    private sealed class PendingTradeInvite
    {
        public long RequesterId { get; init; }
        public string InviteId { get; init; } = "";
        public long ExpiresAtTick { get; init; }
    }

    private sealed class TradeSession
    {
        public string Id { get; init; } = "";
        public long PlayerAId { get; init; }
        public long PlayerBId { get; init; }
        public long StartedTick { get; set; }
        public List<(int ItemId, int Quantity)> OfferA { get; } = new();
        public List<(int ItemId, int Quantity)> OfferB { get; } = new();
        public int CreditsA { get; set; }
        public int CreditsB { get; set; }
        public bool AcceptedA { get; set; }
        public bool AcceptedB { get; set; }
        public bool ConfirmedA { get; set; }
        public bool ConfirmedB { get; set; }
        public TradePhase Phase { get; set; }
    }

    private sealed class TradeIntent
    {
        public enum Kind { AddOffer, WithdrawOffer, SetCredits }
        public Kind Type { get; init; }
        public int ItemId { get; init; }
        public int Quantity { get; init; }
    }

    private readonly Main _main;
    private readonly ServerState _state;
    private readonly ItemLookup _items;
    private readonly InventoryRepository _inventory;
    private readonly Gfs.PerTickManager _perTick;
    private readonly OutboundChannel _outbound;
    private readonly Coordinator _coordinator;

    private readonly Dictionary<long, PendingTradeInvite> _pendingTradeInvites = new();
    private readonly Dictionary<long, TradeSession> _tradeByPlayer = new();
    private readonly Dictionary<long, long> _lastTradeRequestTick = new();
    private readonly Dictionary<long, List<TradeIntent>> _pendingTradeIntents = new();

    public TradeSystem(Main main, ServerState state, ItemLookup items,
                       InventoryRepository inventory, Gfs.PerTickManager perTick,
                       OutboundChannel outbound, Coordinator coordinator)
    {
        _main = main;
        _state = state;
        _items = items;
        _inventory = inventory;
        _perTick = perTick;
        _outbound = outbound;
        _coordinator = coordinator;
    }

    internal bool IsTradeInventoryLocked(long playerId) => _tradeByPlayer.ContainsKey(playerId);

    internal void ClearPendingFor(long playerId) => _pendingTradeIntents.Remove(playerId);

    // ============================================================
    // Entry point (from GFS trade_player.gfs)
    // ============================================================

    internal void RequestTrade(long requesterId, long targetPlayerId)
    {
        if (requesterId == targetPlayerId)
        {
            SendTradeError(requesterId, ErrorKeys.TradeSelf);
            return;
        }
        _coordinator.OnTradeInitiated(requesterId);
        if (_state.DialogueByPlayer.ContainsKey(targetPlayerId))
        {
            SendTradeError(requesterId, ErrorKeys.TradeBusy);
            return;
        }
        if (!IsTradePlayerAvailable(requesterId) || !IsTradePlayerAvailable(targetPlayerId))
        {
            SendTradeError(requesterId, ErrorKeys.TradeUnavailable);
            return;
        }
        if (_tradeByPlayer.ContainsKey(requesterId) || HasPendingTradeInvite(requesterId))
        {
            SendTradeError(requesterId, ErrorKeys.TradeAlreadyActive);
            return;
        }
        if (_tradeByPlayer.ContainsKey(targetPlayerId) || HasPendingTradeInvite(targetPlayerId))
        {
            SendTradeError(requesterId, ErrorKeys.TradeBusy);
            return;
        }
        if (IsInCombatOrPursuit(requesterId) || IsInCombatOrPursuit(targetPlayerId))
        {
            SendTradeError(requesterId, ErrorKeys.TradeInCombat);
            return;
        }
        if (_lastTradeRequestTick.TryGetValue(requesterId, out long lastRequest)
            && _state.CurrentTick - lastRequest < TradeRequestCooldownTicks)
        {
            SendTradeError(requesterId, ErrorKeys.TradeCooldown);
            return;
        }
        _lastTradeRequestTick[requesterId] = _state.CurrentTick;
        SendTradeInvite(requesterId, targetPlayerId);
    }

    private bool IsTradePlayerAvailable(long playerId)
        => _state.PeerByPlayerId.ContainsKey(playerId)
            && _state.Positions.ContainsKey(playerId)
            && _state.Inventories.ContainsKey(playerId)
            && _state.Credits.ContainsKey(playerId);

    private bool HasPendingTradeInvite(long playerId)
        => _pendingTradeInvites.ContainsKey(playerId)
            || _pendingTradeInvites.Values.Any(invite => invite.RequesterId == playerId);

    private bool IsInCombatOrPursuit(long playerId)
        => _state.CombatByPlayer.ContainsKey(playerId)
            || _state.PendingEngage.ContainsKey(playerId)
            || (_state.PlayerCombat.TryGetValue(playerId, out var combat) && combat.CurrentHp <= 0);

    private bool TryGetPlayerSession(long playerId, out PlayerSession session)
    {
        foreach (var entry in _state.Sessions.Values)
        {
            if (entry.PlayerId != playerId) continue;
            session = entry;
            return true;
        }
        session = null!;
        return false;
    }

    // ============================================================
    // Per-tick sweep
    // ============================================================

    internal void ProcessTradeQueues()
    {
        foreach (var entry in _pendingTradeInvites.ToList())
        {
            long targetId = entry.Key;
            var invite = entry.Value;
            if (_state.CurrentTick >= invite.ExpiresAtTick
                || !IsTradePlayerAvailable(invite.RequesterId)
                || !IsTradePlayerAvailable(targetId)
                || IsInCombatOrPursuit(invite.RequesterId)
                || IsInCombatOrPursuit(targetId)
                || _tradeByPlayer.ContainsKey(invite.RequesterId)
                || _tradeByPlayer.ContainsKey(targetId))
            {
                CloseTradeInvite(targetId, invite, ErrorKeys.TradeInviteExpired);
            }
        }

        foreach (TradeSession trade in _tradeByPlayer.Values.Distinct().ToList())
        {
            if (_state.CurrentTick - trade.StartedTick >= TradeSessionTimeoutTicks)
                CancelTrade(trade, ErrorKeys.TradeTimeout);
            else if (IsInCombatOrPursuit(trade.PlayerAId) || IsInCombatOrPursuit(trade.PlayerBId))
                CancelTrade(trade, ErrorKeys.TradeCancelCombat);
        }
    }

    internal void ProcessTradeIntents()
    {
        foreach (var playerId in _pendingTradeIntents.Keys.ToList())
            FlushPendingIntentsFor(playerId);
    }

    private void FlushPendingIntentsFor(long playerId)
    {
        if (!_pendingTradeIntents.TryGetValue(playerId, out var intents) || intents.Count == 0)
        {
            _pendingTradeIntents.Remove(playerId);
            return;
        }
        if (!_tradeByPlayer.TryGetValue(playerId, out var trade))
        {
            _pendingTradeIntents.Remove(playerId);
            return;
        }
        if (trade.Phase != TradePhase.Offering)
        {
            _pendingTradeIntents.Remove(playerId);
            return;
        }

        bool changed = ApplyIntents(trade, playerId, intents);
        _pendingTradeIntents.Remove(playerId);

        if (changed)
        {
            ResetTradeAcceptance(trade);
            trade.StartedTick = _state.CurrentTick;
            SendTradeState(trade);
        }
    }

    private bool ApplyIntents(TradeSession trade, long playerId, List<TradeIntent> intents)
    {
        bool isPlayerA = playerId == trade.PlayerAId;
        var ownOffer = isPlayerA ? trade.OfferA : trade.OfferB;
        if (!_state.Inventories.TryGetValue(playerId, out var inventory)) return false;

        bool changed = false;
        foreach (var intent in intents)
        {
            switch (intent.Type)
            {
                case TradeIntent.Kind.AddOffer:
                    if (TryApplyAdd(ownOffer, inventory, intent.ItemId, intent.Quantity))
                        changed = true;
                    break;
                case TradeIntent.Kind.WithdrawOffer:
                    if (TryApplyWithdraw(ownOffer, intent.ItemId, intent.Quantity))
                        changed = true;
                    break;
                case TradeIntent.Kind.SetCredits:
                    int max = _state.Credits[playerId];
                    int clamped = Math.Clamp(intent.Quantity, 0, max);
                    int current = isPlayerA ? trade.CreditsA : trade.CreditsB;
                    if (current != clamped)
                    {
                        if (isPlayerA) trade.CreditsA = clamped;
                        else trade.CreditsB = clamped;
                        changed = true;
                    }
                    break;
            }
        }
        return changed;
    }

    private bool TryApplyAdd(List<(int ItemId, int Quantity)> offer, long[] inventory, int itemId, int requested)
    {
        if (!_items.TryGetById(itemId, out var item) || !item.CanTrade) return false;
        int available = CountInventoryOfItem(inventory, itemId);
        int alreadyOffered = offer.Where(o => o.ItemId == itemId).Sum(o => o.Quantity);
        int room = available - alreadyOffered;
        int actual = requested < 0 ? room : Math.Min(requested, room);
        if (actual <= 0) return false;
        AddToOffer(offer, itemId, actual);
        return true;
    }

    private bool TryApplyWithdraw(List<(int ItemId, int Quantity)> offer, int itemId, int requested)
    {
        int alreadyOffered = offer.Where(o => o.ItemId == itemId).Sum(o => o.Quantity);
        int actual = requested < 0 ? alreadyOffered : Math.Min(requested, alreadyOffered);
        if (actual <= 0) return false;
        RemoveFromOffer(offer, itemId, actual);
        return true;
    }

    private void QueueIntent(long playerId, TradeIntent intent)
    {
        if (!_pendingTradeIntents.TryGetValue(playerId, out var list))
        {
            list = new List<TradeIntent>();
            _pendingTradeIntents[playerId] = list;
        }
        list.Add(intent);
    }

    // ============================================================
    // Invites
    // ============================================================

    private void SendTradeInvite(long requesterId, long targetPlayerId)
    {
        if (!TryGetPlayerSession(requesterId, out var requester)
            || !TryGetPlayerSession(targetPlayerId, out var target))
            return;

        string inviteId = Guid.NewGuid().ToString("N");
        var invite = new PendingTradeInvite
        {
            RequesterId = requesterId,
            InviteId = inviteId,
            ExpiresAtTick = _state.CurrentTick + TradeInviteTimeoutTicks,
        };
        _pendingTradeInvites[targetPlayerId] = invite;

        SendToTradePlayer(targetPlayerId, new
        {
            type = "trade_request_incoming",
            invite_id = inviteId,
            from_player_id = requesterId,
            from_username = requester.Username,
        });
        _outbound.ChatToPlayer(requesterId, $"Trade request sent to {target.Username}.");
    }

    internal void HandleTradeInviteResponse(long peerId, JsonElement root, bool accept)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var recipient)) return;
        if (!root.TryGetProperty("invite_id", out var inviteElement)
            || inviteElement.ValueKind != JsonValueKind.String) return;
        string inviteId = inviteElement.GetString() ?? "";
        if (inviteId.Length == 0) return;

        if (!_pendingTradeInvites.TryGetValue(recipient.PlayerId, out var invite)
            || invite.InviteId != inviteId)
        {
            SendToTradePlayer(recipient.PlayerId, new
            {
                type = "trade_request_cancelled",
                invite_id = inviteId,
                error_key = ErrorKeys.TradeInviteExpired,
                error_params = new { },
            });
            return;
        }
        if (_state.CurrentTick >= invite.ExpiresAtTick)
        {
            CloseTradeInvite(recipient.PlayerId, invite, ErrorKeys.TradeInviteExpired);
            return;
        }

        _pendingTradeInvites.Remove(recipient.PlayerId);
        if (!accept)
        {
            SendToTradePlayer(invite.RequesterId, new
            {
                type = "trade_request_cancelled",
                invite_id = invite.InviteId,
                error_key = ErrorKeys.TradeCancelDeclined,
                error_params = new { },
            });
            _outbound.ChatToPlayer(invite.RequesterId, $"{recipient.Username} declined the trade request.");
            return;
        }

        if (!IsTradePlayerAvailable(invite.RequesterId)
            || IsInCombatOrPursuit(invite.RequesterId)
            || IsInCombatOrPursuit(recipient.PlayerId)
            || _tradeByPlayer.ContainsKey(invite.RequesterId)
            || _tradeByPlayer.ContainsKey(recipient.PlayerId))
        {
            SendTradeError(recipient.PlayerId, ErrorKeys.TradeUnavailable);
            SendTradeError(invite.RequesterId, ErrorKeys.TradeUnavailable);
            return;
        }

        if (!TryGetPlayerSession(invite.RequesterId, out _)) return;
        _perTick?.EndByStem(invite.RequesterId, "follow_player");
        _perTick?.EndByStem(recipient.PlayerId, "follow_player");
        if (_state.PeerByPlayerId.TryGetValue(invite.RequesterId, out long reqPeer))
            _state.Intents.Remove(reqPeer);
        if (_state.PeerByPlayerId.TryGetValue(recipient.PlayerId, out long recPeer))
            _state.Intents.Remove(recPeer);

        _coordinator.OnTradeAccepted(invite.RequesterId, recipient.PlayerId);

        var trade = new TradeSession
        {
            Id = Guid.NewGuid().ToString("N"),
            PlayerAId = invite.RequesterId,
            PlayerBId = recipient.PlayerId,
            StartedTick = _state.CurrentTick,
        };
        _tradeByPlayer[trade.PlayerAId] = trade;
        _tradeByPlayer[trade.PlayerBId] = trade;
        _state.PendingPickup.Remove(trade.PlayerAId);
        _state.PendingPickup.Remove(trade.PlayerBId);
        SendTradeState(trade);
    }

    // ============================================================
    // Offer / withdraw / credits (queue only — applied on next tick)
    // ============================================================

    internal void HandleTradeOfferAdd(long peerId, JsonElement root)
        => QueueTradeOfferChange(peerId, root, withdraw: false);

    internal void HandleTradeOfferWithdraw(long peerId, JsonElement root)
        => QueueTradeOfferChange(peerId, root, withdraw: true);

    private void QueueTradeOfferChange(long peerId, JsonElement root, bool withdraw)
    {
        if (!TryGetTrade(peerId, root, out var trade, out long playerId, out _)) return;
        if (trade.Phase != TradePhase.Offering)
        {
            SendTradeError(playerId, ErrorKeys.TradeOfferLocked);
            return;
        }
        if (!root.TryGetProperty("item_id", out var itemIdEl) || !itemIdEl.TryGetInt32(out int itemId) || itemId <= 0
            || !root.TryGetProperty("requested_quantity", out var qtyEl) || !qtyEl.TryGetInt32(out int requested))
        {
            SendTradeError(playerId, ErrorKeys.TradeOfferInvalid);
            return;
        }
        if (!_items.TryGetById(itemId, out var item) || !item.CanTrade)
        {
            SendTradeError(playerId, ErrorKeys.TradeOfferInvalid);
            return;
        }

        QueueIntent(playerId, new TradeIntent
        {
            Type = withdraw ? TradeIntent.Kind.WithdrawOffer : TradeIntent.Kind.AddOffer,
            ItemId = itemId,
            Quantity = requested,
        });
    }

    internal void HandleTradeCreditsSet(long peerId, JsonElement root)
    {
        if (!TryGetTrade(peerId, root, out var trade, out long playerId, out _)) return;
        if (trade.Phase != TradePhase.Offering)
        {
            SendTradeError(playerId, ErrorKeys.TradeOfferLocked);
            return;
        }
        if (!root.TryGetProperty("amount", out var amountElement)
            || !amountElement.TryGetInt32(out int amount) || amount < 0)
        {
            SendTradeError(playerId, ErrorKeys.TradeCreditInvalid);
            return;
        }

        QueueIntent(playerId, new TradeIntent
        {
            Type = TradeIntent.Kind.SetCredits,
            Quantity = amount,
        });
    }

    // ============================================================
    // Accept / confirm / cancel
    // ============================================================

    internal void HandleTradeAcceptOffer(long peerId, JsonElement root)
    {
        if (!TryGetTrade(peerId, root, out var trade, out long playerId, out bool isPlayerA)) return;
        if (trade.Phase != TradePhase.Offering) return;

        if (_pendingTradeIntents.TryGetValue(playerId, out var intents) && intents.Count > 0)
        {
            bool changed = ApplyIntents(trade, playerId, intents);
            _pendingTradeIntents.Remove(playerId);
            if (changed) ResetTradeAcceptance(trade);
        }

        if (!TryBuildTradeInventories(trade, out _, out _, out string errorKey))
        {
            SendTradeError(trade.PlayerAId, errorKey);
            SendTradeError(trade.PlayerBId, errorKey);
            CancelTrade(trade, errorKey);
            return;
        }

        if (isPlayerA) trade.AcceptedA = true;
        else trade.AcceptedB = true;
        if (trade.AcceptedA && trade.AcceptedB) trade.Phase = TradePhase.Confirming;
        trade.StartedTick = _state.CurrentTick;
        SendTradeState(trade);
    }

    internal void HandleTradeConfirm(long peerId, JsonElement root)
    {
        if (!TryGetTrade(peerId, root, out var trade, out _, out bool isPlayerA)) return;
        if (trade.Phase != TradePhase.Confirming) return;
        if (isPlayerA) trade.ConfirmedA = true;
        else trade.ConfirmedB = true;
        SendTradeState(trade);
        if (trade.ConfirmedA && trade.ConfirmedB) CommitTrade(trade);
    }

    internal void HandleTradeCancel(long peerId, JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var player)) return;
        if (_tradeByPlayer.TryGetValue(player.PlayerId, out var trade)
            && root.TryGetProperty("session_id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String
            && idElement.GetString() == trade.Id)
        {
            CancelTrade(trade, ErrorKeys.TradeCancelByPlayer);
            return;
        }
        CancelTradeQueuesForPlayer(player.PlayerId, ErrorKeys.TradeCancelByPlayer);
    }

    // ============================================================
    // Cancel plumbing (called by Main and other systems)
    // ============================================================

    internal void CancelTradeQueuesForPlayer(long playerId, string errorKey)
    {
        if (_pendingTradeInvites.Remove(playerId))
            SendTradeError(playerId, errorKey);

        foreach (var entry in _pendingTradeInvites.Where(p => p.Value.RequesterId == playerId).ToList())
        {
            _pendingTradeInvites.Remove(entry.Key);
            var message = new
            {
                type = "trade_request_cancelled",
                invite_id = entry.Value.InviteId,
                error_key = errorKey,
                error_params = new { },
            };
            SendToTradePlayer(entry.Key, message);
            SendToTradePlayer(playerId, message);
        }
    }

    internal void CancelTradeForPlayer(long playerId, string errorKey)
    {
        if (_tradeByPlayer.TryGetValue(playerId, out var trade))
            CancelTrade(trade, errorKey);
    }

    // ============================================================
    // Commit
    // ============================================================

    private void CommitTrade(TradeSession trade)
    {
        if (IsInCombatOrPursuit(trade.PlayerAId) || IsInCombatOrPursuit(trade.PlayerBId))
        {
            CancelTrade(trade, ErrorKeys.TradeCancelCombat);
            return;
        }
        if (!TryBuildTradeInventories(trade, out var inventoryA, out var inventoryB, out string errorKey))
        {
            SendTradeError(trade.PlayerAId, errorKey);
            SendTradeError(trade.PlayerBId, errorKey);
            CancelTrade(trade, errorKey);
            return;
        }

        int balanceA = _state.Credits[trade.PlayerAId];
        int balanceB = _state.Credits[trade.PlayerBId];
        int resultCreditsA = checked(balanceA - trade.CreditsA + trade.CreditsB);
        int resultCreditsB = checked(balanceB - trade.CreditsB + trade.CreditsA);
        try
        {
            if (!_inventory.CommitTradeSync(trade.PlayerAId, inventoryA, resultCreditsA,
                                            trade.PlayerBId, inventoryB, resultCreditsB))
            {
                CancelTrade(trade, ErrorKeys.TradeCommitFailed);
                return;
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Server] Trade {trade.Id} commit failed: {e.Message}");
            CancelTrade(trade, ErrorKeys.TradeCommitFailed);
            return;
        }

        _state.Inventories[trade.PlayerAId] = inventoryA;
        _state.Inventories[trade.PlayerBId] = inventoryB;
        _state.Credits[trade.PlayerAId] = resultCreditsA;
        _state.Credits[trade.PlayerBId] = resultCreditsB;
        _tradeByPlayer.Remove(trade.PlayerAId);
        _tradeByPlayer.Remove(trade.PlayerBId);
        _outbound.BroadcastInventory(trade.PlayerAId);
        _outbound.BroadcastInventory(trade.PlayerBId);
        SendCreditsToPlayer(trade.PlayerAId, resultCreditsA);
        SendCreditsToPlayer(trade.PlayerBId, resultCreditsB);
        SendToTradePlayer(trade.PlayerAId, new { type = "trade_completed", session_id = trade.Id });
        SendToTradePlayer(trade.PlayerBId, new { type = "trade_completed", session_id = trade.Id });
        Diag.Log("diag_item", $"Trade {trade.Id} completed between {trade.PlayerAId} and {trade.PlayerBId}");
    }

    private bool TryBuildTradeInventories(TradeSession trade, out long[] inventoryA, out long[] inventoryB, out string errorKey)
    {
        inventoryA = null!;
        inventoryB = null!;
        errorKey = ErrorKeys.TradeCommitFailed;
        if (!_state.Inventories.TryGetValue(trade.PlayerAId, out var currentA)
            || !_state.Inventories.TryGetValue(trade.PlayerBId, out var currentB)
            || !_state.Credits.TryGetValue(trade.PlayerAId, out int balanceA)
            || !_state.Credits.TryGetValue(trade.PlayerBId, out int balanceB)
            || trade.CreditsA > balanceA || trade.CreditsB > balanceB) return false;

        inventoryA = (long[])currentA.Clone();
        inventoryB = (long[])currentB.Clone();
        if (!RemoveOfferFromInventory(trade.OfferA, inventoryA)
            || !RemoveOfferFromInventory(trade.OfferB, inventoryB)) return false;
        if (!AddOfferToInventory(trade.OfferB, inventoryA)
            || !AddOfferToInventory(trade.OfferA, inventoryB))
        {
            errorKey = ErrorKeys.TradeInventoryFull;
            return false;
        }

        long creditsA = (long)balanceA - trade.CreditsA + trade.CreditsB;
        long creditsB = (long)balanceB - trade.CreditsB + trade.CreditsA;
        if (creditsA > int.MaxValue || creditsB > int.MaxValue)
        {
            errorKey = ErrorKeys.TradeCreditOverflow;
            return false;
        }
        return true;
    }

    // ============================================================
    // Inventory math
    // ============================================================

    private static int CountInventoryOfItem(long[] inventory, int itemId)
    {
        int total = 0;
        foreach (long packed in inventory)
        {
            if (packed == 0) continue;
            if (ItemStack.ItemId(packed) == itemId) total += ItemStack.Quantity(packed);
        }
        return total;
    }

    private static void AddToOffer(List<(int ItemId, int Quantity)> offer, int itemId, int qty)
    {
        for (int i = 0; i < offer.Count; i++)
        {
            if (offer[i].ItemId == itemId)
            {
                offer[i] = (itemId, offer[i].Quantity + qty);
                return;
            }
        }
        offer.Add((itemId, qty));
    }

    private static void RemoveFromOffer(List<(int ItemId, int Quantity)> offer, int itemId, int qty)
    {
        for (int i = 0; i < offer.Count; i++)
        {
            if (offer[i].ItemId != itemId) continue;
            int remaining = offer[i].Quantity - qty;
            if (remaining <= 0) offer.RemoveAt(i);
            else offer[i] = (itemId, remaining);
            return;
        }
    }

    private bool RemoveOfferFromInventory(List<(int ItemId, int Quantity)> offer, long[] inventory)
    {
        foreach (var (itemId, quantity) in offer)
        {
            int remaining = quantity;
            for (int slot = 0; slot < inventory.Length && remaining > 0; slot++)
            {
                long packed = inventory[slot];
                if (packed == 0) continue;
                if (ItemStack.ItemId(packed) != itemId) continue;
                int slotQty = ItemStack.Quantity(packed);
                int take = Math.Min(slotQty, remaining);
                int newQty = slotQty - take;
                inventory[slot] = newQty > 0 ? ItemStack.Encode(itemId, newQty) : 0;
                remaining -= take;
            }
            if (remaining > 0) return false;
        }
        return true;
    }

    private bool AddOfferToInventory(List<(int ItemId, int Quantity)> offer, long[] inventory)
    {
        foreach (var (itemId, quantity) in offer)
        {
            if (!_items.TryGetById(itemId, out var item)) return false;
            int remaining = quantity;

            if (item.Stackable)
            {
                for (int slot = 0; slot < inventory.Length && remaining > 0; slot++)
                {
                    long packed = inventory[slot];
                    if (packed == 0) continue;
                    if (ItemStack.ItemId(packed) != itemId) continue;
                    long total = (long)ItemStack.Quantity(packed) + remaining;
                    if (total > int.MaxValue) return false;
                    inventory[slot] = ItemStack.Encode(itemId, (int)total);
                    remaining = 0;
                }
                if (remaining > 0)
                {
                    for (int slot = 0; slot < inventory.Length && remaining > 0; slot++)
                    {
                        if (inventory[slot] != 0) continue;
                        inventory[slot] = ItemStack.Encode(itemId, remaining);
                        remaining = 0;
                    }
                }
            }
            else
            {
                while (remaining > 0)
                {
                    int empty = Array.FindIndex(inventory, p => p == 0);
                    if (empty < 0) return false;
                    inventory[empty] = ItemStack.Encode(itemId, 1);
                    remaining--;
                }
            }
            if (remaining > 0) return false;
        }
        return true;
    }

    // ============================================================
    // State broadcast
    // ============================================================

    private void ResetTradeAcceptance(TradeSession trade)
    {
        trade.AcceptedA = false;
        trade.AcceptedB = false;
        trade.ConfirmedA = false;
        trade.ConfirmedB = false;
    }

    private bool TryGetTrade(long peerId, JsonElement root, out TradeSession trade, out long playerId, out bool isPlayerA)
    {
        trade = null!;
        playerId = 0;
        isPlayerA = false;
        if (!_state.Sessions.TryGetValue(peerId, out var player)
            || !_tradeByPlayer.TryGetValue(player.PlayerId, out trade)
            || !root.TryGetProperty("session_id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || idElement.GetString() != trade.Id)
            return false;
        playerId = player.PlayerId;
        isPlayerA = playerId == trade.PlayerAId;
        return true;
    }

    private void SendTradeState(TradeSession trade)
    {
        SendTradeStateToPlayer(trade, trade.PlayerAId, true);
        SendTradeStateToPlayer(trade, trade.PlayerBId, false);
    }

    private void SendTradeStateToPlayer(TradeSession trade, long playerId, bool isPlayerA)
    {
        long partnerId = isPlayerA ? trade.PlayerBId : trade.PlayerAId;
        if (!TryGetPlayerSession(partnerId, out var partner)) return;

        int ownCredits = isPlayerA ? trade.CreditsA : trade.CreditsB;
        int partnerCredits = isPlayerA ? trade.CreditsB : trade.CreditsA;
        var ownOffer = isPlayerA ? trade.OfferA : trade.OfferB;
        var partnerOffer = isPlayerA ? trade.OfferB : trade.OfferA;

        SendToTradePlayer(playerId, new
        {
            type = "trade_state",
            session_id = trade.Id,
            partner_id = partnerId,
            partner_username = partner.Username,
            phase = trade.Phase == TradePhase.Offering ? "offering" : "confirming",
            my_offer = BuildTradeOffer(ownOffer),
            their_offer = BuildTradeOffer(partnerOffer),
            my_credits = ownCredits,
            their_credits = partnerCredits,
            my_accepted = isPlayerA ? trade.AcceptedA : trade.AcceptedB,
            their_accepted = isPlayerA ? trade.AcceptedB : trade.AcceptedA,
            my_confirmed = isPlayerA ? trade.ConfirmedA : trade.ConfirmedB,
            their_confirmed = isPlayerA ? trade.ConfirmedB : trade.ConfirmedA,
        });
    }

    private List<object> BuildTradeOffer(List<(int ItemId, int Quantity)> offer)
    {
        var result = new List<object>();
        foreach (var (itemId, quantity) in offer)
        {
            if (quantity <= 0) continue;
            if (!_items.TryGetById(itemId, out var item)) continue;
            result.Add(new
            {
                item_id = itemId,
                slug = item.Slug,
                display = item.Display,
                quantity,
                stackable = item.Stackable,
                cert_of_slug = item.CertOfSlug ?? "",
            });
        }
        return result;
    }

    // ============================================================
    // Low-level helpers
    // ============================================================

    private void SendToTradePlayer(long playerId, object message)
    {
        if (_state.PeerByPlayerId.TryGetValue(playerId, out long peerId)) _outbound.Send(peerId, message);
    }

    private void SendTradeError(long playerId, string errorKey, object? errorParams = null)
        => SendToTradePlayer(playerId, new { type = "trade_error", error_key = errorKey, error_params = errorParams ?? new { } });

    private void CloseTradeInvite(long targetId, PendingTradeInvite invite, string errorKey)
    {
        if (!_pendingTradeInvites.TryGetValue(targetId, out var current) || current.InviteId != invite.InviteId) return;
        _pendingTradeInvites.Remove(targetId);
        var message = new
        {
            type = "trade_request_cancelled",
            invite_id = invite.InviteId,
            error_key = errorKey,
            error_params = new { },
        };
        SendToTradePlayer(targetId, message);
        SendToTradePlayer(invite.RequesterId, message);
    }

    private void CancelTrade(TradeSession trade, string errorKey)
    {
        if (!_tradeByPlayer.TryGetValue(trade.PlayerAId, out var current) || current.Id != trade.Id) return;
        _tradeByPlayer.Remove(trade.PlayerAId);
        _tradeByPlayer.Remove(trade.PlayerBId);
        _pendingTradeIntents.Remove(trade.PlayerAId);
        _pendingTradeIntents.Remove(trade.PlayerBId);
        var message = new
        {
            type = "trade_cancelled",
            session_id = trade.Id,
            error_key = errorKey,
            error_params = new { },
        };
        SendToTradePlayer(trade.PlayerAId, message);
        SendToTradePlayer(trade.PlayerBId, message);
    }

    private void SendCreditsToPlayer(long playerId, int credits)
    {
        if (_state.PeerByPlayerId.TryGetValue(playerId, out long peerId))
            _outbound.Send(peerId, new { type = "credits_updated", credits });
    }
}