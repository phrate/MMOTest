using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns player bank: access via NPCs, deposit/withdraw/move/close intents,
/// JSON blob persistence, cert-mode toggle.
///
/// Coupling note (temporary): holds Main for Send / ChatToPlayer / cross-cutting
/// cancels and world helpers. Phase 4's Coordinator + OutboundChannel absorb these.
/// </summary>
internal sealed class BankSystem
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
        public int Quantity { get; init; }
        public int From { get; init; }
        public int To { get; init; }
        public bool Insert { get; init; }
        public bool CertMode { get; init; }
    }

    private readonly Main _main;
    private readonly ServerState _state;
    private readonly ItemLookup _items;
    private readonly BankRepository _bankRepo;
    private readonly InventoryRepository _inventory;
    private readonly Gfs.PerTickManager _perTick;
    private readonly OutboundChannel _outbound;
    private readonly Coordinator _coordinator;

    private readonly Dictionary<long, List<BankEntry?>> _bankByPlayer = new();
    private readonly Dictionary<long, int> _bankCapacityByPlayer = new();
    private readonly Dictionary<long, BankSession> _bankSessionByPlayer = new();
    private readonly Dictionary<long, long> _pendingBank = new();
    private readonly Dictionary<long, List<BankIntent>> _pendingBankIntents = new();

    public BankSystem(Main main, ServerState state, ItemLookup items,
                      BankRepository bankRepo, InventoryRepository inventory,
                      Gfs.PerTickManager perTick, OutboundChannel outbound,
                      Coordinator coordinator)
    {
        _main = main;
        _state = state;
        _items = items;
        _bankRepo = bankRepo;
        _inventory = inventory;
        _perTick = perTick;
        _outbound = outbound;
        _coordinator = coordinator;
    }

    internal bool IsPlayerInBank(long playerId) => _bankSessionByPlayer.ContainsKey(playerId);

    internal void ClearPlayer(long playerId)
    {
        _bankByPlayer.Remove(playerId);
        _bankCapacityByPlayer.Remove(playerId);
        _bankSessionByPlayer.Remove(playerId);
        _pendingBank.Remove(playerId);
        _pendingBankIntents.Remove(playerId);
    }

    // ---------- Loading / saving ----------

    internal void LoadForPlayer(long playerId)
    {
        var tuple = _bankRepo.GetAsync(playerId).Result;
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
        int lastUsed = -1;
        for (int i = 0; i < bank.Count; i++)
            if (bank[i] != null) lastUsed = i;
        var trimmed = lastUsed < 0
            ? new List<BankEntry?>()
            : bank.GetRange(0, lastUsed + 1);
        _bankRepo.SaveAsync(playerId, SerializeBank(trimmed));
    }

    // ---------- Entry: request to open bank ----------

    internal void RequestBank(long playerId, long npcId)
    {
        if (!_main.TryGetNpcById(npcId, out var npc)) return;
        if (npc.IsDead) return;
        if (!npc.IsBanker) { _outbound.ChatToPlayer(playerId, "They are not a banker."); return; }
        if (!_state.PeerByPlayerId.ContainsKey(playerId) || !_state.Positions.ContainsKey(playerId)) return;
        if (_pendingBank.TryGetValue(playerId, out var current) && current == npcId) return;

        _state.PendingEngage.Remove(playerId);
        _state.PendingPickup.Remove(playerId);
        _perTick?.EndByStem(playerId, "follow_player");
        _main.RemovePlayerIntent(playerId);
        _main.CancelDialogueForPlayer(playerId, "");
        CancelBankForPlayer(playerId, "");

        _pendingBank[playerId] = npcId;
        if (!_main.ArePositionsAdjacent(playerId, npc))
            _main.MovePlayerAdjacentToNpc(playerId, npc);
    }

    internal void CancelPendingBankFor(long playerId) => _pendingBank.Remove(playerId);

    internal void ProcessPendingBank()
    {
        if (_pendingBank.Count == 0) return;

        var toRemove = new List<long>();
        var toOpen = new List<(long PlayerId, long NpcId)>();

        foreach (var kv in _pendingBank)
        {
            long playerId = kv.Key;
            long npcId = kv.Value;

            if (!_main.TryGetNpcById(npcId, out var npc) || npc.IsDead || !npc.IsBanker)
            { toRemove.Add(playerId); continue; }
            if (!_state.Positions.ContainsKey(playerId)
                || !_state.PeerByPlayerId.TryGetValue(playerId, out var peerId))
            { toRemove.Add(playerId); continue; }
            if (_state.CombatByPlayer.ContainsKey(playerId)) { toRemove.Add(playerId); continue; }
            if (_main.ArePositionsAdjacent(playerId, npc))
            {
                toRemove.Add(playerId);
                toOpen.Add((playerId, npcId));
                continue;
            }
            if (!_main.MovePlayerAdjacentToNpc(playerId, npc))
            {
                toRemove.Add(playerId);
                _outbound.ChatToPlayer(playerId, "I can't seem to reach that.");
            }
        }

        foreach (var p in toRemove) _pendingBank.Remove(p);
        foreach (var (pid, nid) in toOpen) OpenBank(pid, nid);
    }

    internal void OpenBank(long playerId, long npcId)
    {
        if (!_main.TryGetNpcById(npcId, out var npc)) return;
        if (!npc.IsBanker || npc.IsDead) return;
        if (!_bankByPlayer.ContainsKey(playerId)) return;

        _coordinator.OnBankOpened(playerId);
        _perTick?.EndByStem(playerId, "follow_player");
        _main.RemovePlayerIntent(playerId);
        _state.PendingPickup.Remove(playerId);
        _state.PendingEngage.Remove(playerId);
        if (_state.CombatByPlayer.TryGetValue(playerId, out var pair) && !pair.StateAnnounced)
            _main.EndCombat(pair);

        _main.SnapAndFaceForDialogue(playerId, npc);

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
        if (!_state.PeerByPlayerId.TryGetValue(session.PlayerId, out var peerId)) return;
        var bank = _bankByPlayer[session.PlayerId];
        int cap = _bankCapacityByPlayer[session.PlayerId];
        _outbound.Send(peerId, new
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
            if (_items.TryGetById(entry.ItemId, out var meta))
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
        SendBankOpen(session);
    }

    private long GetPlayerIdByPeer(long peerId)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var sess)) return 0;
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

    internal void ProcessBankIntents()
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
        if (!_state.Inventories.TryGetValue(playerId, out var inv)) return false;
        if (!_bankByPlayer.TryGetValue(playerId, out var bank)) return false;
        if (!_items.TryGetById(intent.ItemId, out var item)) return false;
        if (item.NonBankable) { notice = ErrorKeys.BankNonBankable; return false; }

        int inInv = CountInventoryOfItem(inv, intent.ItemId);
        if (inInv <= 0) { notice = ErrorKeys.BankNothingToDeposit; return false; }
        int actual = intent.Quantity < 0 ? inInv : Math.Min(intent.Quantity, inInv);
        if (actual <= 0) return false;

        int baseId = intent.ItemId;
        if (!string.IsNullOrEmpty(item.CertOfSlug) && _items.TryGetIdBySlug(item.CertOfSlug, out var baseItemId))
            baseId = (int)baseItemId;

        int existingIdx = bank.FindIndex(e => e != null && e.ItemId == baseId);
        if (existingIdx >= 0)
        {
            int newQty = bank[existingIdx]!.Quantity + actual;
            bank[existingIdx]!.Quantity = newQty;
            RemoveInventoryByItemId(playerId, inv, intent.ItemId, actual);
            return true;
        }

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
        if (!_state.Inventories.TryGetValue(playerId, out var inv)) return false;
        if (!_items.TryGetById(intent.ItemId, out var bankedItem)) return false;

        int existingIdx = bank.FindIndex(e => e != null && e.ItemId == intent.ItemId);
        if (existingIdx < 0) { notice = ErrorKeys.BankNothingToWithdraw; return false; }

        int inBank = bank[existingIdx]!.Quantity;
        int requested = intent.Quantity < 0 ? inBank : Math.Min(intent.Quantity, inBank);

        int outputItemId = intent.ItemId;
        if (session.CertMode && !bankedItem.Stackable && !bankedItem.NonCertable)
        {
            if (_items.TryGetIdBySlug(bankedItem.Slug + "_cert", out var certId))
                outputItemId = (int)certId;
        }
        if (!_items.TryGetById(outputItemId, out var outItem)) return false;

        int fits = HowManyFitInInventory(inv, outputItemId, outItem.Stackable, requested);
        if (fits <= 0) { notice = ErrorKeys.BankPartialWithdraw; return false; }

        GiveItemToInventoryDirect(playerId, inv, outputItemId, outItem.Stackable, fits);

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

    private static int CountInventoryOfItem(long[] inv, int itemId)
    {
        int total = 0;
        foreach (long packed in inv)
        {
            if (packed == 0) continue;
            if (ItemStack.ItemId(packed) == itemId) total += ItemStack.Quantity(packed);
        }
        return total;
    }

    private static int HowManyFitInInventory(long[] inv, int itemId, bool stackable, int requested)
    {
        if (stackable)
        {
            for (int i = 0; i < inv.Length; i++)
                if (inv[i] != 0 && ItemStack.ItemId(inv[i]) == itemId) return requested;
            for (int i = 0; i < inv.Length; i++)
                if (inv[i] == 0) return requested;
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
            _inventory.SaveSlotSync(playerId, i, inv[i]);
        _outbound.BroadcastInventory(playerId);
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
        if (!_state.PeerByPlayerId.TryGetValue(session.PlayerId, out var peerId)) return;
        _outbound.Send(peerId, new
        {
            type = "bank_close",
            session_id = session.SessionId,
            error_key = silent ? "" : errorKey,
            error_params = new { },
        });
    }

    private void SendBankNotice(long playerId, string errorKey)
    {
        if (!_state.PeerByPlayerId.TryGetValue(playerId, out var peerId)) return;
        _outbound.Send(peerId, new { type = "bank_notice", error_key = errorKey, error_params = new { } });
    }
}