using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns the per-tick player movement step. Reads pending movement intents,
/// advances each player along their path, broadcasts tick_positions.
///
/// Path validation and intent queuing (HandlePlayerWalkBuffered) still live on
/// Main until Phase 3b-B extracts CombatSystem — that handler calls EndCombat,
/// which will move to CombatSystem and remove the last cross-system coupling.
/// </summary>
internal sealed class MovementSystem
{
    public const float WalkSpeed = 2.0f;
    public const double TickIntervalSeconds = 0.3;

    private readonly ServerState _state;
    private readonly WorldService _world;
    private readonly OutboundChannel _outbound;
    private readonly Pathfinder _pathfinder;

    public MovementSystem(ServerState state, WorldService world, OutboundChannel outbound, Pathfinder pathfinder)
    {
        _state = state;
        _world = world;
        _outbound = outbound;
        _pathfinder = pathfinder;
    }

    /// <summary>
    /// Advances every player with a pending intent by one tick's worth of
    /// movement. Broadcasts tick_positions with waypoint data if any position
    /// changed. Idempotent if intents is empty.
    /// </summary>
    public void Tick(long currentTick)
    {
        if (_state.Intents.Count == 0) return;

        var updates = new List<object>();
        var donePeers = new List<long>();

        foreach (long peerId in _state.Intents.Keys.ToList())
        {
            if (!_state.Sessions.TryGetValue(peerId, out var session)) { donePeers.Add(peerId); continue; }
            if (!_state.Positions.TryGetValue(session.PlayerId, out var current)) { donePeers.Add(peerId); continue; }

            var path = _state.Intents[peerId];
            if (path.Count == 0) { donePeers.Add(peerId); continue; }

            var (next, finished, waypoint, waypointYaw) = StepTowardPath(current, path);
            _state.Positions[session.PlayerId] = next;
            updates.Add(new
            {
                player_id = session.PlayerId,
                x = next.X, y = next.Y, z = next.Z, yaw = next.Yaw,
                wx = waypoint.HasValue ? (float?)waypoint.Value.X : null,
                wy = waypoint.HasValue ? (float?)waypoint.Value.Y : null,
                wz = waypoint.HasValue ? (float?)waypoint.Value.Z : null,
                wyaw = waypoint.HasValue ? (float?)waypointYaw : null,
            });
            if (finished) donePeers.Add(peerId);
        }

        foreach (long p in donePeers) _state.Intents.Remove(p);
        if (updates.Count > 0)
        {
            _outbound.Broadcast(new { type = "tick_positions", tick = currentTick, updates },
                channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
        }
    }

    /// <summary>
    /// True if the player has a pending movement intent (auto-path or manual
    /// click). Used by combat to gate auto-retaliate: swings only fire while
    /// standing still.
    /// </summary>
    public bool IsMoving(long playerId)
    {
        foreach (var kv in _state.Sessions)
        {
            if (kv.Value.PlayerId != playerId) continue;
            return _state.Intents.TryGetValue(kv.Key, out var path) && path.Count > 0;
        }
        return false;
    }

    /// <summary>
    /// Advances up to WalkSpeed*Tick seconds of distance along the path,
    /// consuming waypoints as reached and carrying leftover distance into the
    /// next segment. Captures the tile just reached when direction changes
    /// mid-tick so the client can interpolate piecewise through the corner
    /// instead of sliding.
    /// </summary>
    private (PlayerPosition Endpoint, bool Finished, Vector3? Waypoint, float WaypointYaw)
        StepTowardPath(PlayerPosition current, List<Vector2I> path)
    {
        float remaining = WalkSpeed * (float)TickIntervalSeconds;
        float x = current.X;
        float z = current.Z;
        float yaw = current.Yaw;

        Vector3? lastWaypoint = null;
        float lastWaypointYaw = 0f;
        bool pastFirstIteration = false;

        while (remaining > 0.0001f && path.Count > 0)
        {
            var target = path[0];
            float dx = target.X - x;
            float dz = target.Y - z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);

            if (dist < 0.0001f) { path.RemoveAt(0); continue; }

            float newYaw = MathF.Atan2(-dx, -dz);
            newYaw = MathF.Round(newYaw / (MathF.PI / 4f)) * (MathF.PI / 4f);

            if (pastFirstIteration && MathF.Abs(yaw - newYaw) > 0.001f)
            {
                int cx = (int)MathF.Round(x);
                int cz = (int)MathF.Round(z);
                var wInfo = _world.GetTileInfo(cx, cz);
                lastWaypoint = new Vector3(cx, wInfo.Z, cz);
                lastWaypointYaw = yaw;
            }

            if (dist <= remaining)
            {
                x = target.X; z = target.Y; remaining -= dist; path.RemoveAt(0);
            }
            else
            {
                float ratio = remaining / dist;
                x += dx * ratio;
                z += dz * ratio;
                remaining = 0;
            }
            yaw = newYaw;
            pastFirstIteration = true;
        }

        int tileX = (int)MathF.Round(x);
        int tileY = (int)MathF.Round(z);
        var tinfo = _world.GetTileInfo(tileX, tileY);
        var endpoint = new PlayerPosition(x, tinfo.Z, z, yaw);
        bool finished = path.Count == 0;
        return (endpoint, finished, lastWaypoint, lastWaypointYaw);
    }

    public bool ArePositionsAdjacent(long playerId, ActiveNpc npc)
    {
        if (!_state.Positions.TryGetValue(playerId, out var ppos)) return false;
        int px = (int)MathF.Round(ppos.X);
        int py = (int)MathF.Round(ppos.Z);
        int nx = (int)MathF.Round(npc.PosX);
        int ny = (int)MathF.Round(npc.PosZ);
        int dx = Math.Abs(px - nx);
        int dy = Math.Abs(py - ny);
        if (dx > 1 || dy > 1) return false;
        if (dx == 0 && dy == 0) return false;
        return true;
    }

    public bool MovePlayerAdjacentToNpc(long playerId, ActiveNpc npc)
    {
        long peerId = -1;
        foreach (var kv in _state.Sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
        if (peerId < 0) return false;
        if (_state.Intents.ContainsKey(peerId)) return true;

        if (!_state.Positions.TryGetValue(playerId, out var ppos)) return false;
        int px = (int)MathF.Round(ppos.X);
        int py = (int)MathF.Round(ppos.Z);
        int nx = (int)MathF.Round(npc.PosX);
        int ny = (int)MathF.Round(npc.PosZ);

        if (px == nx && py == ny)
        {
            Vector2I[] dirs = { new(1,0), new(-1,0), new(0,1), new(0,-1),
                                new(1,1), new(1,-1), new(-1,1), new(-1,-1) };
            foreach (var d in dirs)
            {
                var cand = new Vector2I(px + d.X, py + d.Y);
                if (_world.IsWalkable(cand.X, cand.Y))
                {
                    _state.Intents[peerId] = new List<Vector2I> { cand };
                    return true;
                }
            }
            return false;
        }

        var route = _pathfinder.FindPath(px, py, nx, ny);
        if (route.Count == 0) return false;
        var goal = new Vector2I(nx, ny);
        if (route[^1] == goal) route.RemoveAt(route.Count - 1);
        if (route.Count == 0) return false;
        _state.Intents[peerId] = route;
        return true;
    }

    public void RemoveIntentForPlayer(long playerId)
    {
        if (_state.PeerByPlayerId.TryGetValue(playerId, out long peerId)) _state.Intents.Remove(peerId);
    }

    /// <summary>
    /// Validates a proposed path against world geometry and adjacency rules.
    /// All tiles must be walkable; consecutive tiles must be 8-connected;
    /// diagonal steps require both shared corners walkable (no corner-cutting);
    /// first step must be adjacent to the player's current rounded tile. Bounds
    /// checked to prevent teleport-by-crafted-path. Matches client A*
    /// (DIAGONAL_MODE_ONLY_IF_NO_OBSTACLES).
    /// </summary>
    public bool ValidateWalkPath(long playerId, List<Vector2I> path)
    {
        const int MaxPathLength = 2000;

        if (path.Count == 0) return false;
        if (path.Count > MaxPathLength) return false;
        if (!_state.Positions.TryGetValue(playerId, out var current)) return false;

        Vector2I? prev = null;
        foreach (var tile in path)
        {
            if (Math.Abs(tile.X) > 1_000_000 || Math.Abs(tile.Y) > 1_000_000) return false;
            if (!_world.IsWalkable(tile.X, tile.Y)) return false;

            if (prev.HasValue)
            {
                int ddx = Math.Abs(tile.X - prev.Value.X);
                int ddy = Math.Abs(tile.Y - prev.Value.Y);
                if (ddx > 1 || ddy > 1 || (ddx == 0 && ddy == 0)) return false;
                if (ddx == 1 && ddy == 1)
                {
                    if (!_world.IsWalkable(prev.Value.X, tile.Y)) return false;
                    if (!_world.IsWalkable(tile.X, prev.Value.Y)) return false;
                }
            }
            prev = tile;
        }

        int curX = (int)MathF.Round(current.X);
        int curY = (int)MathF.Round(current.Z);
        int ddx0 = Math.Abs(path[0].X - curX);
        int ddy0 = Math.Abs(path[0].Y - curY);
        if (ddx0 > 1 || ddy0 > 1) return false;
        if (ddx0 == 1 && ddy0 == 1)
        {
            if (!_world.IsWalkable(curX, path[0].Y)) return false;
            if (!_world.IsWalkable(path[0].X, curY)) return false;
        }

        return true;
    }
}