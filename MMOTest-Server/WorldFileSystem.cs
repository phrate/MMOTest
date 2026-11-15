using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MMOTest.Server;

/// <summary>
/// Owns world-file hotfix detection and the world_info / world_fetch packet
/// handlers. Polls WorldService.EnsureLoaded() for mtime changes and
/// broadcasts world_updated to clients when the version bumps.
/// </summary>
internal sealed class WorldFileSystem
{
    private const double CheckIntervalSeconds = 1.0;

    private readonly ServerState _state;
    private readonly WorldService _world;
    private readonly OutboundChannel _outbound;

    private double _checkAccumulator;
    private int _lastBroadcastVersion;

    internal static readonly string[] WorldFileCandidates =
    {
        ProjectSettings.GlobalizePath("user://world.json"),
        ProjectSettings.GlobalizePath("res://data/world.json"),
    };

    private readonly NpcSystem _npc;
    private readonly NpcRepository _npcRepo;

    public WorldFileSystem(ServerState state, WorldService world, OutboundChannel outbound,
                        NpcSystem npc, NpcRepository npcRepo)
    {
        _state = state;
        _world = world;
        _outbound = outbound;
        _npc = npc;
        _npcRepo = npcRepo;
        _lastBroadcastVersion = world.IsLoaded ? world.Version : -1;
    }

    public void Tick(double delta)
    {
        _checkAccumulator += delta;
        if (_checkAccumulator < CheckIntervalSeconds) return;
        _checkAccumulator = 0;
        CheckForUpdates();
    }

    private void CheckForUpdates()
    {
        if (!_world.EnsureLoaded()) return;
        if (_world.Version == _lastBroadcastVersion) return;

        _lastBroadcastVersion = _world.Version;
        Diag.Log("diag_world",
            $"World hotfix: v{_world.Version}, notifying {_state.Sessions.Count} client(s)");

        _outbound.Broadcast(new
        {
            type = "world_updated",
            world_version = _world.Version,
            format_version = _world.FormatVersion
        });
    }

    internal void HandleWorldInfo(long peerId)
    {
        if (!_state.Sessions.ContainsKey(peerId))
        {
            _outbound.SendError(peerId, "world_info_result", ErrorKeys.WorldNotLoggedIn);
            return;
        }
        if (!_world.EnsureLoaded())
        {
            _outbound.SendError(peerId, "world_info_result", ErrorKeys.WorldUnavailable);
            return;
        }
        _outbound.Send(peerId, new
        {
            type = "world_info_result",
            ok = true,
            world_version = _world.Version,
            format_version = _world.FormatVersion
        });
    }

    internal void HandleWorldFetch(long peerId)
    {
        if (!_state.Sessions.ContainsKey(peerId))
        {
            _outbound.SendError(peerId, "world_data", ErrorKeys.WorldNotLoggedIn);
            return;
        }
        if (!_world.EnsureLoaded())
        {
            _outbound.SendError(peerId, "world_data", ErrorKeys.WorldUnavailable);
            return;
        }
        var root = _world.GetRootElement();
        if (root == null)
        {
            _outbound.SendError(peerId, "world_data", ErrorKeys.WorldUnavailable);
            return;
        }
        _outbound.Send(peerId, new
        {
            type = "world_data",
            ok = true,
            world_version = _world.Version,
            format_version = _world.FormatVersion,
            data = root.Value
        });
    }

    internal void HandleDevWorldSave(long peerId, System.Text.Json.JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!session.IsAdmin) return;
        if (!root.TryGetProperty("world_json", out var wjEl)) return;
        if (wjEl.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        string worldJson = wjEl.GetRawText();

        var pendingCreates = new List<(long TypeId, int X, int Y, int Z)>();
        if (root.TryGetProperty("pending_creates", out var pcEl)
            && pcEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var el in pcEl.EnumerateArray())
            {
                if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!el.TryGetProperty("type_id", out var tidE) || !tidE.TryGetInt64(out long tid)) continue;
                if (!el.TryGetProperty("x", out var xE) || !xE.TryGetInt32(out int x)) continue;
                if (!el.TryGetProperty("y", out var yE) || !yE.TryGetInt32(out int y)) continue;
                if (!el.TryGetProperty("z", out var zE) || !zE.TryGetInt32(out int z)) continue;
                pendingCreates.Add((tid, x, y, z));
            }
        }

        var pendingDeletes = new List<long>();
        if (root.TryGetProperty("pending_deletes", out var pdEl)
            && pdEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var el in pdEl.EnumerateArray())
                if (el.TryGetInt64(out long iid)) pendingDeletes.Add(iid);
        }

        _ = ProcessSaveAsync(peerId, worldJson, pendingCreates, pendingDeletes);
    }

    private async Task ProcessSaveAsync(long peerId, string worldJson,
        List<(long TypeId, int X, int Y, int Z)> creates, List<long> deletes)
    {
        try
        {
            // Apply instance changes first so the world hotfix (triggered by the
            // file write below) reloads a world that already matches the DB.
            foreach (var (typeId, x, y, z) in creates)
            {
                var type = _npc.GetTypeById(typeId);
                if (type == null) continue;
                long iid = await _npcRepo.CreateInstanceAsync(typeId, x, y, z);
                var npc = new ActiveNpc
                {
                    Id = iid,
                    Type = type,
                    TileX = x,
                    TileY = y,
                    TileZ = z,
                    PlayingAnimation = "idle",
                };
                npc.ResetToTypeDefaults();
                npc.ResetPositionToHome();
                _npc.AddInstanceDirect(npc);
            }

            foreach (var iid in deletes)
            {
                if (await _npcRepo.DeleteInstanceAsync(iid))
                    _npc.RemoveInstanceDirect(iid);
            }

            _npc.NotifyNpcVisibilityChanged();

            string path = ProjectSettings.GlobalizePath("user://world.json");
            System.IO.File.WriteAllText(path, worldJson);
            Diag.Log("diag_world",
                $"DevWorldSave: wrote {worldJson.Length} bytes, +{creates.Count} NPCs, -{deletes.Count} NPCs");

            _outbound.Send(peerId, new { type = "dev_world_save_result", ok = true });
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[WorldFileSystem] DevWorldSave failed: {e.Message}");
            _outbound.Send(peerId, new
            {
                type = "dev_world_save_result",
                ok = false,
                error = e.Message,
            });
        }
    }
}