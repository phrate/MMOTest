using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

namespace MMOTest.Server;

public partial class Main
{
    internal sealed class BankEntry
    {
        public int ItemId { get; set; }
        public int Quantity { get; set; }
    }

    private sealed class BankSession
    {
        public string SessionId { get; init; } = "";
        public long PlayerId { get; init; }
        public long NpcId { get; init; }
        public bool CertMode { get; set; }
        public bool IsOpened { get; set; }
    }

    private sealed class BankIntent
    {
        public enum Kind { Deposit, Withdraw, Move, SetCertMode }
        public Kind Type { get; init; }
        public int ItemId { get; init; }
        public int Quantity { get; init; }       // -1 = All
        public int From { get; init; }
        public int To { get; init; }
        public bool Insert { get; init; }
        public bool CertMode { get; init; }
    }

    private readonly Dictionary<long, BankSession> _bankSessionByPlayer = new();
    private readonly Dictionary<long, long> _pendingBank = new();               // playerId -> npcId
    private readonly Dictionary<long, List<BankIntent>> _pendingBankIntents = new();

    internal bool IsPlayerInBank(long playerId) => _bankSessionByPlayer.ContainsKey(playerId);

    // ---------- Loading / saving ----------

    private void LoadBankForPlayer(long playerId)
    {
        var tuple = _db.GetBankAsync(playerId).Result;
        if (tuple == null)
        {
            _bankByPlayer[playerId] = new List<BankEntry?>();
            _bankCapacityByPlayer[playerId] = 100;
            return;
        }
        var (json, capacity) = tuple.Value;
        _bankByPlayer[playerId] = ParseBankJson(json);
        _bankCapacityByPlayer[playerId] = capacity;
    }

    private static List<BankEntry?> ParseBankJson(string json)
    {
        var result = new List<BankEntry?>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Null) { result.Add(null); continue; }
                if (el.ValueKind != JsonValueKind.Object) { result.Add(null); continue; }
                int itemId = el.TryGetProperty("item_id", out var idEl) && idEl.TryGetInt32(out var iid) ? iid : 0;
                int qty = el.TryGetProperty("quantity", out var qEl) && qEl.TryGetInt32(out var q) ? q : 0;
                if (itemId <= 0 || qty <= 0) { result.Add(null); continue; }
                result.Add(new BankEntry { ItemId = itemId, Quantity = qty });
            }
        }
        catch (JsonException e)
        {
            GD.PrintErr($"[Bank] Parse error: {e.Message}");
        }
        return result;
    }

    private static string SerializeBank(List<BankEntry?> bank)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var entry in bank)
            {
                if (entry == null || entry.ItemId == 0 || entry.Quantity <= 0)
                {
                    writer.WriteNullValue();
                    continue;
                }
                writer.WriteStartObject();
                writer.WriteNumber("item_id", entry.ItemId);
                writer.WriteNumber("quantity", entry.Quantity);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private void PersistBank(long playerId)
    {
        if (!_bankByPlayer.TryGetValue(playerId, out var bank)) return;
        // Trim trailing empty slots for storage compactness; internal state keeps them.
        int lastUsed = -1;
        for (int i = 0; i < bank.Count; i++)
            if (bank[i] != null) lastUsed = i;
        var trimmed = lastUsed < 0
            ? new List<BankEntry?>()
            : bank.GetRange(0, lastUsed + 1);
        _db.SaveBankAsync(playerId, SerializeBank(trimmed));
    }

    // ---------- Entry: request to open bank ----------

    internal void RequestBank(long playerId, long npcId)
    {
        if (!_npcs.TryGetValue(npcId, out var npc)) return;
        if (npc.IsDead) return;
        if (!npc.IsBanker) { ChatToPlayer(playerId, "They are not a banker."); return; }
        if (!_peerByPlayerId.ContainsKey(playerId) || !_positions.ContainsKey(playerId)) return;
        if (_pendingBank.TryGetValue(playerId, out var current) && current == npcId) return;

        _pendingEngage.Remove(playerId);
        _pendingPickup.Remove(playerId);
        _perTick?.EndByStem(playerId, "follow_player");
        RemovePlayerIntent(playerId);
        CancelDialogueForPlayer(playerId, "");
        CancelBankForPlayer(playerId, "");

        _pendingBank[playerId] = npcId;
        if (!ArePositionsAdjacent(playerId, npc))
            MovePlayerAdjacentToNpc(playerId, npc);
    }

    internal void CancelPendingBankFor(long playerId) => _pendingBank.Remove(playerId);

    private void ProcessPendingBank()
    {
        if (_pendingBank.Count == 0) return;

        var toRemove = new List<long>();
        var toOpen = new List<(long PlayerId, long NpcId)>();

        foreach (var kv in _pendingBank)
        {
            long playerId = kv.Key;
            long npcId = kv.Value;

            if (!_npcs.TryGetValue(npcId, out var npc) || npc.IsDead || !npc.IsBanker)
            { toRemove.Add(playerId); continue; }
            if (!_positions.ContainsKey(playerId)
                || !_peerByPlayerId.TryGetValue(playerId, out var peerId))
            { toRemove.Add(playerId); continue; }
            if (_combatByPlayer.ContainsKey(playerId)) { toRemove.Add(playerId); continue; }
            if (ArePositionsAdjacent(playerId, npc))
            {
                toRemove.Add(playerId);
                toOpen.Add((playerId, npcId));
                continue;
            }
            if (!MovePlayerAdjacentToNpc(playerId, npc))
            {
                toRemove.Add(playerId);
                ChatToPlayer(playerId, "I can't seem to reach that.");
            }
        }

        foreach (var p in toRemove) _pendingBank.Remove(p);
        foreach (var (pid, nid) in toOpen) OpenBank(pid, nid);
    }

    internal void OpenBank(long playerId, long npcId)
    {
        if (!_npcs.TryGetValue(npcId, out var npc)) return;
        if (!npc.IsBanker || npc.IsDead) return;
        if (!_bankByPlayer.ContainsKey(playerId)) return;

        CancelTradeForPlayer(playerId, ErrorKeys.TradeCancelDialogue);
        CancelTradeQueuesForPlayer(playerId, ErrorKeys.TradeCancelDialogue);
        CancelDialogueForPlayer(playerId, "");
        _perTick?.EndByStem(playerId, "follow_player");
        RemovePlayerIntent(playerId);
        _pendingPickup.Remove(playerId);
        _pendingEngage.Remove(playerId);
        if (_combatByPlayer.TryGetValue(playerId, out var pair) && !pair.StateAnnounced)
            EndCombat(pair);

        SnapAndFaceForDialogue(playerId, npc);   // reuse snap+face; quiet since no look-at queue issue

        var session = new BankSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            PlayerId = playerId,
            NpcId = npcId,
        };
        _bankSessionByPlayer[playerId] = session;
        SendBankOpen(session);
    }

    private void SendBankOpen(BankSession session)
    {
        if (!_peerByPlayerId.TryGetValue(session.PlayerId, out var peerId)) return;
        var bank = _bankByPlayer[session.PlayerId];
        int cap = _bankCapacityByPlayer[session.PlayerId];
        Send(peerId, new
        {
            type = session.IsOpened ? "bank_state" : "bank_open",
            session_id = session.SessionId,
            capacity = cap,
            cert_mode = session.CertMode,
            slots = BuildBankSlotsPayload(bank, cap),
        });
        session.IsOpened = true;
    }

    private List<object?> BuildBankSlotsPayload(List<BankEntry?> bank, int capacity)
{
    var outList = new List<object?>();
    for (int i = 0; i < capacity; i++)
    {
        var entry = i < bank.Count ? bank[i] : null;
        if (entry == null) { outList.Add(null); continue; }

        string display = "";
        string certOf = "";
        bool stackable = false;
        if (_itemsById.TryGetValue(entry.ItemId, out var meta))
        {
            display = meta.Display;
            certOf = meta.CertOfSlug;
            stackable = meta.Stackable;
        }
        outList.Add(new
        {
            item_id = entry.ItemId,
            quantity = entry.Quantity,
            display,
            cert_of_slug = certOf,
            stackable,
        });
    }
    return outList;
}

    // ---------- Intents (tick-gated) ----------

    internal void HandleBankDeposit(long peerId, JsonElement root)
        => QueueBankIntent(peerId, root, BankIntent.Kind.Deposit);
    internal void HandleBankWithdraw(long peerId, JsonElement root)
        => QueueBankIntent(peerId, root, BankIntent.Kind.Withdraw);
    internal void HandleBankMove(long peerId, JsonElement root)
        => QueueBankIntent(peerId, root, BankIntent.Kind.Move);

    private void QueueBankIntent(long peerId, JsonElement root, BankIntent.Kind kind)
    {

        var playerId = GetPlayerIdByPeer(peerId);
        if (playerId == 0) return;
        if (!_bankSessionByPlayer.TryGetValue(playerId, out var session)) return;
        if (!root.TryGetProperty("session_id", out var sidEl) || sidEl.GetString() != session.SessionId) return;

        switch (kind)
        {
            case BankIntent.Kind.Deposit:
            case BankIntent.Kind.Withdraw:
            {
                int itemId = root.TryGetProperty("item_id", out var idEl) && idEl.TryGetInt32(out var iid) ? iid : 0;
                int qty = root.TryGetProperty("quantity", out var qEl) && qEl.TryGetInt32(out var q) ? q : 1;
                if (itemId <= 0 || qty == 0) return;
                QueueBank(session.PlayerId, new BankIntent { Type = kind, ItemId = itemId, Quantity = qty });
                return;
            }
            case BankIntent.Kind.Move:
            {
                int from = root.TryGetProperty("from", out var fEl) && fEl.TryGetInt32(out var f) ? f : -1;
                int to = root.TryGetProperty("to", out var tEl) && tEl.TryGetInt32(out var t) ? t : -1;
                bool insert = root.TryGetProperty("insert", out var insEl) && insEl.ValueKind == JsonValueKind.True;
                if (from < 0 || to < 0 || from == to) return;
                QueueBank(session.PlayerId, new BankIntent { Type = kind, From = from, To = to, Insert = insert });
                return;
            }
        }
    }

    internal void HandleBankClose(long peerId, JsonElement root)
    {
        var playerId = GetPlayerIdByPeer(peerId);
        if (playerId == 0) return;
        if (!_bankSessionByPlayer.TryGetValue(playerId, out var session)) return;
        if (!root.TryGetProperty("session_id", out var sidEl) || sidEl.GetString() != session.SessionId) return;
        CloseBank(session, silent: true);
    }

    internal void HandleBankCertMode(long peerId, JsonElement root)
    {
        var playerId = GetPlayerIdByPeer(peerId);
        if (playerId == 0) return;
        if (!_bankSessionByPlayer.TryGetValue(playerId, out var session)) return;
        if (!root.TryGetProperty("session_id", out var sidEl) || sidEl.GetString() != session.SessionId) return;
        if (!root.TryGetProperty("enabled", out var enEl)) return;

        session.CertMode = enEl.ValueKind == JsonValueKind.True;
        SendBankOpen(session);   // echoes bank_state with the new cert_mode
    }

    private long GetPlayerIdByPeer(long peerId)
    {
        if (!_sessions.TryGetValue(peerId, out var sess)) return 0;
        return sess.PlayerId;
    }

    private void QueueBank(long playerId, BankIntent intent)
    {
        if (!_pendingBankIntents.TryGetValue(playerId, out var list))
        {
            list = new List<BankIntent>();
            _pendingBankIntents[playerId] = list;
        }
        list.Add(intent);
    }

    private void ProcessBankIntents()
    {
        foreach (var playerId in _pendingBankIntents.Keys.ToList())
        {
            if (!_bankSessionByPlayer.TryGetValue(playerId, out var session)) continue;
            if (!_pendingBankIntents.TryGetValue(playerId, out var intents)) continue;
            if (intents.Count == 0) { _pendingBankIntents.Remove(playerId); continue; }

            bool changed = false;
            bool noticeSent = false;

            foreach (var intent in intents)
            {
                switch (intent.Type)
                {
                    case BankIntent.Kind.Deposit:
                        changed |= ApplyDeposit(playerId, intent, out string depNotice);
                        if (!string.IsNullOrEmpty(depNotice)) { SendBankNotice(playerId, depNotice); noticeSent = true; }
                        break;
                    case BankIntent.Kind.Withdraw:
                        changed |= ApplyWithdraw(session, intent, out string wdNotice);
                        if (!string.IsNullOrEmpty(wdNotice)) { SendBankNotice(playerId, wdNotice); noticeSent = true; }
                        break;
                    case BankIntent.Kind.Move:
                        changed |= ApplyMove(playerId, intent);
                        break;
                }
            }

            _pendingBankIntents.Remove(playerId);

            if (changed)
            {
                PersistBank(playerId);
                SendBankOpen(session);
            }
        }
    }

    // ---------- Deposit / withdraw logic ----------

    private bool ApplyDeposit(long playerId, BankIntent intent, out string notice)
    {
        notice = "";
        if (!_inventoryByPlayer.TryGetValue(playerId, out var inv)) return false;
        if (!_bankByPlayer.TryGetValue(playerId, out var bank)) return false;
        if (!_itemsById.TryGetValue(intent.ItemId, out var item)) return false;
        if (item.NonBankable) { notice = ErrorKeys.BankNonBankable; return false; }

        int inInv = CountInventoryOfItem(inv, intent.ItemId);
        if (inInv <= 0) { notice = ErrorKeys.BankNothingToDeposit; return false; }
        int actual = intent.Quantity < 0 ? inInv : Math.Min(intent.Quantity, inInv);
        if (actual <= 0) return false;

        // Cert twins convert to base on deposit.
        int baseId = intent.ItemId;
        if (!string.IsNullOrEmpty(item.CertOfSlug) && _itemIdsBySlug.TryGetValue(item.CertOfSlug, out var baseItemId))
            baseId = (int)baseItemId;

        // Find existing stack of baseId in bank.
        int existingIdx = bank.FindIndex(e => e != null && e.ItemId == baseId);
        if (existingIdx >= 0)
        {
            // Merge: no slot needed.
            int newQty = bank[existingIdx]!.Quantity + actual;
            bank[existingIdx]!.Quantity = newQty;
            RemoveInventoryByItemId(playerId, inv, intent.ItemId, actual);
            return true;
        }

        // New item type: needs a free slot.
        int cap = _bankCapacityByPlayer[playerId];
        int freeIdx = bank.FindIndex(e => e == null);
        if (freeIdx < 0 && bank.Count < cap)
        {
            bank.Add(null);
            freeIdx = bank.Count - 1;
        }
        if (freeIdx < 0)
        {
            notice = ErrorKeys.BankFull;
            return false;
        }

        bank[freeIdx] = new BankEntry { ItemId = baseId, Quantity = actual };
        RemoveInventoryByItemId(playerId, inv, intent.ItemId, actual);
        return true;
    }

    private bool ApplyWithdraw(BankSession session, BankIntent intent, out string notice)
    {
        notice = "";
        long playerId = session.PlayerId;
        if (!_bankByPlayer.TryGetValue(playerId, out var bank)) return false;
        if (!_inventoryByPlayer.TryGetValue(playerId, out var inv)) return false;
        if (!_itemsById.TryGetValue(intent.ItemId, out var bankedItem)) return false;

        int existingIdx = bank.FindIndex(e => e != null && e.ItemId == intent.ItemId);
        if (existingIdx < 0) { notice = ErrorKeys.BankNothingToWithdraw; return false; }

        int inBank = bank[existingIdx]!.Quantity;
        int requested = intent.Quantity < 0 ? inBank : Math.Min(intent.Quantity, inBank);

        // Determine withdrawal output: base item, or cert item if cert mode applies.
        int outputItemId = intent.ItemId;
        string certReason = "";
        if (session.CertMode && !bankedItem.Stackable && !bankedItem.NonCertable)
        {
            if (_itemIdsBySlug.TryGetValue(bankedItem.Slug + "_cert", out var certId))
                outputItemId = (int)certId;
            // else: silently fall back to base
        }
        if (!_itemsById.TryGetValue(outputItemId, out var outItem)) return false;

        int fits = HowManyFitInInventory(inv, outputItemId, outItem.Stackable, requested);
        if (fits <= 0) { notice = ErrorKeys.BankPartialWithdraw; return false; }

        // Perform inventory insert.
        GiveItemToInventoryDirect(playerId, inv, outputItemId, outItem.Stackable, fits);

        // Reduce bank.
        int remaining = inBank - fits;
        if (remaining <= 0) bank[existingIdx] = null;
        else bank[existingIdx]!.Quantity = remaining;

        if (fits < requested) notice = ErrorKeys.BankPartialWithdraw;

        PersistInventoryFromCache(playerId, inv);
        return true;
    }

    private bool ApplyMove(long playerId, BankIntent intent)
    {
        if (!_bankByPlayer.TryGetValue(playerId, out var bank)) return false;
        int cap = _bankCapacityByPlayer[playerId];
        int from = intent.From;
        int to = intent.To;
        if (from < 0 || to < 0 || from >= cap || to >= cap) return false;

        // Grow bank list if needed (up to capacity) so both indices exist.
        while (bank.Count <= from || bank.Count <= to)
        {
            if (bank.Count >= cap) break;
            bank.Add(null);
        }
        if (from >= bank.Count || to >= bank.Count) return false;
        if (bank[from] == null && !intent.Insert) return false;
        if (bank[from] == null) return false;

        if (!intent.Insert)
        {
            (bank[from], bank[to]) = (bank[to], bank[from]);
            return true;
        }

        var item = bank[from];
        bank.RemoveAt(from);
        int target = Math.Clamp(to, 0, bank.Count);
        bank.Insert(target, item);
        return true;
    }

    // ---------- Inventory-side helpers ----------

    private static int HowManyFitInInventory(long[] inv, int itemId, bool stackable, int requested)
    {
        if (stackable)
        {
            for (int i = 0; i < inv.Length; i++)
                if (inv[i] != 0 && ItemStack.ItemId(inv[i]) == itemId) return requested; // merges
            for (int i = 0; i < inv.Length; i++)
                if (inv[i] == 0) return requested; // one slot available
            return 0;
        }
        int free = 0;
        for (int i = 0; i < inv.Length; i++)
            if (inv[i] == 0) free++;
        return Math.Min(requested, free);
    }

    private void GiveItemToInventoryDirect(long playerId, long[] inv, int itemId, bool stackable, int qty)
    {
        int remaining = qty;
        if (stackable)
        {
            for (int i = 0; i < inv.Length && remaining > 0; i++)
            {
                if (inv[i] == 0) continue;
                if (ItemStack.ItemId(inv[i]) != itemId) continue;
                inv[i] = ItemStack.Encode(itemId, ItemStack.Quantity(inv[i]) + remaining);
                remaining = 0;
            }
            for (int i = 0; i < inv.Length && remaining > 0; i++)
            {
                if (inv[i] != 0) continue;
                inv[i] = ItemStack.Encode(itemId, remaining);
                remaining = 0;
            }
        }
        else
        {
            for (int i = 0; i < inv.Length && remaining > 0; i++)
            {
                if (inv[i] != 0) continue;
                inv[i] = ItemStack.Encode(itemId, 1);
                remaining--;
            }
        }
    }

    private void RemoveInventoryByItemId(long playerId, long[] inv, int itemId, int qty)
    {
        int remaining = qty;
        for (int i = 0; i < inv.Length && remaining > 0; i++)
        {
            if (inv[i] == 0) continue;
            if (ItemStack.ItemId(inv[i]) != itemId) continue;
            int have = ItemStack.Quantity(inv[i]);
            int take = Math.Min(have, remaining);
            int left = have - take;
            inv[i] = left > 0 ? ItemStack.Encode(itemId, left) : 0;
            remaining -= take;
        }
        PersistInventoryFromCache(playerId, inv);
    }

    private void PersistInventoryFromCache(long playerId, long[] inv)
    {
        for (int i = 0; i < inv.Length; i++)
            _db.SaveInventorySlotSync(playerId, i, inv[i]);
        BroadcastInventory(playerId);
    }

    // ---------- Cancel / notice / close ----------

    internal void CancelBankForPlayer(long playerId, string errorKey)
    {
        if (!_bankSessionByPlayer.TryGetValue(playerId, out var session)) return;
        CloseBank(session, silent: string.IsNullOrEmpty(errorKey), errorKey);
    }

    private void CloseBank(BankSession session, bool silent, string errorKey = "")
    {
        if (!_bankSessionByPlayer.TryGetValue(session.PlayerId, out var cur) || cur.SessionId != session.SessionId) return;
        _bankSessionByPlayer.Remove(session.PlayerId);
        _pendingBankIntents.Remove(session.PlayerId);
        PersistBank(session.PlayerId);
        if (!_peerByPlayerId.TryGetValue(session.PlayerId, out var peerId)) return;
        Send(peerId, new
        {
            type = "bank_close",
            session_id = session.SessionId,
            error_key = silent ? "" : errorKey,
            error_params = new { },
        });
    }

    private void SendBankNotice(long playerId, string errorKey)
    {
        if (!_peerByPlayerId.TryGetValue(playerId, out var peerId)) return;
        Send(peerId, new { type = "bank_notice", error_key = errorKey, error_params = new { } });
    }
}