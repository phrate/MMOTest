using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace MMOTest.Server;

internal sealed class WalkHandler
{
    private readonly ServerState _state;
    private readonly MovementSystem _movement;
    private readonly CombatSystem _combat;
    private readonly Coordinator _coordinator;
    private readonly Gfs.PerTickManager _perTick;

    public WalkHandler(ServerState state, MovementSystem movement, CombatSystem combat,
                       Coordinator coordinator, Gfs.PerTickManager perTick)
    {
        _state = state;
        _movement = movement;
        _combat = combat;
        _coordinator = coordinator;
        _perTick = perTick;
    }

    internal void HandlePlayerWalk(long peerId, JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;

        _perTick?.EndByStem(session.PlayerId, "follow_player");
        _coordinator.OnPlayerWalked(session.PlayerId);
        _state.PendingEngage.Remove(session.PlayerId);
        _state.PendingPickup.Remove(session.PlayerId);

        _combat.CancelPendingApproach(session.PlayerId);

        if (!root.TryGetProperty("path", out JsonElement pathEl) || pathEl.ValueKind != JsonValueKind.Array)
            return;

        var path = new List<Vector2I>();
        foreach (JsonElement t in pathEl.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() != 2) return;
            if (!t[0].TryGetInt32(out int x) || !t[1].TryGetInt32(out int y)) return;
            path.Add(new Vector2I(x, y));
        }

        if (!_movement.ValidateWalkPath(session.PlayerId, path)) return;

        _state.Intents[peerId] = path;
    }
}