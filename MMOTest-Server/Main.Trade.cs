using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace MMOTest.Server;

public partial class Main
{
	private const long TradeApproachTimeoutTicks = 200;
	private const long TradeInviteTimeoutTicks = 200;
	private const long TradeSessionTimeoutTicks = 1200;
	private const long TradeRequestCooldownTicks = 10;

	private enum TradePhase { Offering, Confirming }

	private sealed class PendingTradeApproach
	{
		public long TargetPlayerId { get; init; }
		public long StartedTick { get; init; }
	}

	private sealed class PendingTradeInvite
	{
		public long RequesterId { get; init; }
		public string InviteId { get; init; }
		public long ExpiresAtTick { get; init; }
	}

	private sealed class TradeSession
	{
		public string Id { get; init; }
		public long PlayerAId { get; init; }
		public long PlayerBId { get; init; }
		public long StartedTick { get; set; }
		public int[] OfferA { get; } = new int[28];
		public int[] OfferB { get; } = new int[28];
		public int CreditsA { get; set; }
		public int CreditsB { get; set; }
		public bool AcceptedA { get; set; }
		public bool AcceptedB { get; set; }
		public bool ConfirmedA { get; set; }
		public bool ConfirmedB { get; set; }
		public TradePhase Phase { get; set; }
	}

	private readonly Dictionary<long, PendingTradeApproach> _pendingTradeApproaches = new();
	private readonly Dictionary<long, PendingTradeInvite> _pendingTradeInvites = new();
	private readonly Dictionary<long, TradeSession> _tradeByPlayer = new();
	private readonly Dictionary<long, long> _lastTradeRequestTick = new();

	internal bool IsTradeInventoryLocked(long playerId) => _tradeByPlayer.ContainsKey(playerId);

	internal void RequestTrade(long requesterId, long targetPlayerId)
	{
		if (requesterId == targetPlayerId)
		{
			SendTradeError(requesterId, "You cannot trade with yourself.");
			return;
		}
		if (!IsTradePlayerAvailable(requesterId) || !IsTradePlayerAvailable(targetPlayerId))
		{
			SendTradeError(requesterId, "That player is unavailable for trading.");
			return;
		}
		if (_tradeByPlayer.ContainsKey(requesterId) || HasPendingTradeApproach(requesterId)
			|| HasPendingTradeInvite(requesterId))
		{
			SendTradeError(requesterId, "You already have a trade request or session active.");
			return;
		}
		if (_tradeByPlayer.ContainsKey(targetPlayerId) || HasPendingTradeApproach(targetPlayerId)
			|| HasPendingTradeInvite(targetPlayerId))
		{
			SendTradeError(requesterId, "That player is busy.");
			return;
		}
		if (IsInCombatOrPursuit(requesterId) || IsInCombatOrPursuit(targetPlayerId))
		{
			SendTradeError(requesterId, "You cannot trade while either player is in combat.");
			return;
		}
		if (_lastTradeRequestTick.TryGetValue(requesterId, out long lastRequest)
			&& _currentTick - lastRequest < TradeRequestCooldownTicks)
		{
			SendTradeError(requesterId, "Please wait before sending another trade request.");
			return;
		}

		_lastTradeRequestTick[requesterId] = _currentTick;
		if (ArePlayersAdjacent(requesterId, targetPlayerId))
		{
			SendTradeInvite(requesterId, targetPlayerId);
			return;
		}

		_pendingTradeApproaches[requesterId] = new PendingTradeApproach
		{
			TargetPlayerId = targetPlayerId,
			StartedTick = _currentTick,
		};
		_pendingPickup.Remove(requesterId);
		_perTick?.EndByStem(requesterId, "follow_player");
		RemovePlayerIntent(requesterId);
		MovePlayerAdjacentToPlayer(requesterId, targetPlayerId);
		ChatToPlayer(requesterId, "Approaching that player to trade.");
	}

	private bool IsTradePlayerAvailable(long playerId)
		=> _peerByPlayerId.ContainsKey(playerId)
			&& _positions.ContainsKey(playerId)
			&& _inventoryByPlayer.ContainsKey(playerId)
			&& _creditsByPlayer.ContainsKey(playerId);

	private bool HasPendingTradeInvite(long playerId)
		=> _pendingTradeInvites.ContainsKey(playerId)
			|| _pendingTradeInvites.Values.Any(invite => invite.RequesterId == playerId);

	private bool HasPendingTradeApproach(long playerId)
		=> _pendingTradeApproaches.ContainsKey(playerId)
			|| _pendingTradeApproaches.Values.Any(approach => approach.TargetPlayerId == playerId);

	private bool IsInCombatOrPursuit(long playerId)
		=> _combatByPlayer.ContainsKey(playerId)
			|| _pendingEngage.ContainsKey(playerId)
			|| (_playerCombat.TryGetValue(playerId, out var combat) && combat.CurrentHp <= 0);

	private bool TryGetPlayerSession(long playerId, out PlayerSession session)
	{
		foreach (var entry in _sessions.Values)
		{
			if (entry.PlayerId != playerId) continue;
			session = entry;
			return true;
		}
		session = null;
		return false;
	}

	private void ProcessTradeQueues()
	{
		foreach (var entry in _pendingTradeApproaches.ToList())
		{
			long requesterId = entry.Key;
			var pending = entry.Value;
			if (_currentTick - pending.StartedTick >= TradeApproachTimeoutTicks
				|| !IsTradePlayerAvailable(requesterId)
				|| !IsTradePlayerAvailable(pending.TargetPlayerId)
				|| IsInCombatOrPursuit(requesterId)
				|| IsInCombatOrPursuit(pending.TargetPlayerId)
				|| _tradeByPlayer.ContainsKey(requesterId)
				|| _tradeByPlayer.ContainsKey(pending.TargetPlayerId))
			{
				_pendingTradeApproaches.Remove(requesterId);
				SendTradeError(requesterId, "Trade approach cancelled.");
				continue;
			}

			if (ArePlayersAdjacent(requesterId, pending.TargetPlayerId))
			{
				_pendingTradeApproaches.Remove(requesterId);
				SendTradeInvite(requesterId, pending.TargetPlayerId);
			}
			else if (!IsPlayerMoving(requesterId))
			{
				MovePlayerAdjacentToPlayer(requesterId, pending.TargetPlayerId);
			}
		}

		foreach (var entry in _pendingTradeInvites.ToList())
		{
			long targetId = entry.Key;
			var invite = entry.Value;
			if (_currentTick >= invite.ExpiresAtTick
				|| !ArePlayersAdjacent(invite.RequesterId, targetId)
				|| !IsTradePlayerAvailable(invite.RequesterId)
				|| !IsTradePlayerAvailable(targetId)
				|| IsInCombatOrPursuit(invite.RequesterId)
				|| IsInCombatOrPursuit(targetId))
			{
				CloseTradeInvite(targetId, invite, "The trade request expired or was interrupted.");
			}
		}

		foreach (TradeSession trade in _tradeByPlayer.Values.Distinct().ToList())
		{
			if (_currentTick - trade.StartedTick >= TradeSessionTimeoutTicks)
				CancelTrade(trade, "Trade timed out.");
			else if (!ArePlayersAdjacent(trade.PlayerAId, trade.PlayerBId))
				CancelTrade(trade, "Trade cancelled because a player moved away.");
			else if (IsInCombatOrPursuit(trade.PlayerAId) || IsInCombatOrPursuit(trade.PlayerBId))
				CancelTrade(trade, "Trade cancelled because a player entered combat.");
		}
	}

	private void SendTradeInvite(long requesterId, long targetPlayerId)
	{
		if (HasPendingTradeInvite(requesterId) || HasPendingTradeInvite(targetPlayerId)
			|| HasPendingTradeApproach(requesterId) || HasPendingTradeApproach(targetPlayerId)
			|| _tradeByPlayer.ContainsKey(requesterId)
			|| _tradeByPlayer.ContainsKey(targetPlayerId))
		{
			SendTradeError(requesterId, "That player is busy.");
			return;
		}
		if (!TryGetPlayerSession(requesterId, out var requester)
			|| !TryGetPlayerSession(targetPlayerId, out var target))
			return;

		string inviteId = Guid.NewGuid().ToString("N");
		var invite = new PendingTradeInvite
		{
			RequesterId = requesterId,
			InviteId = inviteId,
			ExpiresAtTick = _currentTick + TradeInviteTimeoutTicks,
		};
		_pendingTradeInvites[targetPlayerId] = invite;
		if (OS.IsDebugBuild())
			GD.Print($"[Trade] Invite {inviteId} created: requester={requesterId}, target={targetPlayerId}, expires={invite.ExpiresAtTick}");
		SendToTradePlayer(targetPlayerId, new
		{
			type = "trade_request_incoming",
			invite_id = inviteId,
			from_player_id = requesterId,
			from_username = requester.Username,
		});
		ChatToPlayer(requesterId, $"Trade request sent to {target.Username}.");
	}

	internal void HandleTradeInviteResponse(long peerId, JsonElement root, bool accept)
	{
		if (!_sessions.TryGetValue(peerId, out var recipient)) return;
		if (!root.TryGetProperty("invite_id", out var inviteElement)
			|| inviteElement.ValueKind != JsonValueKind.String) return;
		string inviteId = inviteElement.GetString() ?? "";
		if (inviteId.Length == 0) return;
		if (!_pendingTradeInvites.TryGetValue(recipient.PlayerId, out var invite)
			|| invite.InviteId != inviteId)
		{
			string pendingId = _pendingTradeInvites.TryGetValue(recipient.PlayerId, out var currentInvite)
				? currentInvite.InviteId
				: "none";
			GD.PrintErr($"[Trade] Invite response rejected: recipient={recipient.PlayerId}, received={inviteId}, pending={pendingId}, tick={_currentTick}");
			SendToTradePlayer(recipient.PlayerId, new
			{
				type = "trade_request_cancelled",
				invite_id = inviteId,
				reason = "This trade request is no longer available.",
			});
			return;
		}
		if (_currentTick >= invite.ExpiresAtTick)
		{
			GD.PrintErr($"[Trade] Expired invite response: invite={invite.InviteId}, recipient={recipient.PlayerId}, tick={_currentTick}, expires={invite.ExpiresAtTick}");
			CloseTradeInvite(recipient.PlayerId, invite, "The trade request expired.");
			return;
		}

		_pendingTradeInvites.Remove(recipient.PlayerId);
		if (!accept)
		{
			SendToTradePlayer(invite.RequesterId, new
			{
				type = "trade_request_cancelled",
				invite_id = invite.InviteId,
				reason = "The player declined the trade request.",
			});
			ChatToPlayer(invite.RequesterId, $"{recipient.Username} declined the trade request.");
			return;
		}

		if (!IsTradePlayerAvailable(invite.RequesterId)
			|| !ArePlayersAdjacent(invite.RequesterId, recipient.PlayerId)
			|| IsInCombatOrPursuit(invite.RequesterId)
			|| IsInCombatOrPursuit(recipient.PlayerId)
			|| _tradeByPlayer.ContainsKey(invite.RequesterId)
			|| _tradeByPlayer.ContainsKey(recipient.PlayerId))
		{
			GD.PrintErr($"[Trade] Accepted invite became unavailable: invite={invite.InviteId}, requester={invite.RequesterId}, target={recipient.PlayerId}, adjacent={ArePlayersAdjacent(invite.RequesterId, recipient.PlayerId)}, requester_available={IsTradePlayerAvailable(invite.RequesterId)}, target_available={IsTradePlayerAvailable(recipient.PlayerId)}, requester_combat={IsInCombatOrPursuit(invite.RequesterId)}, target_combat={IsInCombatOrPursuit(recipient.PlayerId)}");
			SendTradeError(recipient.PlayerId, "The trade is no longer available.");
			SendTradeError(invite.RequesterId, "The trade is no longer available.");
			return;
		}

		if (!TryGetPlayerSession(invite.RequesterId, out var requester)) return;
		_perTick?.EndByStem(invite.RequesterId, "follow_player");
		_perTick?.EndByStem(recipient.PlayerId, "follow_player");
		RemovePlayerIntent(invite.RequesterId);
		RemovePlayerIntent(recipient.PlayerId);

		var trade = new TradeSession
		{
			Id = Guid.NewGuid().ToString("N"),
			PlayerAId = invite.RequesterId,
			PlayerBId = recipient.PlayerId,
			StartedTick = _currentTick,
		};
		if (OS.IsDebugBuild())
			GD.Print($"[Trade] Invite {invite.InviteId} accepted by {recipient.PlayerId}");
		_tradeByPlayer[trade.PlayerAId] = trade;
		_tradeByPlayer[trade.PlayerBId] = trade;
		_pendingPickup.Remove(trade.PlayerAId);
		_pendingPickup.Remove(trade.PlayerBId);
		SendTradeState(trade);
	}

	internal void HandleTradeOfferSet(long peerId, JsonElement root)
	{
		if (!TryGetTrade(peerId, root, out var trade, out long playerId, out bool isPlayerA)) return;
		if (trade.Phase != TradePhase.Offering)
		{
			SendTradeError(playerId, "The offer is locked. Choose Edit Offer first.");
			return;
		}
		if (!root.TryGetProperty("slot", out var slotElement) || !slotElement.TryGetInt32(out int slot)
			|| !root.TryGetProperty("quantity", out var quantityElement) || !quantityElement.TryGetInt32(out int quantity)
			|| slot < 0 || slot >= 28 || quantity < 0)
		{
			SendTradeError(playerId, "Invalid item offer.");
			return;
		}
		if (!_inventoryByPlayer.TryGetValue(playerId, out var inventory)) return;

		if (quantity > 0)
		{
			long packed = inventory[slot];
			if (packed == 0 || quantity > ItemStack.Quantity(packed)
				|| !_itemsById.TryGetValue(ItemStack.ItemId(packed), out var item)
				|| !item.CanTrade
				|| (!item.Stackable && quantity != 1))
			{
				SendTradeError(playerId, "That item or quantity cannot be offered.");
				return;
			}
		}

		GetTradeOffer(trade, isPlayerA)[slot] = quantity;
		ResetTradeAcceptance(trade);
		trade.StartedTick = _currentTick;
		SendTradeState(trade);
	}

	internal void HandleTradeCreditsSet(long peerId, JsonElement root)
	{
		if (!TryGetTrade(peerId, root, out var trade, out long playerId, out bool isPlayerA)) return;
		if (trade.Phase != TradePhase.Offering)
		{
			SendTradeError(playerId, "The offer is locked. Choose Edit Offer first.");
			return;
		}
		if (!root.TryGetProperty("amount", out var amountElement)
			|| !amountElement.TryGetInt32(out int amount) || amount < 0
			|| !_creditsByPlayer.TryGetValue(playerId, out int balance) || amount > balance)
		{
			SendTradeError(playerId, "Invalid credit offer.");
			return;
		}

		if (isPlayerA) trade.CreditsA = amount;
		else trade.CreditsB = amount;
		ResetTradeAcceptance(trade);
		trade.StartedTick = _currentTick;
		SendTradeState(trade);
	}

	internal void HandleTradeAcceptOffer(long peerId, JsonElement root)
	{
		if (!TryGetTrade(peerId, root, out var trade, out long playerId, out bool isPlayerA)) return;
		if (trade.Phase != TradePhase.Offering) return;
		if (!TryBuildTradeInventories(trade, out _, out _, out string failure))
		{
			SendTradeError(trade.PlayerAId, failure);
			SendTradeError(trade.PlayerBId, failure);
			CancelTrade(trade, failure);
			return;
		}

		if (isPlayerA) trade.AcceptedA = true;
		else trade.AcceptedB = true;
		if (trade.AcceptedA && trade.AcceptedB)
			trade.Phase = TradePhase.Confirming;
		trade.StartedTick = _currentTick;
		SendTradeState(trade);
	}

	internal void HandleTradeEditOffer(long peerId, JsonElement root)
	{
		if (!TryGetTrade(peerId, root, out var trade, out _, out _)) return;
		if (trade.Phase != TradePhase.Confirming) return;
		trade.Phase = TradePhase.Offering;
		ResetTradeAcceptance(trade);
		trade.StartedTick = _currentTick;
		SendTradeState(trade);
	}

	internal void HandleTradeConfirm(long peerId, JsonElement root)
	{
		if (!TryGetTrade(peerId, root, out var trade, out _, out bool isPlayerA)) return;
		if (trade.Phase != TradePhase.Confirming) return;
		if (isPlayerA) trade.ConfirmedA = true;
		else trade.ConfirmedB = true;
		SendTradeState(trade);
		if (trade.ConfirmedA && trade.ConfirmedB)
			CommitTrade(trade);
	}

	internal void HandleTradeCancel(long peerId, JsonElement root)
	{
		if (!_sessions.TryGetValue(peerId, out var player)) return;
		if (_tradeByPlayer.TryGetValue(player.PlayerId, out var trade)
			&& root.TryGetProperty("session_id", out var idElement)
			&& idElement.ValueKind == JsonValueKind.String
			&& idElement.GetString() == trade.Id)
		{
			CancelTrade(trade, "Trade cancelled.");
			return;
		}
		CancelTradeQueuesForPlayer(player.PlayerId, "Trade request cancelled.");
	}

	internal void CancelTradeQueuesForPlayer(long playerId, string reason)
	{
		if (_pendingTradeApproaches.Remove(playerId))
			SendTradeError(playerId, reason);

		foreach (var entry in _pendingTradeApproaches.Where(p => p.Value.TargetPlayerId == playerId).ToList())
		{
			_pendingTradeApproaches.Remove(entry.Key);
			SendTradeError(entry.Key, reason);
		}

		foreach (var entry in _pendingTradeInvites.ToList())
		{
			if (entry.Key == playerId || entry.Value.RequesterId == playerId)
				CloseTradeInvite(entry.Key, entry.Value, reason);
		}
	}

	internal void CancelTradeForPlayer(long playerId, string reason)
	{
		if (_tradeByPlayer.TryGetValue(playerId, out var trade))
			CancelTrade(trade, reason);
	}

	private bool TryGetTrade(long peerId, JsonElement root, out TradeSession trade, out long playerId, out bool isPlayerA)
	{
		trade = null;
		playerId = 0;
		isPlayerA = false;
		if (!_sessions.TryGetValue(peerId, out var player)
			|| !_tradeByPlayer.TryGetValue(player.PlayerId, out trade)
			|| !root.TryGetProperty("session_id", out var idElement)
			|| idElement.ValueKind != JsonValueKind.String
			|| idElement.GetString() != trade.Id)
			return false;
		playerId = player.PlayerId;
		isPlayerA = playerId == trade.PlayerAId;
		return true;
	}

	private bool ArePlayersAdjacent(long playerAId, long playerBId)
	{
		if (!_positions.TryGetValue(playerAId, out var a) || !_positions.TryGetValue(playerBId, out var b)) return false;
		int dx = Math.Abs((int)MathF.Round(a.X) - (int)MathF.Round(b.X));
		int dy = Math.Abs((int)MathF.Round(a.Z) - (int)MathF.Round(b.Z));
		return dx <= 1 && dy <= 1 && (dx != 0 || dy != 0);
	}

	private void MovePlayerAdjacentToPlayer(long playerId, long targetPlayerId)
	{
		if (!_peerByPlayerId.TryGetValue(playerId, out long peerId)
			|| _intents.ContainsKey(peerId)
			|| !_positions.TryGetValue(playerId, out var position)
			|| !_positions.TryGetValue(targetPlayerId, out var target)) return;

		int px = (int)MathF.Round(position.X);
		int py = (int)MathF.Round(position.Z);
		int tx = (int)MathF.Round(target.X);
		int ty = (int)MathF.Round(target.Z);
		if (px == tx && py == ty)
		{
			Vector2I[] directions = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1), new(1, 1), new(1, -1), new(-1, 1), new(-1, -1) };
			foreach (var direction in directions)
			{
				var tile = new Vector2I(px + direction.X, py + direction.Y);
				if (!IsValidTile(tile.X, tile.Y)) continue;
				_intents[peerId] = new List<Vector2I> { tile };
				return;
			}
			return;
		}

		var path = PathfindToNearest(playerId, tx, ty);
		if (path.Count == 0) return;
		if (path[^1] == new Vector2I(tx, ty)) path.RemoveAt(path.Count - 1);
		if (path.Count > 0) SetIntent(playerId, path);
	}

	private void RemovePlayerIntent(long playerId)
	{
		if (_peerByPlayerId.TryGetValue(playerId, out long peerId)) _intents.Remove(peerId);
	}

	private void ResetTradeAcceptance(TradeSession trade)
	{
		trade.AcceptedA = false;
		trade.AcceptedB = false;
		trade.ConfirmedA = false;
		trade.ConfirmedB = false;
	}

	private int[] GetTradeOffer(TradeSession trade, bool isPlayerA) => isPlayerA ? trade.OfferA : trade.OfferB;

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
		int[] ownOffer = GetTradeOffer(trade, isPlayerA);
		int[] partnerOffer = GetTradeOffer(trade, !isPlayerA);
		SendToTradePlayer(playerId, new
		{
			type = "trade_state",
			session_id = trade.Id,
			partner_id = partnerId,
			partner_username = partner.Username,
			phase = trade.Phase == TradePhase.Offering ? "offering" : "confirming",
			my_offer = BuildTradeOffer(playerId, ownOffer),
			their_offer = BuildTradeOffer(partnerId, partnerOffer),
			my_credits = ownCredits,
			their_credits = partnerCredits,
			my_accepted = isPlayerA ? trade.AcceptedA : trade.AcceptedB,
			their_accepted = isPlayerA ? trade.AcceptedB : trade.AcceptedA,
			my_confirmed = isPlayerA ? trade.ConfirmedA : trade.ConfirmedB,
			their_confirmed = isPlayerA ? trade.ConfirmedB : trade.ConfirmedA,
		});
	}

	private List<object> BuildTradeOffer(long playerId, int[] quantities)
	{
		var result = new List<object>();
		if (!_inventoryByPlayer.TryGetValue(playerId, out var inventory)) return result;
		for (int slot = 0; slot < quantities.Length; slot++)
		{
			int quantity = quantities[slot];
			long packed = inventory[slot];
			if (quantity <= 0 || packed == 0) continue;
			int itemId = ItemStack.ItemId(packed);
			if (!_itemsById.TryGetValue(itemId, out var item)) continue;
			result.Add(new { slot, item_id = itemId, slug = item.Slug, display = item.Display, quantity });
		}
		return result;
	}

	private void SendToTradePlayer(long playerId, object message)
	{
		if (_peerByPlayerId.TryGetValue(playerId, out long peerId)) Send(peerId, message);
	}

	private void SendTradeError(long playerId, string message)
		=> SendToTradePlayer(playerId, new { type = "trade_error", message });

	private void CloseTradeInvite(long targetId, PendingTradeInvite invite, string reason)
	{
		if (!_pendingTradeInvites.TryGetValue(targetId, out var current) || current.InviteId != invite.InviteId) return;
		if (OS.IsDebugBuild())
			GD.Print($"[Trade] Invite {invite.InviteId} closed: requester={invite.RequesterId}, target={targetId}, tick={_currentTick}, reason={reason}");
		_pendingTradeInvites.Remove(targetId);
		var message = new { type = "trade_request_cancelled", invite_id = invite.InviteId, reason };
		SendToTradePlayer(targetId, message);
		SendToTradePlayer(invite.RequesterId, message);
		ChatToPlayer(invite.RequesterId, reason);
	}

	private void CancelTrade(TradeSession trade, string reason)
	{
		if (!_tradeByPlayer.TryGetValue(trade.PlayerAId, out var current) || current.Id != trade.Id) return;
		_tradeByPlayer.Remove(trade.PlayerAId);
		_tradeByPlayer.Remove(trade.PlayerBId);
		var message = new { type = "trade_cancelled", session_id = trade.Id, reason };
		SendToTradePlayer(trade.PlayerAId, message);
		SendToTradePlayer(trade.PlayerBId, message);
	}

	private bool TryBuildTradeInventories(TradeSession trade, out long[] inventoryA, out long[] inventoryB, out string failure)
	{
		inventoryA = null;
		inventoryB = null;
		failure = "The trade offer is no longer valid.";
		if (!_inventoryByPlayer.TryGetValue(trade.PlayerAId, out var currentA)
			|| !_inventoryByPlayer.TryGetValue(trade.PlayerBId, out var currentB)
			|| !_creditsByPlayer.TryGetValue(trade.PlayerAId, out int balanceA)
			|| !_creditsByPlayer.TryGetValue(trade.PlayerBId, out int balanceB)
			|| trade.CreditsA > balanceA || trade.CreditsB > balanceB) return false;

		inventoryA = (long[])currentA.Clone();
		inventoryB = (long[])currentB.Clone();
		if (!RemoveTradeOffer(trade.OfferA, currentA, inventoryA)
			|| !RemoveTradeOffer(trade.OfferB, currentB, inventoryB)) return false;
		if (!AddTradeOffer(trade.OfferB, currentB, inventoryA)
			|| !AddTradeOffer(trade.OfferA, currentA, inventoryB))
		{
			failure = "Trade cancelled because an inventory does not have enough room.";
			return false;
		}

		long creditsA = (long)balanceA - trade.CreditsA + trade.CreditsB;
		long creditsB = (long)balanceB - trade.CreditsB + trade.CreditsA;
		if (creditsA > int.MaxValue || creditsB > int.MaxValue)
		{
			failure = "Trade cancelled because a credit balance would exceed its limit.";
			return false;
		}
		return true;
	}

	private bool RemoveTradeOffer(int[] offer, long[] source, long[] result)
	{
		for (int slot = 0; slot < offer.Length; slot++)
		{
			int quantity = offer[slot];
			if (quantity == 0) continue;
			long packed = source[slot];
			if (packed == 0 || quantity > ItemStack.Quantity(packed)) return false;
			int itemId = ItemStack.ItemId(packed);
			if (!_itemsById.TryGetValue(itemId, out var item) || !item.CanTrade || (!item.Stackable && quantity != 1)) return false;
			int remainder = ItemStack.Quantity(packed) - quantity;
			result[slot] = remainder > 0 ? ItemStack.Encode(itemId, remainder) : 0;
		}
		return true;
	}

	private bool AddTradeOffer(int[] offer, long[] source, long[] target)
	{
		for (int slot = 0; slot < offer.Length; slot++)
		{
			int quantity = offer[slot];
			if (quantity == 0) continue;
			int itemId = ItemStack.ItemId(source[slot]);
			if (!_itemsById.TryGetValue(itemId, out var item) || !item.CanTrade) return false;
			if (item.Stackable)
			{
				int existing = Array.FindIndex(target, packed => packed != 0 && ItemStack.ItemId(packed) == itemId);
				if (existing >= 0)
				{
					long total = (long)ItemStack.Quantity(target[existing]) + quantity;
					if (total > int.MaxValue) return false;
					target[existing] = ItemStack.Encode(itemId, (int)total);
					continue;
				}
				int empty = Array.FindIndex(target, packed => packed == 0);
				if (empty < 0) return false;
				target[empty] = ItemStack.Encode(itemId, quantity);
				continue;
			}

			for (int count = 0; count < quantity; count++)
			{
				int empty = Array.FindIndex(target, packed => packed == 0);
				if (empty < 0) return false;
				target[empty] = ItemStack.Encode(itemId, 1);
			}
		}
		return true;
	}

	private void CommitTrade(TradeSession trade)
	{
		if (!ArePlayersAdjacent(trade.PlayerAId, trade.PlayerBId)
			|| IsInCombatOrPursuit(trade.PlayerAId) || IsInCombatOrPursuit(trade.PlayerBId))
		{
			CancelTrade(trade, "Trade cancelled because a player moved away or entered combat.");
			return;
		}
		if (!TryBuildTradeInventories(trade, out var inventoryA, out var inventoryB, out string failure))
		{
			SendTradeError(trade.PlayerAId, failure);
			SendTradeError(trade.PlayerBId, failure);
			CancelTrade(trade, failure);
			return;
		}

		int balanceA = _creditsByPlayer[trade.PlayerAId];
		int balanceB = _creditsByPlayer[trade.PlayerBId];
		int resultCreditsA = checked(balanceA - trade.CreditsA + trade.CreditsB);
		int resultCreditsB = checked(balanceB - trade.CreditsB + trade.CreditsA);
		try
		{
			if (!_db.CommitTradeSync(trade.PlayerAId, inventoryA, resultCreditsA,
				trade.PlayerBId, inventoryB, resultCreditsB))
			{
				CancelTrade(trade, "Trade could not be saved. No items were exchanged.");
				return;
			}
		}
		catch (Exception e)
		{
			GD.PrintErr($"[Server] Trade {trade.Id} commit failed: {e.Message}");
			CancelTrade(trade, "Trade could not be saved. No items were exchanged.");
			return;
		}

		_inventoryByPlayer[trade.PlayerAId] = inventoryA;
		_inventoryByPlayer[trade.PlayerBId] = inventoryB;
		_creditsByPlayer[trade.PlayerAId] = resultCreditsA;
		_creditsByPlayer[trade.PlayerBId] = resultCreditsB;
		_tradeByPlayer.Remove(trade.PlayerAId);
		_tradeByPlayer.Remove(trade.PlayerBId);
		BroadcastInventory(trade.PlayerAId);
		BroadcastInventory(trade.PlayerBId);
		SendCreditsToPlayer(trade.PlayerAId, resultCreditsA);
		SendCreditsToPlayer(trade.PlayerBId, resultCreditsB);
		SendToTradePlayer(trade.PlayerAId, new { type = "trade_completed", session_id = trade.Id });
		SendToTradePlayer(trade.PlayerBId, new { type = "trade_completed", session_id = trade.Id });
		Diag.Log("diag_item", $"Trade {trade.Id} completed between {trade.PlayerAId} and {trade.PlayerBId}");
	}

	private void SendCreditsToPlayer(long playerId, int credits)
	{
		if (_peerByPlayerId.TryGetValue(playerId, out long peerId))
			Send(peerId, new { type = "credits_updated", credits });
	}
}