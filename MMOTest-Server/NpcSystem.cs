using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns NPC lifecycle, visibility, movement, render bundles, look-at queue,
/// and admin editor. Does NOT own combat or loot drops — combat lives in
/// CombatSystem, loot drops in ItemSystem (Phase 3b-D). The one remaining
/// Main-side method that touches NPCs, KillNpc, stays on Main until loot
/// extraction lands.
/// </summary>
internal sealed class NpcSystem
{
    private const double WanderIntervalSeconds = 3.0;

    private readonly ServerState _state;
    private readonly WorldService _world;
    private readonly OutboundChannel _outbound;
    private readonly NpcRepository _npcRepo;
    private readonly Gfs.GfsManager _gfs;
    private readonly NpcRenderManager _npcRender;
    private readonly Pathfinder _pathfinder;
    private readonly bool _diagNpcWander;

    private readonly Dictionary<long, ActiveNpc> _npcs = new();
    private readonly Dictionary<long, HashSet<long>> _visibleNpcs = new();
    private readonly Dictionary<long, (long PlayerId, long Sid)> _npcLookAt = new();
    private double _wanderAccumulator;
    private readonly Random _wanderRng = new();

    public int NpcViewChunkRadius { get; }

    public NpcSystem(ServerState state, WorldService world, OutboundChannel outbound,
                     NpcRepository npcRepo, Gfs.GfsManager gfs, NpcRenderManager npcRender,
                     Pathfinder pathfinder, int npcViewChunkRadius, bool diagNpcWander)
    {
        _state = state;
        _world = world;
        _outbound = outbound;
        _npcRepo = npcRepo;
        _gfs = gfs;
        _npcRender = npcRender;
        _pathfinder = pathfinder;
        NpcViewChunkRadius = npcViewChunkRadius;
        _diagNpcWander = diagNpcWander;
    }

    // ============================================================
    // Loading
    // ============================================================

    internal void LoadAll()
    {
        var npcList = _npcRepo.GetAllSync();
        foreach (var npc in npcList) _npcs[npc.Id] = npc;
        GD.Print($"[Server] Loaded {_npcs.Count} NPC(s) from DB");
    }

    internal IEnumerable<ActiveNpc> All() => _npcs.Values;
    internal ActiveNpc? GetNpcById(long npcId) =>
        _npcs.TryGetValue(npcId, out var n) ? n : null;
    internal bool TryGetNpcById(long npcId, out ActiveNpc npc) =>
        _npcs.TryGetValue(npcId, out npc!);
    internal bool IsNpcInCombat(long npcId) => _state.CombatByNpc.ContainsKey(npcId);

    // ============================================================
    // Movement
    // ============================================================

    internal void TickMovement(long currentTick)
    {
        if (_npcs.Count == 0) return;
        var updates = new List<object>();

        foreach (var npc in _npcs.Values)
        {
            if (npc.IsDead) continue;
            if (npc.ReturningHome && npc.Path.Count == 0 && DistanceFromSpawn(npc) <= 1)
                npc.ReturningHome = false;
            if (npc.Path.Count == 0) continue;

            StepNpcMovement(npc);

            if (MathF.Abs(npc.PosX - npc.LastSentX) > 0.001f ||
                MathF.Abs(npc.PosY - npc.LastSentY) > 0.001f ||
                MathF.Abs(npc.PosZ - npc.LastSentZ) > 0.001f ||
                MathF.Abs(npc.Yaw  - npc.LastSentYaw) > 0.01f)
            {
                npc.LastSentX = npc.PosX;
                npc.LastSentY = npc.PosY;
                npc.LastSentZ = npc.PosZ;
                npc.LastSentYaw = npc.Yaw;
                updates.Add(new
                {
                    npc_id = npc.Id,
                    x = npc.PosX,
                    y = npc.PosY,
                    z = npc.PosZ,
                    yaw = npc.Yaw
                });
            }
        }

        if (updates.Count > 0)
            _outbound.Broadcast(new { type = "npc_tick_positions", updates = updates },
                channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
    }

    internal void TickWander(double delta)
    {
        if (!_diagNpcWander) return;
        _wanderAccumulator += delta;
        if (_wanderAccumulator < WanderIntervalSeconds) return;
        _wanderAccumulator = 0;

        foreach (var npc in _npcs.Values)
        {
            if (npc.IsDead) continue;
            if (npc.Path.Count > 0) continue;
            if (npc.AiPackageState != "wander") continue;
            if (_state.CombatByNpc.ContainsKey(npc.Id)) continue;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int dx = _wanderRng.Next(-3, 4);
                int dy = _wanderRng.Next(-3, 4);
                if (dx == 0 && dy == 0) continue;
                int tx = npc.TileX + dx;
                int ty = npc.TileY + dy;
                if (_world.IsWalkable(tx, ty))
                {
                    MoveNpcTo(npc.Id, tx, ty);
                    break;
                }
            }
        }
    }

    private void StepNpcMovement(ActiveNpc npc)
    {
        float remaining = MovementSystem.WalkSpeed * (float)MovementSystem.TickIntervalSeconds;
        float x = npc.PosX;
        float z = npc.PosZ;
        float yaw = npc.Yaw;

        while (remaining > 0.0001f && npc.Path.Count > 0)
        {
            var target = npc.Path[0];
            float dx = target.X - x;
            float dz = target.Y - z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < 0.0001f) { npc.Path.RemoveAt(0); continue; }

            yaw = MathF.Atan2(-dx, -dz);
            yaw = MathF.Round(yaw / (MathF.PI / 4f)) * (MathF.PI / 4f);

            if (dist <= remaining)
            {
                x = target.X; z = target.Y; remaining -= dist; npc.Path.RemoveAt(0);
            }
            else
            {
                float ratio = remaining / dist;
                x += dx * ratio; z += dz * ratio; remaining = 0;
            }
        }

        int tileX = (int)MathF.Round(x);
        int tileY = (int)MathF.Round(z);
        var tinfo = _world.GetTileInfo(tileX, tileY);
        npc.PosX = x;
        npc.PosY = tinfo.Z;
        npc.PosZ = z;
        npc.Yaw = yaw;
    }

    internal void MoveNpcTo(long npcId, int tileX, int tileY)
    {
        if (!_npcs.TryGetValue(npcId, out var npc)) return;
        if (npc.IsDead) return;
        if (!_world.IsWalkable(tileX, tileY)) return;

        int curX = (int)MathF.Round(npc.PosX);
        int curY = (int)MathF.Round(npc.PosZ);
        if (curX == tileX && curY == tileY) return;

        var path = _pathfinder.FindPath(curX, curY, tileX, tileY);
        if (path.Count == 0) return;
        npc.Path = path;
    }

    private int DistanceFromSpawn(ActiveNpc npc)
    {
        int dx = (int)MathF.Round(npc.PosX) - npc.TileX;
        int dy = (int)MathF.Round(npc.PosZ) - npc.TileY;
        return Math.Max(Math.Abs(dx), Math.Abs(dy));
    }

    // ============================================================
    // Respawn
    // ============================================================

    internal void CheckNpcRespawns()
    {
        long now = (long)Time.GetTicksMsec();
        bool changed = false;
        foreach (var npc in _npcs.Values)
        {
            if (!npc.IsDead) continue;
            if (npc.DiedAtMs <= 0) continue;
            if (now - npc.DiedAtMs >= npc.SpawnTimerSeconds * 1000L)
            {
                Revive(npc);
                changed = true;
            }
        }
        if (changed) NotifyNpcVisibilityChanged();
    }

    private void Revive(ActiveNpc npc)
    {
        npc.IsDead = false;
        npc.DiedAtMs = 0;
        npc.Hitpoints = npc.MaxHitpoints;
        npc.HealAccumulatorTicks = 0;
        npc.ResetPositionToHome();
        npc.Path.Clear();
        _ = _npcRepo.ReviveAsync(npc.Id);
        Diag.Log("diag_npc", $"NPC {npc.Name} (id {npc.Id}) respawned with {npc.Hitpoints} HP");
    }

    // ============================================================
    // Visibility
    // ============================================================

    internal void RefreshNpcVisibility(long peerId, int tileX, int tileY)
    {
        if (!_visibleNpcs.TryGetValue(peerId, out var oldSet))
        {
            oldSet = new HashSet<long>();
            _visibleNpcs[peerId] = oldSet;
        }
        var newSet = ComputeVisibleNpcIds(tileX, tileY);

        var added = new List<object>();
        foreach (var id in newSet)
        {
            if (!oldSet.Contains(id) && _npcs.TryGetValue(id, out var npc))
                added.Add(NpcToPacket(npc));
        }
        if (added.Count > 0)
        {
            _outbound.Send(peerId, new { type = "npc_joined", npcs = added });
            Diag.Log("diag_npc", $"peer {peerId} sees {added.Count} new NPC(s)");
        }

        var removed = new List<long>();
        foreach (var id in oldSet)
            if (!newSet.Contains(id)) removed.Add(id);
        if (removed.Count > 0)
        {
            _outbound.Send(peerId, new { type = "npc_left", ids = removed });
            Diag.Log("diag_npc", $"peer {peerId} lost {removed.Count} NPC(s)");
        }

        _visibleNpcs[peerId] = newSet;
    }

    internal void NotifyNpcVisibilityChanged()
    {
        foreach (long peerId in _state.Sessions.Keys.ToList())
        {
            if (!_state.Sessions.TryGetValue(peerId, out var sess)) continue;
            if (!_state.Positions.TryGetValue(sess.PlayerId, out var pos)) continue;
            int tx = (int)MathF.Round(pos.X);
            int ty = (int)MathF.Round(pos.Z);
            RefreshNpcVisibility(peerId, tx, ty);
        }
    }

    private HashSet<long> ComputeVisibleNpcIds(int tileX, int tileY)
    {
        var visible = new HashSet<long>();
        int pcx = ActiveNpc.FloorDiv(tileX, _world.ChunkSize);
        int pcy = ActiveNpc.FloorDiv(tileY, _world.ChunkSize);
        foreach (var npc in _npcs.Values)
        {
            if (npc.IsDead) continue;
            int ncx = npc.ChunkX(_world.ChunkSize);
            int ncy = npc.ChunkY(_world.ChunkSize);
            if (Math.Abs(pcx - ncx) <= NpcViewChunkRadius &&
                Math.Abs(pcy - ncy) <= NpcViewChunkRadius)
                visible.Add(npc.Id);
        }
        return visible;
    }

    private object NpcToPacket(ActiveNpc npc) => new
    {
        id = npc.Id,
        name = npc.Name,
        display = npc.Display,
        x = npc.PosX,
        y = npc.PosY,
        z = npc.PosZ,
        yaw = npc.Yaw,
        anim = npc.PlayingAnimation,
        attackable = npc.Attackable,
        final_level = npc.FinalLevel,
        render_slug = npc.RenderSlug,
        hitpoints = npc.Hitpoints,
        max_hitpoints = npc.MaxHitpoints,
        has_dialogue = HasDialogueScript(npc),
        dialogue_script = npc.DialogueScript,
        is_banker = npc.IsBanker,
    };

    private bool HasDialogueScript(ActiveNpc npc)
    {
        if (string.IsNullOrEmpty(npc.DialogueScript)) return false;
        string path = npc.DialogueScript;
        if (!path.StartsWith("dialogue/")) path = "dialogue/" + path;
        if (!path.EndsWith(".gfs")) path += ".gfs";
        return _gfs.GetAst(path) != null;
    }

    // ============================================================
    // Look-at queue (snap-and-face on dialogue/bank open)
    // ============================================================

    internal void SnapAndFaceForDialogue(long playerId, ActiveNpc npc)
    {
        if (!_state.Positions.TryGetValue(playerId, out var pos)) return;

        int px = (int)MathF.Round(pos.X);
        int pz = (int)MathF.Round(pos.Z);
        var tinfo = _world.GetTileInfo(px, pz);
        int nx = (int)MathF.Round(npc.PosX);
        int nz = (int)MathF.Round(npc.PosZ);

        float yaw = MathF.Atan2(-(nx - px), -(nz - pz));
        yaw = MathF.Round(yaw / (MathF.PI / 4f)) * (MathF.PI / 4f);

        var newPos = new PlayerPosition(px, tinfo.Z, pz, yaw);
        _state.Positions[playerId] = newPos;

        _outbound.Broadcast(new
        {
            type = "tick_positions",
            tick = _state.CurrentTick,
            updates = new[] {
                new { player_id = playerId, x = newPos.X, y = newPos.Y, z = newPos.Z, yaw = newPos.Yaw }
            }
        }, channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);

        long sid = 0;
        if (_state.PeerByPlayerId.TryGetValue(playerId, out var peerId)
            && _state.Sessions.TryGetValue(peerId, out var sess))
            sid = sess.Sid;
        QueueNpcLookAt(npc.Id, playerId, sid);
    }

    private void QueueNpcLookAt(long npcId, long playerId, long sid)
    {
        if (_npcLookAt.TryGetValue(npcId, out var existing) && sid >= existing.Sid) return;
        _npcLookAt[npcId] = (playerId, sid);
    }

    internal void ProcessNpcLookAt()
    {
        if (_npcLookAt.Count == 0) return;
        foreach (var kv in _npcLookAt)
        {
            long npcId = kv.Key;
            long playerId = kv.Value.PlayerId;
            if (!_npcs.TryGetValue(npcId, out var npc)) continue;
            if (npc.IsDead) continue;
            if (_state.CombatByNpc.ContainsKey(npcId)) continue;
            if (!_state.Positions.TryGetValue(playerId, out var ppos)) continue;

            int nx = (int)MathF.Round(npc.PosX);
            int nz = (int)MathF.Round(npc.PosZ);
            int px = (int)MathF.Round(ppos.X);
            int pz = (int)MathF.Round(ppos.Z);
            float yaw = MathF.Atan2(-(px - nx), -(pz - nz));
            yaw = MathF.Round(yaw / (MathF.PI / 4f)) * (MathF.PI / 4f);

            if (MathF.Abs(yaw - npc.Yaw) > 0.05f)
            {
                npc.Yaw = yaw;
                npc.LastSentYaw = yaw;
                _outbound.Broadcast(new
                {
                    type = "npc_tick_positions",
                    updates = new[] {
                        new { npc_id = npcId, x = npc.PosX, y = npc.PosY, z = npc.PosZ, yaw = yaw }
                    }
                }, channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
            }
        }
        _npcLookAt.Clear();
    }

    // ============================================================
    // Render bundles
    // ============================================================

    internal void PollRenderBundles()
    {
        if (_npcRender == null) return;
        if (!_npcRender.Poll()) return;
        _outbound.Broadcast(new { type = "npc_render_updated", slugs = _npcRender.GetManifest() });
        Diag.Log("diag_npc", $"Render bundles updated, notified {_state.Sessions.Count} client(s)");
    }

    internal void HandleNpcRenderInfo(long peerId)
    {
        if (!_state.Sessions.ContainsKey(peerId)) return;
        _outbound.Send(peerId, new
        {
            type = "npc_render_info_response",
            slugs = _npcRender.GetManifest()
        });
    }

    internal void HandleNpcRenderFetch(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.ContainsKey(peerId)) return;
        if (!root.TryGetProperty("slugs", out var slugsEl)
            || slugsEl.ValueKind != System.Text.Json.JsonValueKind.Array) return;

        foreach (var s in slugsEl.EnumerateArray())
        {
            string slug = s.GetString() ?? "";
            if (string.IsNullOrEmpty(slug)) continue;
            var bundle = _npcRender.GetBundle(slug);
            if (bundle == null) continue;

            var filesDict = new Dictionary<string, string>();
            foreach (var f in bundle.Files)
                filesDict[f.RelativePath] = Convert.ToBase64String(f.Bytes);

            _outbound.Send(peerId, new
            {
                type = "npc_render_data",
                slug = bundle.Slug,
                hash = bundle.Hash,
                files = filesDict
            });
            Diag.Log("diag_npc", $"Sent render bundle '{slug}' to peer {peerId} ({bundle.Files.Count} files)");
        }
    }

    // ============================================================
    // Admin NPC editor
    // ============================================================

    internal void OpenNpcEditorForPlayer(long playerId, long npcId)
    {
        long peerId = -1;
        foreach (var kv in _state.Sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
        if (peerId < 0) return;
        if (!_state.Sessions[peerId].IsAdmin)
        {
            _outbound.ChatToPlayer(playerId, "Admin only.");
            return;
        }
        if (!_npcs.ContainsKey(npcId)) return;
        _outbound.Send(peerId, new { type = "npc_editor_open", npc_id = npcId });
    }

    internal void HandleNpcEditorGet(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        if (!root.TryGetProperty("npc_id", out var idEl) || !idEl.TryGetInt64(out long npcId)) return;
        if (!_npcs.TryGetValue(npcId, out var npc)) return;

        _outbound.Send(peerId, new
        {
            type = "npc_editor_data",
            npc = new
            {
                id = npc.Id,
                name = npc.Name,
                display = npc.Display,
                render_slug = npc.RenderSlug,
                attackable = npc.Attackable,
                aggressive = npc.Aggressive,
                aggro_range = npc.AggroRangeTiles,
                ai_package_state = npc.AiPackageState,
                ai_action_state = npc.AiActionState,
                loot_table_name = npc.LootTableName,
                spawn_timer = npc.SpawnTimerSeconds,
                hitpoints = npc.Hitpoints,
                max_hitpoints = npc.MaxHitpoints,
                accuracy = npc.Accuracy,
                strength = npc.Strength,
                defence = npc.Defence,
                ether = npc.Ether,
                max_ether = npc.MaxEther,
                melee_bonus = npc.MeleeBonus,
                ranged_bonus = npc.RangedBonus,
                ether_bonus = npc.EtherBonus,
                melee_defense = npc.MeleeDefense,
                ranged_defense = npc.RangedDefense,
                ether_defense = npc.EtherDefense,
                attack_speed = npc.AttackSpeedTicks,
                final_level = npc.FinalLevel,
            }
        });
    }

    internal void HandleNpcEditorSave(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        if (!root.TryGetProperty("npc_id", out var idEl) || !idEl.TryGetInt64(out long npcId)) return;
        if (!_npcs.TryGetValue(npcId, out var npc)) return;
        if (!root.TryGetProperty("updates", out var upEl)
            || upEl.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        var u = new Dictionary<string, object>();
        foreach (var prop in upEl.EnumerateObject())
        {
            u[prop.Name] = prop.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.String => prop.Value.GetString() ?? "",
                System.Text.Json.JsonValueKind.Number => prop.Value.TryGetInt32(out int iv) ? iv : 0,
                _ => (object)0,
            };
        }

        try
        {
            if (u.ContainsKey("display"))          npc.Display = u["display"].ToString() ?? npc.Display;
            if (u.ContainsKey("attackable"))       npc.Attackable = (bool)u["attackable"];
            if (u.ContainsKey("aggressive"))       npc.Aggressive = (bool)u["aggressive"];
            if (u.ContainsKey("aggro_range"))      npc.AggroRangeTiles = Math.Max(1, Convert.ToInt32(u["aggro_range"]));
            if (u.ContainsKey("ai_package_state")) npc.AiPackageState = u["ai_package_state"].ToString() ?? "idle";
            if (u.ContainsKey("ai_action_state"))  npc.AiActionState = u["ai_action_state"].ToString() ?? "none";
            if (u.ContainsKey("loot_table_name"))  npc.LootTableName = u["loot_table_name"].ToString() ?? "common_loot_1";
            if (u.ContainsKey("spawn_timer"))      npc.SpawnTimerSeconds = Math.Max(1, Convert.ToInt32(u["spawn_timer"]));
            if (u.ContainsKey("hitpoints"))        npc.Hitpoints = Math.Max(0, Convert.ToInt32(u["hitpoints"]));
            if (u.ContainsKey("max_hitpoints"))    npc.MaxHitpoints = Math.Max(1, Convert.ToInt32(u["max_hitpoints"]));
            if (npc.Hitpoints > npc.MaxHitpoints)  npc.MaxHitpoints = npc.Hitpoints;
            if (u.ContainsKey("accuracy"))         npc.Accuracy = Math.Max(1, Convert.ToInt32(u["accuracy"]));
            if (u.ContainsKey("strength"))         npc.Strength = Math.Max(1, Convert.ToInt32(u["strength"]));
            if (u.ContainsKey("defence"))          npc.Defence = Math.Max(1, Convert.ToInt32(u["defence"]));
            if (u.ContainsKey("ether"))            npc.Ether = Math.Max(1, Convert.ToInt32(u["ether"]));
            if (u.ContainsKey("max_ether"))        npc.MaxEther = Math.Max(1, Convert.ToInt32(u["max_ether"]));
            if (npc.Ether > npc.MaxEther)          npc.MaxEther = npc.Ether;
            if (u.ContainsKey("melee_bonus"))      npc.MeleeBonus = Math.Max(0, Convert.ToInt32(u["melee_bonus"]));
            if (u.ContainsKey("ranged_bonus"))     npc.RangedBonus = Math.Max(0, Convert.ToInt32(u["ranged_bonus"]));
            if (u.ContainsKey("ether_bonus"))      npc.EtherBonus = Math.Max(0, Convert.ToInt32(u["ether_bonus"]));
            if (u.ContainsKey("melee_defense"))    npc.MeleeDefense = Math.Max(0, Convert.ToInt32(u["melee_defense"]));
            if (u.ContainsKey("ranged_defense"))   npc.RangedDefense = Math.Max(0, Convert.ToInt32(u["ranged_defense"]));
            if (u.ContainsKey("ether_defense"))    npc.EtherDefense = Math.Max(0, Convert.ToInt32(u["ether_defense"]));
            if (u.ContainsKey("attack_speed"))     npc.AttackSpeedTicks = Math.Max(1, Convert.ToInt32(u["attack_speed"]));

            u["display"]          = npc.Display;
            u["attackable"]       = npc.Attackable;
            u["aggressive"]       = npc.Aggressive;
            u["aggro_range"]      = npc.AggroRangeTiles;
            u["ai_package_state"] = npc.AiPackageState;
            u["ai_action_state"]  = npc.AiActionState;
            u["loot_table_name"]  = npc.LootTableName;
            u["spawn_timer"]      = npc.SpawnTimerSeconds;
            u["hitpoints"]        = npc.Hitpoints;
            u["max_hitpoints"]    = npc.MaxHitpoints;
            u["accuracy"]         = npc.Accuracy;
            u["strength"]         = npc.Strength;
            u["defence"]          = npc.Defence;
            u["ether"]            = npc.Ether;
            u["max_ether"]        = npc.MaxEther;
            u["melee_bonus"]      = npc.MeleeBonus;
            u["ranged_bonus"]     = npc.RangedBonus;
            u["ether_bonus"]      = npc.EtherBonus;
            u["melee_defense"]    = npc.MeleeDefense;
            u["ranged_defense"]   = npc.RangedDefense;
            u["ether_defense"]    = npc.EtherDefense;
            u["attack_speed"]     = npc.AttackSpeedTicks;

            _ = _npcRepo.SaveAdminEditAsync(npcId, u);
            _outbound.BroadcastNpcHp(npc.Id, npc.Hitpoints, npc.MaxHitpoints);
            _outbound.Broadcast(new { type = "npc_updated", npc = NpcToPacket(npc) });
            NotifyNpcVisibilityChanged();
            _outbound.Send(peerId, new { type = "npc_editor_saved", ok = true, npc_id = npcId });
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Server] npc_editor_save: {e.Message}");
            _outbound.Send(peerId, new
            {
                type = "npc_editor_saved",
                ok = false,
                error_key = "npc.save_failed",
                error_params = new { },
            });
        }
    }
}