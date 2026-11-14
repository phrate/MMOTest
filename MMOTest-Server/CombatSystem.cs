using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns melee combat: pursuit, engagement, swings, damage, XP, death/respawn,
/// NPC hostility, NPC and player healing.
///
/// Coupling note (temporary): holds Main for NPC lifecycle (KillNpc, MoveNpcTo,
/// NotifyNpcVisibilityChanged), item drops (GivePlayerCredits, SpawnGroundItemAt),
/// and spawn helpers (ComputeSpawnPosition). These extract into NpcSystem (3b-C)
/// and ItemSystem (3b-D); after that CombatSystem takes those systems directly
/// and _main drops out.
/// </summary>
internal sealed class CombatSystem
{
    private const int PlayerAttackSpeedTicks = 10;
    private const int AttackAnimDurationMs = 1400;
    private const int NpcLeashRadiusTiles = 15;

    private readonly Main _main;
    private readonly ServerState _state;
    private readonly WorldService _world;
    private readonly OutboundChannel _outbound;
    private readonly Coordinator _coordinator;
    private readonly MovementSystem _movement;
    private readonly PlayerStatsRepository _stats;
    private readonly Gfs.PerTickManager _perTick;
    private readonly Random _combatRng;

    private readonly Dictionary<long, int> _npcCombatRefCount = new();
    private readonly Dictionary<long, int> _playerHealAccumTicks = new();

    public CombatSystem(Main main, ServerState state, WorldService world,
                        OutboundChannel outbound, Coordinator coordinator,
                        MovementSystem movement, PlayerStatsRepository stats,
                        Gfs.PerTickManager perTick, Random combatRng)
    {
        _main = main;
        _state = state;
        _world = world;
        _outbound = outbound;
        _coordinator = coordinator;
        _movement = movement;
        _stats = stats;
        _perTick = perTick;
        _combatRng = combatRng;
    }

    internal void ClearPlayer(long playerId)
    {
        if (_state.CombatByPlayer.TryGetValue(playerId, out var pair))
            EndCombat(pair);
        _playerHealAccumTicks.Remove(playerId);
        _state.PendingEngage.Remove(playerId);
    }

    private static bool CanSwingTick(long lastSwingTick, int attackSpeedTicks, long currentTick)
        => currentTick - lastSwingTick >= attackSpeedTicks;

    // ============================================================
    // Entry: request engagement (from GFS combat_start_npc)
    // ============================================================

    internal void RequestEngage(long playerId, long npcId)
    {
        if (!_main.TryGetNpcById(npcId, out var npc)) return;
        if (npc.IsDead) return;
        if (!npc.Attackable) return;

        if (_state.CombatByPlayer.TryGetValue(playerId, out var existing))
        {
            if (existing.NpcId == npcId) return;
            return;
        }

        if (_state.CombatByNpc.ContainsKey(npcId))
        {
            if (_state.PendingEngage.TryGetValue(playerId, out var pendingNpc) && pendingNpc == npcId)
                _state.PendingEngage.Remove(playerId);
            _outbound.ChatToPlayer(playerId, "That's already in a fight, I cannot engage in attack.");
            Diag.Log("diag_combat", $"Player {playerId} tried to engage already-engaged NPC {npc.Name} (id {npcId}); rejected");
            return;
        }

        _coordinator.OnCombatStarted(playerId);

        if (_movement.ArePositionsAdjacent(playerId, npc))
        {
            _state.PendingEngage.Remove(playerId);
            EngageNpc(playerId, npcId);
            return;
        }

        _state.PendingEngage[playerId] = npcId;

        _perTick?.EndByStem(playerId, "follow_player");
        _movement.RemoveIntentForPlayer(playerId);
        _movement.MovePlayerAdjacentToNpc(playerId, npc);
        Diag.Log("diag_combat", $"Player {playerId} approaching NPC {npc.Name} (id {npcId})");
    }

    internal void EngageNpc(long playerId, long npcId, bool initiatedByPlayer = true)
    {
        if (!_main.TryGetNpcById(npcId, out var npc)) return;
        if (npc.IsDead) return;

        if (_state.CombatByPlayer.TryGetValue(playerId, out var existing))
        {
            if (existing.NpcId == npcId) return;
            return;
        }

        if (_state.CombatByNpc.ContainsKey(npcId))
        {
            if (initiatedByPlayer)
                _outbound.ChatToPlayer(playerId, "That's already in a fight, I cannot engage in attack.");
            Diag.Log("diag_combat", $"Player {playerId} tried to engage already-engaged NPC {npc.Name} (id {npcId}) at promotion; rejected");
            return;
        }

        _coordinator.OnCombatStarted(playerId);

        var pair = new CombatPair
        {
            PlayerId = playerId,
            NpcId = npcId,
            LastPlayerSwingTick = _state.CurrentTick - PlayerAttackSpeedTicks,
            LastNpcSwingTick = initiatedByPlayer
                ? _state.CurrentTick
                : _state.CurrentTick - npc.AttackSpeedTicks,
            StateAnnounced = false,
            InitiatedByPlayer = initiatedByPlayer,
        };
        _state.CombatByPlayer[playerId] = pair;
        if (!_state.CombatByNpc.ContainsKey(npcId)) _state.CombatByNpc[npcId] = pair;

        _perTick?.EndByStem(playerId, "follow_player");

        Diag.Log("diag_combat", $"Player {playerId} engaged NPC {npc.Name} (id {npcId}) initiatedByPlayer={initiatedByPlayer}");
    }

    internal void EndCombat(CombatPair pair)
    {
        _state.CombatByPlayer.Remove(pair.PlayerId);
        bool wasOwner = _state.CombatByNpc.TryGetValue(pair.NpcId, out var owner) && owner == pair;
        if (wasOwner) _state.CombatByNpc.Remove(pair.NpcId);

        if (pair.StateAnnounced)
        {
            int prevRef = _npcCombatRefCount.TryGetValue(pair.NpcId, out var pc) ? pc : 0;
            int newRef = Math.Max(0, prevRef - 1);
            if (newRef == 0) _npcCombatRefCount.Remove(pair.NpcId);
            else _npcCombatRefCount[pair.NpcId] = newRef;
            if (prevRef > 0 && newRef == 0)
                _outbound.Broadcast(new { type = "npc_combat_state", npc_id = pair.NpcId, in_combat = false });
            _outbound.Broadcast(new
            {
                type = "player_combat_state",
                player_id = pair.PlayerId,
                in_combat = false,
                npc_id = 0
            });
        }

        Diag.Log("diag_combat", $"Combat ended: player {pair.PlayerId}, npc {pair.NpcId}");
    }

    // ============================================================
    // Per-tick combat phases
    // ============================================================

    internal void ProcessPendingEngage()
    {
        if (_state.PendingEngage.Count == 0) return;

        var toRemove = new List<long>();
        var toEngage = new List<(long playerId, long npcId)>();

        foreach (var kv in _state.PendingEngage)
        {
            long playerId = kv.Key;
            long npcId = kv.Value;

            if (!_main.TryGetNpcById(npcId, out var npc) || npc.IsDead)
            { toRemove.Add(playerId); continue; }
            if (!_state.Positions.ContainsKey(playerId))
            { toRemove.Add(playerId); continue; }
            if (_movement.ArePositionsAdjacent(playerId, npc))
            {
                toRemove.Add(playerId);
                toEngage.Add((playerId, npcId));
                continue;
            }
            if (!_movement.MovePlayerAdjacentToNpc(playerId, npc))
            {
                toRemove.Add(playerId);
                _outbound.ChatToPlayer(playerId, "I can't seem to reach that.");
            }
        }

        foreach (var p in toRemove) _state.PendingEngage.Remove(p);
        foreach (var (pid, nid) in toEngage) EngageNpc(pid, nid);
    }

    internal void ProcessHostility()
    {
        foreach (var npc in _main.AllNpcs())
        {
            if (!npc.Aggressive) continue;
            if (npc.IsDead) continue;
            if (_state.CombatByNpc.ContainsKey(npc.Id)) continue;
            if (npc.ReturningHome) continue;
            if (DistanceFromSpawn(npc) > NpcLeashRadiusTiles) continue;

            int nx = (int)MathF.Round(npc.PosX);
            int ny = (int)MathF.Round(npc.PosZ);

            long bestPlayer = 0;
            int bestDist = int.MaxValue;
            int levelCap = npc.FinalLevel * 2;

            foreach (var kv in _state.Sessions)
            {
                long playerId = kv.Value.PlayerId;
                if (!_state.Positions.ContainsKey(playerId)) continue;
                if (_state.CombatByPlayer.ContainsKey(playerId)) continue;
                if (!_state.PlayerCombat.TryGetValue(playerId, out var pdata)) continue;
                if (pdata.FinalLevel < 1 || pdata.FinalLevel > levelCap) continue;
                if (pdata.CurrentHp <= 0) continue;

                var ppos = _state.Positions[playerId];
                int px = (int)MathF.Round(ppos.X);
                int py = (int)MathF.Round(ppos.Z);
                int dist = Math.Max(Math.Abs(px - nx), Math.Abs(py - ny));
                if (dist > npc.AggroRangeTiles) continue;
                if (dist < bestDist) { bestDist = dist; bestPlayer = playerId; }
            }

            if (bestPlayer != 0)
            {
                Diag.Log("diag_combat", $"Hostile NPC {npc.Name} (id {npc.Id}) aggroed on player {bestPlayer} (dist {bestDist})");
                EngageNpc(bestPlayer, npc.Id, initiatedByPlayer: false);
            }
        }
    }

    internal void ProcessCombat()
    {
        if (_state.CombatByPlayer.Count == 0) return;
        var toEnd = new List<CombatPair>();

        foreach (var pair in _state.CombatByPlayer.Values)
        {
            if (!_main.TryGetNpcById(pair.NpcId, out var npc)) { toEnd.Add(pair); continue; }
            if (npc.IsDead) { toEnd.Add(pair); continue; }
            if (!_state.Positions.ContainsKey(pair.PlayerId)) { toEnd.Add(pair); continue; }
            if (!_state.PlayerCombat.ContainsKey(pair.PlayerId)) { toEnd.Add(pair); continue; }

            if (!_movement.ArePositionsAdjacent(pair.PlayerId, npc))
            {
                if (pair.InitiatedByPlayer && !pair.StateAnnounced)
                {
                    _movement.MovePlayerAdjacentToNpc(pair.PlayerId, npc);
                    continue;
                }

                if (DistanceFromSpawn(npc) > NpcLeashRadiusTiles)
                {
                    EndCombat(pair);
                    npc.ReturningHome = true;
                    npc.Path.Clear();
                    _main.MoveNpcTo(npc.Id, npc.TileX, npc.TileY);
                    Diag.Log("diag_combat", $"NPC {npc.Name} (id {npc.Id}) leash broken");
                    continue;
                }

                if (_state.Positions.TryGetValue(pair.PlayerId, out var chasePos))
                {
                    var playerTile = new Vector2I(
                        (int)MathF.Round(chasePos.X),
                        (int)MathF.Round(chasePos.Z));
                    if (pair.LastChaseTarget != playerTile)
                    {
                        pair.LastChaseTarget = playerTile;
                        int nx = (int)MathF.Round(npc.PosX);
                        int ny = (int)MathF.Round(npc.PosZ);
                        if (nx == playerTile.X && ny == playerTile.Y)
                        {
                            Vector2I[] dirs = { new(1,0), new(-1,0), new(0,1), new(0,-1),
                                                new(1,1), new(1,-1), new(-1,1), new(-1,-1) };
                            foreach (var d in dirs)
                            {
                                var cand = playerTile + d;
                                if (_world.IsWalkable(cand.X, cand.Y))
                                {
                                    npc.Path = new List<Vector2I> { cand };
                                    break;
                                }
                            }
                        }
                        else
                        {
                            var route = _main.PathfindBetween(nx, ny, playerTile.X, playerTile.Y);
                            if (route.Count > 0)
                            {
                                if (route[^1] == playerTile) route.RemoveAt(route.Count - 1);
                                if (route.Count > 0) npc.Path = route;
                            }
                        }
                    }
                }
                continue;
            }

            if (!pair.StateAnnounced)
            {
                pair.StateAnnounced = true;
                int prevRef = _npcCombatRefCount.TryGetValue(npc.Id, out var pc) ? pc : 0;
                _npcCombatRefCount[npc.Id] = prevRef + 1;
                if (prevRef == 0)
                    _outbound.Broadcast(new { type = "npc_combat_state", npc_id = npc.Id, in_combat = true });
                _outbound.Broadcast(new
                {
                    type = "player_combat_state",
                    player_id = pair.PlayerId,
                    in_combat = true,
                    npc_id = npc.Id
                });
            }

            if (pair.StateAnnounced && _state.Positions.TryGetValue(pair.PlayerId, out var facingPos))
            {
                int ptx = (int)MathF.Round(facingPos.X);
                int pty = (int)MathF.Round(facingPos.Z);
                int ntx = (int)MathF.Round(npc.PosX);
                int nty = (int)MathF.Round(npc.PosZ);
                float nYaw = MathF.Atan2(-(ptx - ntx), -(pty - nty));
                nYaw = MathF.Round(nYaw / (MathF.PI / 4f)) * (MathF.PI / 4f);

                if (!_movement.IsMoving(pair.PlayerId))
                {
                    float pYaw = MathF.Atan2(-(ntx - ptx), -(nty - pty));
                    pYaw = MathF.Round(pYaw / (MathF.PI / 4f)) * (MathF.PI / 4f);
                    if (MathF.Abs(pYaw - facingPos.Yaw) > 0.05f)
                    {
                        _state.Positions[pair.PlayerId] = facingPos with { Yaw = pYaw };
                        _outbound.Broadcast(new
                        {
                            type = "tick_positions",
                            tick = _state.CurrentTick,
                            updates = new[] {
                                new { player_id = pair.PlayerId, x = facingPos.X, y = facingPos.Y, z = facingPos.Z, yaw = pYaw }
                            }
                        }, channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
                    }
                }

                if (MathF.Abs(nYaw - npc.Yaw) > 0.05f)
                {
                    npc.Yaw = nYaw;
                    npc.LastSentYaw = nYaw;
                    _outbound.Broadcast(new
                    {
                        type = "npc_tick_positions",
                        updates = new[] {
                            new { npc_id = npc.Id, x = npc.PosX, y = npc.PosY, z = npc.PosZ, yaw = nYaw }
                        }
                    }, channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
                }
            }

            if (!_movement.IsMoving(pair.PlayerId)
                && CanSwingTick(pair.LastPlayerSwingTick, PlayerAttackSpeedTicks, _state.CurrentTick))
            {
                ResolvePlayerAttack(pair, npc);
                pair.LastPlayerSwingTick = _state.CurrentTick;
                if (npc.IsDead || !_state.PlayerCombat.ContainsKey(pair.PlayerId)) { toEnd.Add(pair); continue; }
            }

            if (_state.CombatByNpc.TryGetValue(pair.NpcId, out var npcOwner) && npcOwner == pair)
            {
                if (CanSwingTick(pair.LastNpcSwingTick, npc.AttackSpeedTicks, _state.CurrentTick))
                {
                    ResolveNpcAttack(pair, npc);
                    pair.LastNpcSwingTick = _state.CurrentTick;
                    if (!_state.PlayerCombat.TryGetValue(pair.PlayerId, out var pd) || pd.CurrentHp <= 0)
                    {
                        HandlePlayerDeath(pair.PlayerId);
                        toEnd.Add(pair);
                        continue;
                    }
                }
            }
        }

        foreach (var pair in toEnd) EndCombat(pair);
    }

    internal void ProcessNpcHealing()
    {
        const int IN_COMBAT_HEAL_TICKS = 268;
        const int OUT_COMBAT_HEAL_TICKS = 27;
        const int IN_COMBAT_HEAL_PCT = 10;
        const int OUT_COMBAT_HEAL_PCT = 25;

        foreach (var npc in _main.AllNpcs())
        {
            if (npc.IsDead) continue;
            if (npc.Hitpoints >= npc.MaxHitpoints) continue;

            npc.HealAccumulatorTicks++;
            bool inCombat = _state.CombatByNpc.ContainsKey(npc.Id);
            int threshold = inCombat ? IN_COMBAT_HEAL_TICKS : OUT_COMBAT_HEAL_TICKS;
            int pct = inCombat ? IN_COMBAT_HEAL_PCT : OUT_COMBAT_HEAL_PCT;

            if (npc.HealAccumulatorTicks < threshold) continue;

            npc.HealAccumulatorTicks = 0;
            int heal = Math.Max(1, (npc.MaxHitpoints * pct) / 100);
            int newHp = Math.Min(npc.MaxHitpoints, npc.Hitpoints + heal);
            if (newHp == npc.Hitpoints) continue;

            npc.Hitpoints = newHp;
            _ = _stats.SetNpcHpAsync(npc.Id, newHp);
            _outbound.BroadcastNpcHp(npc.Id, newHp, npc.MaxHitpoints);
            Diag.Log("diag_combat", $"NPC {npc.Name} healed to {newHp}/{npc.MaxHitpoints} ({(inCombat ? "in combat" : "out of combat")})");
        }
    }

    internal void ProcessPlayerHealing()
    {
        const int HEAL_INTERVAL_TICKS = 83;
        const int HEAL_PCT = 10;

        foreach (var kv in _state.PlayerCombat)
        {
            long playerId = kv.Key;
            var data = kv.Value;
            if (data.CurrentHp >= data.MaxHp) { _playerHealAccumTicks[playerId] = 0; continue; }
            if (!_state.Positions.ContainsKey(playerId)) continue;

            int accum = (_playerHealAccumTicks.TryGetValue(playerId, out var a) ? a : 0) + 1;
            if (accum < HEAL_INTERVAL_TICKS)
            {
                _playerHealAccumTicks[playerId] = accum;
                continue;
            }

            _playerHealAccumTicks[playerId] = 0;
            int heal = Math.Max(1, (data.MaxHp * HEAL_PCT) / 100);
            int newHp = Math.Min(data.MaxHp, data.CurrentHp + heal);
            if (newHp == data.CurrentHp) continue;

            _state.PlayerCombat[playerId] = data with { CurrentHp = newHp };
            _ = _stats.SetPlayerHpAsync(playerId, newHp);
            _outbound.BroadcastPlayerHp(playerId, newHp, data.MaxHp);
            Diag.Log("diag_combat", $"Player {playerId} regen to {newHp}/{data.MaxHp}");
        }
    }

    // ============================================================
    // Attack resolution
    // ============================================================

    private void ResolvePlayerAttack(CombatPair pair, ActiveNpc npc)
    {
        if (!_state.PlayerCombat.TryGetValue(pair.PlayerId, out var stats)) return;
        _outbound.BroadcastPlayerAnim(pair.PlayerId, "attack", AttackAnimDurationMs);

        bool hit = CombatRolls.RollHit(stats.Accuracy, stats.MeleeBonus, npc.Defence, npc.MeleeDefense, _combatRng);
        int damage = hit ? CombatRolls.RollDamage(stats.Strength, stats.MeleeBonus, _combatRng) : 0;

        if (damage > 0)
        {
            AwardCombatExperience(pair.PlayerId, damage);
            npc.Hitpoints = Math.Max(0, npc.Hitpoints - damage);
            _ = _stats.SetNpcHpAsync(npc.Id, npc.Hitpoints);
            _outbound.BroadcastNpcHp(npc.Id, npc.Hitpoints, npc.MaxHitpoints);
        }

        _outbound.Broadcast(new { type = "damage_number", target_type = "npc", target_id = npc.Id, amount = damage });

        if (npc.Hitpoints <= 0)
        {
            _main.KillNpc(npc.Id);
            return;
        }
        if (!_state.CombatByNpc.ContainsKey(npc.Id)) _state.CombatByNpc[npc.Id] = pair;
    }

    private void AwardCombatExperience(long playerId, int damage)
    {
        if (damage <= 0 || !_state.SkillsByPlayer.TryGetValue(playerId, out var skills)) return;

        int accuracyIndex = skills.FindIndex(skill => skill.Id == "accuracy");
        int hitpointsIndex = skills.FindIndex(skill => skill.Id == "hitpoints");
        if (accuracyIndex < 0 || hitpointsIndex < 0) return;

        var accuracy = skills[accuracyIndex];
        var hitpoints = skills[hitpointsIndex];
        long accuracyXpTenths = accuracy.CurrentXpTenths + (long)damage * SkillExperience.AccuracyXpTenthsPerDamage;
        long hitpointsXpTenths = hitpoints.CurrentXpTenths + (long)damage * SkillExperience.HitpointsXpTenthsPerDamage;
        int accuracyLevel = Math.Max(accuracy.Max, SkillExperience.GetLevelForXpTenths(accuracyXpTenths));
        int hitpointsMax = Math.Max(hitpoints.Max, SkillExperience.GetLevelForXpTenths(hitpointsXpTenths));

        skills[accuracyIndex] = accuracy with
        {
            Level = accuracyLevel,
            Max = accuracyLevel,
            CurrentXpTenths = accuracyXpTenths,
        };
        skills[hitpointsIndex] = hitpoints with
        {
            Max = hitpointsMax,
            CurrentXpTenths = hitpointsXpTenths,
        };

        if (_state.PlayerCombat.TryGetValue(playerId, out var combat))
        {
            int newMaxHp = Math.Max(combat.MaxHp, hitpointsMax);
            _state.PlayerCombat[playerId] = combat with
            {
                Accuracy = Math.Max(combat.Accuracy, accuracyLevel),
                MaxHp = newMaxHp,
            };
            if (newMaxHp != combat.MaxHp)
                _outbound.BroadcastPlayerHp(playerId, combat.CurrentHp, newMaxHp);
        }

        _ = _stats.AwardCombatXpAsync(playerId, damage, accuracyLevel, hitpointsMax);
        if (_state.PeerByPlayerId.TryGetValue(playerId, out long peerId))
            _outbound.SendSkills(peerId);
    }

    private void ResolveNpcAttack(CombatPair pair, ActiveNpc npc)
    {
        if (!_state.PlayerCombat.TryGetValue(pair.PlayerId, out var pd)) return;
        _outbound.BroadcastNpcAnim(npc.Id, "attack", AttackAnimDurationMs);

        bool hit = CombatRolls.RollHit(npc.Accuracy, npc.MeleeBonus, pd.Defence, pd.MeleeDefense, _combatRng);
        int damage = hit ? CombatRolls.RollDamage(npc.Strength, npc.MeleeBonus, _combatRng) : 0;

        if (damage > 0)
        {
            int newHp = Math.Max(0, pd.CurrentHp - damage);
            _state.PlayerCombat[pair.PlayerId] = pd with { CurrentHp = newHp };
            _ = _stats.SetPlayerHpAsync(pair.PlayerId, newHp);
            _outbound.BroadcastPlayerHp(pair.PlayerId, newHp, pd.MaxHp);
        }

        _outbound.Broadcast(new { type = "damage_number", target_type = "player", target_id = pair.PlayerId, amount = damage });
    }

    // ============================================================
    // Death
    // ============================================================

    internal void HandlePlayerDeath(long playerId)
    {
        _coordinator.OnPlayerDied(playerId);

        long peerId = -1;
        foreach (var kv in _state.Sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
        if (peerId < 0) return;

        if (_state.CombatByPlayer.TryGetValue(playerId, out var pair)) EndCombat(pair);

        _state.Intents.Remove(peerId);
        _state.PendingEngage.Remove(playerId);

        var spawnPos = _main.ComputeSpawnPosition(playerId);
        _state.Positions[playerId] = spawnPos;

        int hp = 10;
        if (_state.PlayerCombat.TryGetValue(playerId, out var pd))
        {
            _state.PlayerCombat[playerId] = pd with { CurrentHp = pd.MaxHp };
            _ = _stats.SetPlayerHpAsync(playerId, pd.MaxHp);
            hp = pd.MaxHp;
        }
        _playerHealAccumTicks[playerId] = 0;

        _outbound.Send(peerId, new
        {
            type = "player_respawned",
            x = spawnPos.X, y = spawnPos.Y, z = spawnPos.Z, yaw = 0f,
            hitpoints = hp, max_hitpoints = hp
        });
        _outbound.BroadcastPlayerAnim(playerId, "death", 1000);
        Diag.Log("diag_combat", $"Player {playerId} died and respawned at ({spawnPos.X}, {spawnPos.Z})");
    }

    private int DistanceFromSpawn(ActiveNpc npc)
    {
        int dx = (int)MathF.Round(npc.PosX) - npc.TileX;
        int dy = (int)MathF.Round(npc.PosZ) - npc.TileY;
        return Math.Max(Math.Abs(dx), Math.Abs(dy));
    }
}