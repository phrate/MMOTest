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
    private readonly Dictionary<string, Dictionary<string, (int Min, int Max)>> _lootTables;
    private readonly Dictionary<string, (int Min, int Max)> _creditsLoot;

    public int NpcViewChunkRadius { get; }

    public NpcSystem(ServerState state, WorldService world, OutboundChannel outbound,
                    NpcRepository npcRepo, Gfs.GfsManager gfs, NpcRenderManager npcRender,
                    Pathfinder pathfinder, int npcViewChunkRadius, bool diagNpcWander,
                    Dictionary<string, Dictionary<string, (int Min, int Max)>> lootTables,
                    Dictionary<string, (int Min, int Max)> creditsLoot)
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
        _lootTables = lootTables;
        _creditsLoot = creditsLoot;
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
        if (!_npcs.TryGetValue(npcId, out var npc)) return;
        _outbound.Send(peerId, new { type = "npc_editor_open", type_id = npc.Type.Id });
    }

    internal void HandleDevNpcEditorOptions(long peerId)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;

        var renderSlugs = _npcRender.GetManifest().Keys.OrderBy(k => k).ToList();

        var dialogueScripts = new List<string>();
        foreach (var path in _gfs.GetAllScriptPaths())
        {
            if (!path.StartsWith("dialogue/npc_")) continue;
            if (!path.EndsWith(".gfs")) continue;
            string shortName = path.Substring("dialogue/".Length);
            shortName = shortName.Substring(0, shortName.Length - 4);
            dialogueScripts.Add(shortName);
        }
        dialogueScripts.Sort();

        var lootLists = _lootTables.Keys.OrderBy(k => k).ToList();
        var creditsLootLists = _creditsLoot.Keys.OrderBy(k => k).ToList();

        _outbound.Send(peerId, new
        {
            type = "dev_npc_editor_options_result",
            ok = true,
            options = new
            {
                render_slugs = renderSlugs,
                dialogue_scripts = dialogueScripts,
                loot_lists = lootLists,
                credits_loot_lists = creditsLootLists,
            }
        });
    }

    internal void HandleNpcRenderSaveHitbox(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;

        string slug = GetStringProp(root, "slug");
        if (!IsSafeSlug(slug)) return;
        if (!root.TryGetProperty("hitbox", out var hbEl)) return;
        if (hbEl.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        string json = hbEl.GetRawText();
        bool ok = WriteUserNpcRenderFile(slug, "hitbox.json", json);
        _outbound.Send(peerId, new
        {
            type = "npc_render_save_result",
            ok,
            kind = "hitbox",
            slug,
        });
    }

    private static string GetStringProp(System.Text.Json.JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return "";
        if (el.ValueKind != System.Text.Json.JsonValueKind.String) return "";
        return el.GetString() ?? "";
    }

    private static bool IsSafeSlug(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return false;
        foreach (char c in slug)
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;
        return true;
    }

    private static bool WriteUserNpcRenderFile(string slug, string fileName, string contents)
    {
        try
        {
            string baseDir = ProjectSettings.GlobalizePath("user://npc_render");
            string target = System.IO.Path.Combine(baseDir, slug);
            System.IO.Directory.CreateDirectory(target);
            System.IO.File.WriteAllText(System.IO.Path.Combine(target, fileName), contents);
            return true;
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[NpcSystem] WriteUserNpcRenderFile({slug}, {fileName}): {e.Message}");
            return false;
        }
    }

    internal void HandleNpcRenderSaveRender(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;

        string slug = GetStringProp(root, "slug");
        if (!IsSafeSlug(slug)) return;
        if (!root.TryGetProperty("render", out var rEl)) return;
        if (rEl.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        string json = rEl.GetRawText();
        bool ok = WriteUserNpcRenderFile(slug, "render.json", json);
        _outbound.Send(peerId, new
        {
            type = "npc_render_save_result",
            ok,
            kind = "render",
            slug,
        });
    }

    // ============================================================
    // NPC type CRUD
    // ============================================================

    internal void HandleDevNpcTypeList(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;

        var types = _npcRepo.GetAllTypesSync();
        var items = new List<object>();
        foreach (var t in types)
        {
            int instanceCount = _npcRepo.CountInstancesByTypeSync(t.Id);
            items.Add(new
            {
                id = t.Id,
                slug_id = t.SlugId,
                display = t.Display,
                render_slug = t.RenderSlug,
                instance_count = instanceCount,
            });
        }

        _outbound.Send(peerId, new
        {
            type = "dev_npc_type_list_result",
            ok = true,
            items,
        });
    }

    internal void HandleDevNpcTypeGet(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        if (!root.TryGetProperty("type_id", out var idEl)
            || !idEl.TryGetInt64(out long typeId)) return;

        var record = _npcRepo.GetTypeByIdSync(typeId);
        if (record == null)
        {
            _outbound.Send(peerId, new { type = "dev_npc_type_data", ok = false });
            return;
        }

        _outbound.Send(peerId, new
        {
            type = "dev_npc_type_data",
            ok = true,
            data = NpcTypeRecordToDict(record),
        });
    }

    internal void HandleDevNpcTypeCreate(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        if (!root.TryGetProperty("data", out var dataEl)
            || dataEl.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        var updates = ParseJsonToDict(dataEl);
        _ = CreateTypeAndReply(peerId, updates);
    }

    private async Task CreateTypeAndReply(long peerId, Dictionary<string, object> updates)
    {
        try
        {
            long newId = await _npcRepo.CreateTypeAsync(updates);
            _outbound.Send(peerId, new
            {
                type = "dev_npc_type_created",
                ok = true,
                type_id = newId,
            });
        }
        catch (Exception e)
        {
            GD.PrintErr($"[NpcSystem] CreateType failed: {e.Message}");
            _outbound.Send(peerId, new
            {
                type = "dev_npc_type_created",
                ok = false,
                type_id = 0L,
                error = e.Message,
            });
        }
    }

    internal void HandleDevNpcTypeUpdate(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        if (!root.TryGetProperty("type_id", out var idEl)
            || !idEl.TryGetInt64(out long typeId)) return;
        if (!root.TryGetProperty("data", out var dataEl)
            || dataEl.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        var updates = ParseJsonToDict(dataEl);
        _ = UpdateTypeAndReply(peerId, typeId, updates);
    }

    private async Task UpdateTypeAndReply(long peerId, long typeId, Dictionary<string, object> updates)
    {
        bool ok;
        try { ok = await _npcRepo.UpdateTypeAsync(typeId, updates); }
        catch (Exception e)
        {
            GD.PrintErr($"[NpcSystem] UpdateType failed: {e.Message}");
            ok = false;
        }

        if (ok)
        {
            // Refresh the in-memory NpcTypeRecord (shared by reference across
            // every instance of this type) and broadcast npc_updated so clients
            // reflect the change immediately — no respawn needed.
            var reloaded = _npcRepo.GetTypeByIdSync(typeId);
            if (reloaded != null)
            {
                var affected = new List<ActiveNpc>();
                foreach (var npc in _npcs.Values)
                    if (npc.Type.Id == typeId) affected.Add(npc);

                if (affected.Count > 0)
                {
                    CopyTypeFields(reloaded, affected[0].Type);
                    foreach (var npc in affected)
                        _outbound.Broadcast(new { type = "npc_updated", npc = NpcToPacket(npc) });
                    NotifyNpcVisibilityChanged();
                }
            }
        }

        _outbound.Send(peerId, new
        {
            type = "dev_npc_type_updated",
            ok,
            type_id = typeId,
        });
    }

    private static void CopyTypeFields(NpcTypeRecord src, NpcTypeRecord dst)
    {
        dst.SlugId            = src.SlugId;
        dst.Display           = src.Display;
        dst.RenderSlug        = src.RenderSlug;
        dst.DialogueScript    = src.DialogueScript;
        dst.IsBanker          = src.IsBanker;
        dst.Attackable        = src.Attackable;
        dst.Aggressive        = src.Aggressive;
        dst.AggroRangeTiles   = src.AggroRangeTiles;
        dst.AiPackageState    = src.AiPackageState;
        dst.SpawnTimerSeconds = src.SpawnTimerSeconds;
        dst.SpawnFlags        = src.SpawnFlags;
        dst.LootTableName     = src.LootTableName;
        dst.CreditsTableName  = src.CreditsTableName;
        dst.FinalLevel        = src.FinalLevel;
        dst.MaxHitpoints      = src.MaxHitpoints;
        dst.MaxAccuracy       = src.MaxAccuracy;
        dst.MaxStrength       = src.MaxStrength;
        dst.MaxDefence        = src.MaxDefence;
        dst.MaxEther          = src.MaxEther;
        dst.MeleeBonus        = src.MeleeBonus;
        dst.RangedBonus       = src.RangedBonus;
        dst.EtherBonus        = src.EtherBonus;
        dst.MeleeDefense      = src.MeleeDefense;
        dst.RangedDefense     = src.RangedDefense;
        dst.EtherDefense      = src.EtherDefense;
        dst.AttackSpeedTicks  = src.AttackSpeedTicks;
    }

    internal void HandleDevNpcTypeDelete(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        if (!root.TryGetProperty("type_id", out var idEl)
            || !idEl.TryGetInt64(out long typeId)) return;

        _ = DeleteTypeAndReply(peerId, typeId);
    }

    private async Task DeleteTypeAndReply(long peerId, long typeId)
    {
        bool ok;
        try { ok = await _npcRepo.DeleteTypeAsync(typeId); }
        catch (Exception e)
        {
            GD.PrintErr($"[NpcSystem] DeleteType failed: {e.Message}");
            ok = false;
        }

        if (ok)
        {
            // Remove any live instances of the deleted type from memory
            var toRemove = new List<long>();
            foreach (var npc in _npcs.Values)
                if (npc.Type.Id == typeId) toRemove.Add(npc.Id);
            foreach (var id in toRemove) _npcs.Remove(id);
            if (toRemove.Count > 0) NotifyNpcVisibilityChanged();
        }

        _outbound.Send(peerId, new
        {
            type = "dev_npc_type_deleted",
            ok,
            type_id = typeId,
        });
    }

    private Dictionary<string, object> ParseJsonToDict(System.Text.Json.JsonElement obj)
    {
        var result = new Dictionary<string, object>();
        foreach (var prop in obj.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.String => prop.Value.GetString() ?? "",
                System.Text.Json.JsonValueKind.Number => prop.Value.TryGetInt64(out long l) ? l : 0L,
                _ => (object)0L,
            };
        }
        return result;
    }

    private static Dictionary<string, object> NpcTypeRecordToDict(NpcTypeRecord t) => new()
    {
        ["id"] = t.Id,
        ["slug_id"] = t.SlugId,
        ["display"] = t.Display,
        ["render_slug"] = t.RenderSlug,
        ["dialogue_script"] = t.DialogueScript,
        ["is_banker"] = t.IsBanker,
        ["attackable"] = t.Attackable,
        ["aggressive"] = t.Aggressive,
        ["aggro_range"] = t.AggroRangeTiles,
        ["ai_package_state"] = t.AiPackageState,
        ["spawn_timer"] = t.SpawnTimerSeconds,
        ["spawn_flags"] = t.SpawnFlags,
        ["loot_table_name"] = t.LootTableName,
        ["credits_table_name"] = t.CreditsTableName,
        ["final_level"] = t.FinalLevel,
        ["max_hitpoints"] = t.MaxHitpoints,
        ["max_accuracy"] = t.MaxAccuracy,
        ["max_strength"] = t.MaxStrength,
        ["max_defence"] = t.MaxDefence,
        ["max_ether"] = t.MaxEther,
        ["melee_bonus"] = t.MeleeBonus,
        ["ranged_bonus"] = t.RangedBonus,
        ["ether_bonus"] = t.EtherBonus,
        ["melee_defense"] = t.MeleeDefense,
        ["ranged_defense"] = t.RangedDefense,
        ["ether_defense"] = t.EtherDefense,
        ["attack_speed"] = t.AttackSpeedTicks,
    };
}