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

    public MovementSystem(ServerState state, WorldService world, OutboundChannel outbound)
    {
        _state = state;
        _world = world;
        _outbound = outbound;
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
}