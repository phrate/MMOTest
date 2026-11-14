using System.Collections.Generic;
using Godot;

namespace MMOTest.Server;

// ---------------------------------------------------------------------------
// Domain types — moved out of Main so ServerState can reference them without
// needing a Main.CombatPair-style nested path.
// ---------------------------------------------------------------------------

internal record PlayerSession(long PlayerId, string Username, bool IsAdmin, long Sid);

internal record PlayerPosition(float X, float Y, float Z, float Yaw);

internal sealed class CombatPair
{
    public long PlayerId { get; init; }
    public long NpcId { get; init; }
    public long LastPlayerSwingTick { get; set; }
    public long LastNpcSwingTick { get; set; }
    public bool StateAnnounced { get; set; }
    public bool InitiatedByPlayer { get; set; } = true;
    public Vector2I LastChaseTarget { get; set; } = new(-99999, -99999);
}

internal sealed class DialogueSession
{
    public string SessionId { get; init; } = "";
    public long PlayerId { get; init; }
    public long NpcId { get; init; }
    public string ScriptPath { get; init; } = "";
    public Gfs.SuiteNode Ast { get; init; } = null!;
    public string CurrentTopic { get; set; } = "start";
    public bool IsOpened { get; set; }
}

// ---------------------------------------------------------------------------
// Shared mutable state across systems. Single bag of fields, no methods.
// ---------------------------------------------------------------------------
//
// Purpose: give cross-cutting systems a single constructor dependency instead
// of a dozen individual dictionaries. Systems that will be extracted in
// Phase 3b-3d take (ServerState state, ...) and read state.Positions,
// state.Inventories, etc. Main continues to hold its own private fields that
// aren't shared (bank JSON blobs, ground items, NPC render bundles, ...).
//
// Owner map (informational, not enforced):
//   Sessions, PeerByPlayerId   — Main (auth)
//   Positions                  — Main (movement tick)
//   Inventories, Credits       — Main (item, trade, bank, recipe)
//   CombatByPlayer, PlayerCombat,
//   PendingEngage              — Main (combat)
//   Intents, PendingPickup     — Main (movement, pickup)
//   DialogueByPlayer           — Main (dialogue)
//   CurrentTick                — Main (RunTick mirrors _state.CurrentTick here)
// ---------------------------------------------------------------------------

internal sealed class ServerState
{
    public readonly Dictionary<long, PlayerSession> Sessions = new();
    public readonly Dictionary<long, long> PeerByPlayerId = new();
    public readonly Dictionary<long, PlayerPosition> Positions = new();
    public readonly Dictionary<long, long[]> Inventories = new();
    public readonly Dictionary<long, int> Credits = new();
    public readonly Dictionary<long, CombatPair> CombatByPlayer = new();
    public readonly Dictionary<long, long> PendingEngage = new();
    public readonly Dictionary<long, PlayerCombatData> PlayerCombat = new();
    public readonly Dictionary<long, List<SkillLevel>> SkillsByPlayer = new();
    public readonly Dictionary<long, List<Vector2I>> Intents = new();
    public readonly Dictionary<long, long> PendingPickup = new();
    public readonly Dictionary<long, DialogueSession> DialogueByPlayer = new();

    public long CurrentTick;
}