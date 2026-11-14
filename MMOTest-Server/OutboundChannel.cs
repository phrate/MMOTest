using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns the outbound send pipeline: serialize-to-bytes, broadcast, per-player
/// routing, inventory/skills payload assembly, chat effect routing.
///
/// The lowest-level transport (ENet SendBytes with the connected-peer check)
/// is injected as a callback by Main so this class has no dependency on
/// SceneMultiplayer directly.
/// </summary>
internal sealed class OutboundChannel
{
    private readonly ServerState _state;
    private readonly ItemLookup _items;
    private readonly Action<long, byte[], int, MultiplayerPeer.TransferModeEnum> _sendBytes;

    public OutboundChannel(ServerState state, ItemLookup items,
                           Action<long, byte[], int, MultiplayerPeer.TransferModeEnum> sendBytes)
    {
        _state = state;
        _items = items;
        _sendBytes = sendBytes;
    }

    public void Send(long peerId, object message, int channel = 0,
        MultiplayerPeer.TransferModeEnum mode = MultiplayerPeer.TransferModeEnum.Reliable)
    {
        _sendBytes(peerId, JsonSerializer.SerializeToUtf8Bytes(message), channel, mode);
    }

    public void SendError(long peerId, string type, string errorKey, object? errorParams = null)
    => Send(peerId, new
    {
        type,
        ok = false,
        error_key = errorKey,
        error_params = errorParams ?? new { },
    });

    public void Broadcast(object message, long exceptPeerId = 0, int channel = 0,
        MultiplayerPeer.TransferModeEnum mode = MultiplayerPeer.TransferModeEnum.Reliable)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        foreach (long peerId in _state.Sessions.Keys)
        {
            if (peerId == exceptPeerId) continue;
            _sendBytes(peerId, bytes, channel, mode);
        }
    }

    public void ChatToPlayer(long playerId, string message)
    {
        if (_state.PeerByPlayerId.TryGetValue(playerId, out long peerId))
            Send(peerId, new { type = "private_message", text = message });
    }

    public void BroadcastInventory(long playerId)
    {
        if (_state.PeerByPlayerId.TryGetValue(playerId, out long peerId))
            SendInventory(peerId);
    }

    public void BroadcastCredits(long playerId, int credits)
    {
        if (_state.PeerByPlayerId.TryGetValue(playerId, out long peerId))
            Send(peerId, new { type = "credits_updated", credits });
    }

    public void BroadcastPlayerHp(long playerId, int hp, int maxHp)
    {
        Broadcast(new
        {
            type = "player_hp_updated",
            player_id = playerId,
            hitpoints = hp,
            max_hitpoints = maxHp
        });
    }

    public void BroadcastNpcHp(long npcId, int hp, int maxHp)
    {
        Broadcast(new
        {
            type = "npc_hp_updated",
            npc_id = npcId,
            hitpoints = hp,
            max_hitpoints = maxHp
        });
    }

    public void BroadcastPlayerAnim(long playerId, string anim, int durationMs)
    {
        Broadcast(new { type = "anim_changed", player_id = playerId, anim, duration_ms = durationMs });
    }

    public void BroadcastNpcAnim(long npcId, string anim, int durationMs)
    {
        Broadcast(new { type = "npc_anim_changed", npc_id = npcId, anim, duration_ms = durationMs });
    }

    public void SendInventory(long peerId)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!_state.Inventories.TryGetValue(session.PlayerId, out var inv)) return;

        var slots = new List<object>();
        for (int i = 0; i < 28; i++)
        {
            long packed = inv[i];
            if (packed == 0) continue;
            int itemId = ItemStack.ItemId(packed);
            int qty = ItemStack.Quantity(packed);
            if (!_items.TryGetById(itemId, out var meta)) continue;
            slots.Add(new
            {
                slot = i,
                item_id = itemId,
                slug = meta.Slug,
                display = meta.Display,
                type = meta.Type,
                type_slug = meta.TypeSlug,
                quantity = qty,
                cert_of_slug = meta.CertOfSlug,
                can_trade = meta.CanTrade,
                can_bank = !meta.NonBankable,
            });
        }
        Send(peerId, new { type = "inventory_data", slots });
    }

    public void SendSkills(long peerId)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!_state.SkillsByPlayer.TryGetValue(session.PlayerId, out var skills)) return;

        var list = skills.Select(s =>
        {
            int level = Math.Clamp(s.Max, 1, SkillExperience.MaxLevel);
            int displayedLevel = s.Level;
            int displayedMax = s.Max;
            if (s.Id == "hitpoints" && _state.PlayerCombat.TryGetValue(session.PlayerId, out var combat))
            {
                displayedLevel = combat.CurrentHp;
                displayedMax = combat.MaxHp;
            }
            bool maxLevelReached = level == SkillExperience.MaxLevel;
            int nextLevel = maxLevelReached ? SkillExperience.MaxLevel : level + 1;
            int currentXp = SkillExperience.GetDisplayedXp(s.CurrentXpTenths);
            int remainingXp = SkillExperience.GetDisplayedXpToNextLevel(s.CurrentXpTenths, level);

            return new
            {
                id = s.Id,
                level = displayedLevel,
                max = displayedMax,
                current_xp = currentXp,
                next_level = nextLevel,
                remaining_xp = remainingXp,
                max_level_reached = maxLevelReached,
            };
        }).ToList();
        Send(peerId, new { type = "skills_data", skills = list });
    }

    public void RouteChatEffects(long pid, string username, int cx, int cy,
        List<Dictionary<string, object>> effects, long selfPeer)
    {
        foreach (var effect in effects)
        {
            string kind = effect.TryGetValue("kind", out var k) ? k as string ?? "" : "";
            string text = effect.TryGetValue("text", out var t) ? t as string ?? "" : "";
            switch (kind)
            {
                case "chat_local":
                    if (selfPeer >= 0) Send(selfPeer, new { type = "private_message", text });
                    break;
                case "chat_public":
                    Broadcast(new { type = "chat_message", username, message = text, from_chunk = new[] { cx, cy } });
                    break;
                case "chat_overhead":
                    Broadcast(new { type = "overhead_message", username, text, from_chunk = new[] { cx, cy } });
                    break;
                case "chat_private":
                    long targetPid = effect.TryGetValue("target_player_id", out var tpid) ? Convert.ToInt64(tpid) : 0;
                    if (targetPid > 0 && _state.PeerByPlayerId.TryGetValue(targetPid, out long targetPeer))
                        Send(targetPeer, new { type = "private_message", text });
                    break;
                case "anim_set":
                    string anim = effect.TryGetValue("anim", out var a) ? a as string ?? "" : "";
                    long dur = effect.TryGetValue("duration_ms", out var d) ? Convert.ToInt64(d) : 0;
                    Broadcast(new { type = "anim_changed", player_id = pid, anim, duration_ms = dur });
                    break;
            }
        }
    }
}