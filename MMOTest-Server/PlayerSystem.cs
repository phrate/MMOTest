using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Admin-only player inspection and mutation. Mirrors NpcSystem's
/// HandleDevNpcType* shape — session.IsAdmin gate, structured reply,
/// defensive validation before every write.
///
/// Design principles:
///   - Browse list is DB-paged (20 rows/page, 4 fields/row). Never preloaded.
///   - Full player data is gathered on demand for one player at a time.
///   - Online players prefer live in-memory ServerState (fresher than DB
///     because DB writes are async); offline players read DB directly.
///   - Level edits write level columns AND xp column atomically so the
///     level/XP stay consistent — otherwise the next XP gain would call
///     GetLevelForXpTenths and silently revert the admin's override.
/// </summary>
internal sealed class PlayerSystem
{
	public const int PageSize = 20;

	private readonly ServerState _state;
	private readonly OutboundChannel _outbound;
	private readonly ItemLookup _items;
	private readonly WorldService _world;
	private readonly PlayerRepository _players;
	private readonly PlayerStatsRepository _stats;
	private readonly InventoryRepository _inventory;
	private readonly BankRepository _bank;

	public PlayerSystem(ServerState state, OutboundChannel outbound, ItemLookup items,
		WorldService world, PlayerRepository players, PlayerStatsRepository stats,
		InventoryRepository inventory, BankRepository bank)
	{
		_state = state;
		_outbound = outbound;
		_items = items;
		_world = world;
		_players = players;
		_stats = stats;
		_inventory = inventory;
		_bank = bank;
	}

	// ============================================================
	// dev_player_list — DB-paged browse
	// ============================================================

	internal void HandleDevPlayerList(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;

		string search = "";
		if (root.TryGetProperty("search", out var searchEl)
			&& searchEl.ValueKind == JsonValueKind.String)
			search = searchEl.GetString() ?? "";

		int page = 0;
		if (root.TryGetProperty("page", out var pageEl)
			&& pageEl.TryGetInt32(out int p) && p >= 0)
			page = p;

		_ = ListAsync(peerId, search, page);
	}

	private async System.Threading.Tasks.Task ListAsync(long peerId, string search, int page)
	{
		try
		{
			var (items, totalCount) = await _players.GetPagedAsync(search, page, PageSize);
			var payload = new List<object>(items.Count);
			foreach (var item in items)
			{
				payload.Add(new
				{
					id = item.Id,
					username = item.Username,
					final_level = item.FinalLevel,
					credits = item.Credits,
				});
			}
			_outbound.Send(peerId, new
			{
				type = "dev_player_list_result",
				ok = true,
				items = payload,
				total_count = totalCount,
				page = page,
				page_size = PageSize,
			});
		}
		catch (Exception e)
		{
			GD.PrintErr($"[PlayerSystem] ListAsync: {e.Message}");
			_outbound.Send(peerId, new
			{
				type = "dev_player_list_result",
				ok = false,
				items = new List<object>(),
				total_count = 0,
				page = page,
				page_size = PageSize,
			});
		}
	}

	// ============================================================
	// dev_player_get — full data for one player
	// ============================================================

	internal void HandleDevPlayerGet(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		if (!root.TryGetProperty("player_id", out var idEl)
			|| !idEl.TryGetInt64(out long playerId)) return;

		_ = GetAsync(peerId, playerId);
	}

	private async System.Threading.Tasks.Task GetAsync(long peerId, long playerId)
	{
		try
		{
			var player = await _players.GetByIdAsync(playerId);
			if (player == null)
			{
				_outbound.Send(peerId, new { type = "dev_player_data", ok = false });
				return;
			}

			bool isOnline = _state.Positions.ContainsKey(playerId);

			// Tile: prefer live position when online, else DB.
			int tileX = 0, tileY = 0, tileZ = 0;
			if (isOnline && _state.Positions.TryGetValue(playerId, out var livePos))
			{
				tileX = (int)MathF.Round(livePos.X);
				tileY = (int)MathF.Round(livePos.Z);
				tileZ = (int)MathF.Round(livePos.Y);
			}
			else
			{
				var dbTile = await _players.GetTileAsync(playerId);
				if (dbTile.HasValue)
				{
					tileX = dbTile.Value.X;
					tileY = dbTile.Value.Y;
					tileZ = dbTile.Value.Z;
				}
			}

			// Combat + skills: prefer in-memory when online.
			PlayerCombatData? combat;
			List<SkillLevel> skills;
			if (isOnline
				&& _state.PlayerCombat.TryGetValue(playerId, out var liveCombat)
				&& _state.SkillsByPlayer.TryGetValue(playerId, out var liveSkills))
			{
				combat = liveCombat;
				skills = liveSkills;
			}
			else
			{
				combat = await _stats.GetCombatAsync(playerId);
				skills = await _stats.GetSkillsAsync(playerId);
			}

			// Credits: prefer in-memory when online.
			int credits;
			if (isOnline && _state.Credits.TryGetValue(playerId, out int liveCredits))
				credits = liveCredits;
			else
				credits = await _players.GetCreditsAsync(playerId);

			// Inventory: prefer in-memory when online.
			long[] inventory;
			if (isOnline && _state.Inventories.TryGetValue(playerId, out var liveInv))
				inventory = liveInv;
			else
				inventory = await _inventory.GetSlotsAsync(playerId) ?? new long[28];

			// Bank: read from DB (bank lives in BankSystem, not ServerState).
			var bankData = await _bank.GetAsync(playerId);
			string bankJson = bankData?.Json ?? "[]";
			int bankCapacity = bankData?.Capacity ?? 100;
			var bankEntries = ParseBankSlots(bankJson);

			int totalLevel = 0;
			foreach (var sk in skills) totalLevel += sk.Max;
			int finalLevel = combat?.FinalLevel ?? 1;

			var inventoryList = new List<object>();
			for (int i = 0; i < inventory.Length; i++)
			{
				long packed = inventory[i];
				if (packed == 0) continue;
				int itemId = ItemStack.ItemId(packed);
				int qty = ItemStack.Quantity(packed);
				if (!_items.TryGetById(itemId, out var meta)) continue;
				inventoryList.Add(new
				{
					slot = i,
					item_id = itemId,
					slug = meta.Slug,
					display = meta.Display,
					quantity = qty,
					cert_of_slug = meta.CertOfSlug,
					stackable = meta.Stackable,
				});
			}

			var bankList = new List<object>();
			foreach (var entry in bankEntries)
			{
				if (entry == null) continue;
				if (!_items.TryGetById(entry.ItemId, out var meta)) continue;
				bankList.Add(new
				{
					item_id = entry.ItemId,
					quantity = entry.Quantity,
					display = meta.Display,
					cert_of_slug = meta.CertOfSlug,
					stackable = meta.Stackable,
				});
			}

			var skillsList = new List<object>(skills.Count);
			foreach (var sk in skills)
			{
				skillsList.Add(new
				{
					id = sk.Id,
					level = sk.Level,
					max = sk.Max,
					current_xp = SkillExperience.GetDisplayedXp(sk.CurrentXpTenths),
					current_xp_tenths = sk.CurrentXpTenths,
				});
			}

			_outbound.Send(peerId, new
			{
				type = "dev_player_data",
				ok = true,
				data = new
				{
					id = player.Id,
					username = player.Username,
					is_admin = player.IsAdmin,
					is_online = isOnline,
					tile_x = tileX,
					tile_y = tileY,
					tile_z = tileZ,
					final_level = finalLevel,
					total_level = totalLevel,
					credits = credits,
					bank_capacity = bankCapacity,
					inventory = inventoryList,
					bank = bankList,
					skills = skillsList,
				},
			});
		}
		catch (Exception e)
		{
			GD.PrintErr($"[PlayerSystem] GetAsync: {e.Message}");
			_outbound.Send(peerId, new { type = "dev_player_data", ok = false });
		}
	}

	private sealed class BankEntry
	{
		public int ItemId { get; init; }
		public int Quantity { get; init; }
	}

	private static List<BankEntry?> ParseBankSlots(string json)
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
			GD.PrintErr($"[PlayerSystem] ParseBankSlots: {e.Message}");
		}
		return result;
	}

	// ============================================================
	// dev_player_set_admin
	// ============================================================

	internal void HandleDevPlayerSetAdmin(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		if (!root.TryGetProperty("player_id", out var idEl)
			|| !idEl.TryGetInt64(out long playerId)) return;
		if (!root.TryGetProperty("is_admin", out var admEl)) return;
		bool isAdmin = admEl.ValueKind == JsonValueKind.True;

		_ = SetAdminAsync(peerId, playerId, isAdmin);
	}

	private async System.Threading.Tasks.Task SetAdminAsync(long peerId, long playerId, bool isAdmin)
	{
		try
		{
			bool ok = _players.SetAdminSync(playerId, isAdmin);
			if (ok && _state.PeerByPlayerId.TryGetValue(playerId, out long targetPeer)
				&& _state.Sessions.TryGetValue(targetPeer, out var targetSession))
			{
				_state.Sessions[targetPeer] = targetSession with { IsAdmin = isAdmin };
			}
			_outbound.Send(peerId, new
			{
				type = "dev_player_admin_updated",
				ok,
				player_id = playerId,
				is_admin = isAdmin,
			});
		}
		catch (Exception e)
		{
			GD.PrintErr($"[PlayerSystem] SetAdminAsync: {e.Message}");
			_outbound.Send(peerId, new
			{
				type = "dev_player_admin_updated",
				ok = false,
				player_id = playerId,
				is_admin = isAdmin,
			});
		}
	}

	// ============================================================
	// dev_player_update_level
	// ============================================================

	internal void HandleDevPlayerUpdateLevel(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
		if (!session.IsAdmin) return;
		if (!root.TryGetProperty("player_id", out var idEl)
			|| !idEl.TryGetInt64(out long playerId)) return;
		if (!root.TryGetProperty("skill_id", out var skEl)
			|| skEl.ValueKind != JsonValueKind.String) return;
		string skillId = skEl.GetString() ?? "";
		if (!PlayerStatsRepository.SkillIds.Contains(skillId)) return;
		if (!root.TryGetProperty("new_level", out var lvEl)
			|| !lvEl.TryGetInt32(out int newLevel)) return;
		if (newLevel < 1 || newLevel > SkillExperience.MaxLevel) return;

		_ = UpdateLevelAsync(peerId, playerId, skillId, newLevel);
	}

	private async System.Threading.Tasks.Task UpdateLevelAsync(long peerId, long playerId,
		string skillId, int newLevel)
	{
		try
		{
			// Set level columns AND xp column to match. Without the XP sync,
			// the next XP gain would call GetLevelForXpTenths and silently
			// revert the admin's override back to the pre-edit level.
			long newXpTenths = SkillExperience.GetXpForLevelTenths(newLevel);
			await _stats.SetSkillLevelAndXpAsync(playerId, skillId, newLevel, newXpTenths);

			if (_state.SkillsByPlayer.TryGetValue(playerId, out var skills))
			{
				int idx = skills.FindIndex(s => s.Id == skillId);
				if (idx >= 0)
				{
					skills[idx] = skills[idx] with
					{
						Level = newLevel,
						Max = newLevel,
						CurrentXpTenths = newXpTenths,
					};
				}
			}

			// Hitpoints level affects max HP; keep PlayerCombat in sync.
			if (skillId == "hitpoints"
				&& _state.PlayerCombat.TryGetValue(playerId, out var combat))
			{
				int newHp = Math.Min(combat.CurrentHp, newLevel);
				_state.PlayerCombat[playerId] = combat with
				{
					MaxHp = newLevel,
					CurrentHp = newHp,
				};
				_outbound.BroadcastPlayerHp(playerId, newHp, newLevel);
			}

			if (_state.PeerByPlayerId.TryGetValue(playerId, out long targetPeer))
				_outbound.SendSkills(targetPeer);

			_outbound.Send(peerId, new
			{
				type = "dev_player_level_updated",
				ok = true,
				player_id = playerId,
				skill_id = skillId,
				new_level = newLevel,
				current_xp = SkillExperience.GetDisplayedXp(newXpTenths),
			});
		}
		catch (Exception e)
		{
			GD.PrintErr($"[PlayerSystem] UpdateLevelAsync: {e.Message}");
			_outbound.Send(peerId, new
			{
				type = "dev_player_level_updated",
				ok = false,
				player_id = playerId,
				skill_id = skillId,
				new_level = newLevel,
			});
		}
	}

	// ============================================================
	// Teleport variants
	// ============================================================

	internal void HandleDevPlayerTeleport(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var admin)) return;
		if (!admin.IsAdmin) return;
		if (!root.TryGetProperty("player_id", out var idEl)
			|| !idEl.TryGetInt64(out long targetId)) return;
		if (!root.TryGetProperty("x", out var xEl) || !xEl.TryGetInt32(out int tx)) return;
		if (!root.TryGetProperty("y", out var yEl) || !yEl.TryGetInt32(out int ty)) return;

		if (!TeleportPlayerTo(targetId, peerId, tx, ty)) return;

		string targetName = GetPlayerUsername(targetId);
		_outbound.ChatToPlayer(admin.PlayerId,
			$"You teleported {targetName} to tile ({tx}, {ty}).");
		if (targetId != admin.PlayerId)
			_outbound.ChatToPlayer(targetId, "You have been teleported by an admin.");
	}

	internal void HandleDevPlayerTeleportToAdmin(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var admin)) return;
		if (!admin.IsAdmin) return;
		if (!root.TryGetProperty("player_id", out var idEl)
			|| !idEl.TryGetInt64(out long targetId)) return;

		if (!_state.Positions.TryGetValue(admin.PlayerId, out var adminPos))
		{
			_outbound.Send(peerId, new
			{
				type = "dev_player_teleport_result",
				ok = false,
				player_id = targetId,
				error = "admin_position_unknown",
			});
			return;
		}

		int tx = (int)MathF.Round(adminPos.X);
		int ty = (int)MathF.Round(adminPos.Z);
		if (!TeleportPlayerTo(targetId, peerId, tx, ty)) return;

		string targetName = GetPlayerUsername(targetId);
		_outbound.ChatToPlayer(admin.PlayerId,
			$"You teleported {targetName} to you.");
		if (targetId != admin.PlayerId)
			_outbound.ChatToPlayer(targetId, "You have been teleported by an admin.");
	}

	internal void HandleDevPlayerTeleportAdminTo(long peerId, JsonElement root)
	{
		if (!_state.Sessions.TryGetValue(peerId, out var admin)) return;
		if (!admin.IsAdmin) return;
		if (!root.TryGetProperty("player_id", out var idEl)
			|| !idEl.TryGetInt64(out long targetId)) return;

		if (!_state.Positions.TryGetValue(targetId, out var targetPos))
		{
			_outbound.Send(peerId, new
			{
				type = "dev_player_teleport_result",
				ok = false,
				player_id = targetId,
				error = "player_offline",
			});
			return;
		}

		int tx = (int)MathF.Round(targetPos.X);
		int ty = (int)MathF.Round(targetPos.Z);
		if (!TeleportPlayerTo(admin.PlayerId, peerId, tx, ty)) return;

		string targetName = GetPlayerUsername(targetId);
		_outbound.ChatToPlayer(admin.PlayerId,
			$"You teleported to {targetName}.");
	}

	private bool TeleportPlayerTo(long playerId, long requesterPeerId, int tileX, int tileY)
	{
		if (!_state.Positions.ContainsKey(playerId))
		{
			_outbound.Send(requesterPeerId, new
			{
				type = "dev_player_teleport_result",
				ok = false,
				player_id = playerId,
				error = "player_offline",
			});
			return false;
		}

		if (!_world.IsWalkable(tileX, tileY))
		{
			_outbound.Send(requesterPeerId, new
			{
				type = "dev_player_teleport_result",
				ok = false,
				player_id = playerId,
				error = "tile_not_walkable",
			});
			return false;
		}

		var info = _world.GetTileInfo(tileX, tileY);
		var newPos = new PlayerPosition(tileX, info.Z, tileY, 0f);
		_state.Positions[playerId] = newPos;

		// Cancel any in-flight walk intent so the player doesn't drift back.
		if (_state.PeerByPlayerId.TryGetValue(playerId, out long targetPeer))
			_state.Intents.Remove(targetPeer);

		// Persist tile so next login spawns at the new spot.
		_ = _players.SetTileAsync(playerId, tileX, tileY, info.Z);

		// Dedicated teleport message — client snaps instead of interpolating.
		_outbound.Broadcast(new
		{
			type = "player_teleported",
			player_id = playerId,
			x = newPos.X,
			y = newPos.Y,
			z = newPos.Z,
			yaw = newPos.Yaw,
		});

		_outbound.Send(requesterPeerId, new
		{
			type = "dev_player_teleport_result",
			ok = true,
			player_id = playerId,
			tile_x = tileX,
			tile_y = tileY,
			tile_z = info.Z,
		});

		return true;
	}

	private string GetPlayerUsername(long playerId)
	{
		if (_state.PeerByPlayerId.TryGetValue(playerId, out long peerId)
			&& _state.Sessions.TryGetValue(peerId, out var sess))
			return sess.Username;
		return $"player {playerId}";
	}
}