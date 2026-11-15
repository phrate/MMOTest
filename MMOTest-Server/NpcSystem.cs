using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns NPC lifecycle: loading types + instances, per-tick movement, visibility,
/// respawn, look-at queue, render bundles. Kill/death/loot lives in CombatSystem.
///
/// Phase A state: the NPC editor backend is stubbed. Every editor packet
/// handler returns a "not available" reply. The editor UI is broken until
/// Phase B rebuilds it against the type/instance schema. Runtime systems
/// (movement, visibility, combat-facing queries, render bundle fetch) are
/// fully operational against the seeded Citizen instance.
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
        var types = _npcRepo.GetAllTypesSync();
        var typesById = new Dictionary<long, NpcTypeRecord>();
        foreach (var t in types) typesById[t.Id] = t;

        var instances = _npcRepo.GetAllInstancesSync();
        foreach (var inst in instances)
        {
            if (!typesById.TryGetValue(inst.TypeId, out var type))
            {
                GD.PrintErr($"[Server] NPC instance {inst.Id} references missing type {inst.TypeId}; skipping");
                continue;
            }
            var npc = new ActiveNpc
            {
                Id = inst.Id,
                Type = type,
                NameOverride = inst.NameOverride ?? "",
                TileX = inst.X,
                TileY = inst.Y,
                TileZ = inst.Z,
                PlayingAnimation = inst.Anim,
                IsDead = inst.IsDead,
                IsDying = inst.IsDying,
                DiedAtMs = inst.DiedAtMs,
            };
            npc.ResetToTypeDefaults();
            npc.ResetPositionToHome();
            _npcs[npc.Id] = npc;
        }
        GD.Print($"[Server] Loaded {types.Count} NPC type(s), {_npcs.Count} instance(s)");
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
            if (npc.Type.AiPackageState != "wander") continue;
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
            if (now - npc.DiedAtMs >= npc.Type.SpawnTimerSeconds * 1000L)
            {
                Revive(npc);
                changed = true;
            }
        }
        if (changed) NotifyNpcVisibilityChanged();
    }

    internal Task MarkDeadAsync(long instanceId) => _npcRepo.MarkDeadAsync(instanceId);

    private void Revive(ActiveNpc npc)
    {
        npc.IsDead = false;
        npc.IsDying = false;
        npc.DiedAtMs = 0;
        npc.ResetToTypeDefaults();
        npc.ResetPositionToHome();
        npc.Path.Clear();
        _ = _npcRepo.ReviveAsync(npc.Id);
        Diag.Log("diag_npc", $"NPC {npc.Display} (id {npc.Id}) respawned with {npc.Hitpoints} HP");
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
        slug_id = npc.SlugId,
        render_slug = npc.RenderSlug,
        x = npc.PosX,
        y = npc.PosY,
        z = npc.PosZ,
        yaw = npc.Yaw,
        anim = npc.PlayingAnimation,
        attackable = npc.Type.Attackable,
        final_level = npc.Type.FinalLevel,
        hitpoints = npc.Hitpoints,
        max_hitpoints = npc.MaxHitpoints,
        has_dialogue = HasDialogueScript(npc),
        dialogue_script = npc.Type.DialogueScript,
        is_banker = npc.Type.IsBanker,
    };

    private bool HasDialogueScript(ActiveNpc npc)
    {
        string ds = npc.Type.DialogueScript;
        if (string.IsNullOrEmpty(ds)) return false;
        string path = ds;
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
    // Render bundles (served to clients; not an editor endpoint)
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
    // Admin open (still works; opens the panel which is UI-stubbed)
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

    // ============================================================
    // NPC editor — Phase A stubs
    // ============================================================
    //
    // The editor backend is fully stubbed pending Phase B. Every handler
    // returns a "not available" reply so the client's UI degrades gracefully
    // (empty lists, save-failed indicators). The runtime path — loading,
    // movement, visibility, combat, respawn — is unaffected.
    //
    // Phase B will replace these with type CRUD (list, get, create, update,
    // delete) plus hitbox/scale saves routed through the type's render_slug.

    internal void HandleNpcEditorGet(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        // Phase A: no response. Client shows empty NPC data.
    }

    internal Task HandleNpcEditorSaveAsync(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return Task.CompletedTask;
        if (!session.IsAdmin) return Task.CompletedTask;
        _outbound.Send(peerId, new
        {
            type = "npc_editor_saved",
            ok = false,
            npc_id = 0,
        });
        return Task.CompletedTask;
    }

    internal void HandleDevNpcEditorList(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        _outbound.Send(peerId, new
        {
            type = "dev_npc_editor_list_result",
            ok = true,
            mode = "unique",
            items = new List<object>(),
        });
    }

    internal void HandleDevNpcEditorGetTemplate(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        _outbound.Send(peerId, new
        {
            type = "dev_npc_editor_template_data",
            ok = false,
            template = new Dictionary<string, object>(),
        });
    }

    internal void HandleDevNpcEditorSaveTemplate(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        _outbound.Send(peerId, new
        {
            type = "dev_npc_editor_template_saved",
            ok = false,
            template_id = 0,
        });
    }

    internal void HandleDevNpcEditorOptions(long peerId)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        _outbound.Send(peerId, new
        {
            type = "dev_npc_editor_options_result",
            ok = false,
            options = new
            {
                render_slugs = new List<string>(),
                dialogue_scripts = new List<string>(),
                loot_lists = new List<string>(),
                credits_loot_lists = new List<string>(),
            }
        });
    }

    internal void HandleNpcRenderSaveHitbox(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        _outbound.Send(peerId, new
        {
            type = "npc_render_save_result",
            ok = false,
            kind = "hitbox",
            slug = "",
        });
    }

    internal void HandleNpcRenderSaveRender(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        _outbound.Send(peerId, new
        {
            type = "npc_render_save_result",
            ok = false,
            kind = "render",
            slug = "",
        });
    }
}