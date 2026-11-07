using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace MMOTest.Server;

public partial class Main
{
    private sealed class DialogueSession
    {
        public string SessionId { get; init; } = "";
        public long PlayerId { get; init; }
        public long NpcId { get; init; }
        public string ScriptPath { get; init; } = "";
        public Gfs.SuiteNode Ast { get; init; } = null!;
        public string CurrentTopic { get; set; } = "start";
        public bool IsOpened { get; set; }
    }

private readonly Dictionary<long, long> _pendingTalk = new(); // playerId -> npcId
private readonly Dictionary<long, (long PlayerId, long Sid)> _npcLookAt = new();

private void SnapAndFaceForDialogue(long playerId, ActiveNpc npc)
{
    if (!_positions.TryGetValue(playerId, out var pos)) return;

    int px = (int)MathF.Round(pos.X);
    int pz = (int)MathF.Round(pos.Z);
    var tinfo = GetTileInfo(px, pz);
    int nx = (int)MathF.Round(npc.PosX);
    int nz = (int)MathF.Round(npc.PosZ);

    float yaw = MathF.Atan2(-(nx - px), -(nz - pz));
    yaw = MathF.Round(yaw / (MathF.PI / 4f)) * (MathF.PI / 4f);

    var newPos = new PlayerPosition(px, tinfo.Z, pz, yaw);
    _positions[playerId] = newPos;

    Broadcast(new
    {
        type = "tick_positions",
        tick = _currentTick,
        updates = new[] {
            new {
                player_id = playerId,
                x = newPos.X, y = newPos.Y, z = newPos.Z, yaw = newPos.Yaw,
            }
        }
    }, channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);

    long sid = 0;
    long peerId = GetPeerIdForPlayer(playerId);
    if (peerId >= 0 && _sessions.TryGetValue(peerId, out var sess))
        sid = sess.Sid;
    QueueNpcLookAt(npc.Id, playerId, sid);
}

private void QueueNpcLookAt(long npcId, long playerId, long sid)
{
    if (_npcLookAt.TryGetValue(npcId, out var existing))
    {
        // Keep the lower sid — deterministic tiebreak when two players reach
        // the same NPC on the same tick.
        if (sid >= existing.Sid) return;
    }
    _npcLookAt[npcId] = (playerId, sid);
}

private void ProcessNpcLookAt()
{
    if (_npcLookAt.Count == 0) return;
    foreach (var kv in _npcLookAt)
    {
        long npcId = kv.Key;
        long playerId = kv.Value.PlayerId;
        if (!_npcs.TryGetValue(npcId, out var npc)) continue;
        if (npc.IsDead) continue;
        if (_combatByNpc.ContainsKey(npcId)) continue;   // combat facing takes precedence
        if (!_positions.TryGetValue(playerId, out var ppos)) continue;

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
            Broadcast(new
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

internal void RequestTalk(long playerId, long npcId)
{
    if (!_npcs.TryGetValue(npcId, out var npc)) return;
    if (npc.IsDead) return;
    if (string.IsNullOrEmpty(npc.DialogueScript))
    {
        ChatToPlayer(playerId, "They have nothing to say.");
        return;
    }
    if (!_peerByPlayerId.ContainsKey(playerId) || !_positions.ContainsKey(playerId)) return;

    // Already walking to talk to this exact NPC → no-op.
    if (_pendingTalk.TryGetValue(playerId, out var currentNpcId) && currentNpcId == npcId) return;

    // Cancel any conflicting pursuits.
    _pendingEngage.Remove(playerId);
    _pendingPickup.Remove(playerId);
    _perTick?.EndByStem(playerId, "follow_player");
    RemovePlayerIntent(playerId);

    // If already talking to someone (or the same NPC), close first.
    CancelDialogueForPlayer(playerId, "");

    // Even when already adjacent, queue the open for the next tick so the
    // latency is uniform whether the player walked in or was standing next
    // to the NPC when clicking. ProcessPendingTalk handles both.
    _pendingTalk[playerId] = npcId;

    if (!ArePositionsAdjacent(playerId, npc))
        MovePlayerAdjacentToNpc(playerId, npc);
}

internal void CancelPendingTalkFor(long playerId)
{
    _pendingTalk.Remove(playerId);
}

private void ProcessPendingTalk()
{
    if (_pendingTalk.Count == 0) return;

    var toRemove = new List<long>();
    var toOpen = new List<(long PlayerId, long NpcId)>();

    foreach (var kv in _pendingTalk)
    {
        long playerId = kv.Key;
        long npcId = kv.Value;

        if (!_npcs.TryGetValue(npcId, out var npc) || npc.IsDead
            || string.IsNullOrEmpty(npc.DialogueScript))
        {
            toRemove.Add(playerId);
            continue;
        }
        if (!_positions.ContainsKey(playerId)
            || !_peerByPlayerId.TryGetValue(playerId, out var peerId))
        {
            toRemove.Add(playerId);
            continue;
        }
        if (_combatByPlayer.ContainsKey(playerId))
        {
            toRemove.Add(playerId);
            continue;
        }
        if (ArePositionsAdjacent(playerId, npc))
        {
            toRemove.Add(playerId);
            toOpen.Add((playerId, npcId));
            continue;
        }
        // Keep pursuing. If no progress is possible, cancel and notify.
        if (!MovePlayerAdjacentToNpc(playerId, npc))
        {
            toRemove.Add(playerId);
            ChatToPlayer(playerId, "I can't seem to reach that.");
        }
    }

    foreach (var p in toRemove) _pendingTalk.Remove(p);
    foreach (var (pid, nid) in toOpen) OpenDialogue(pid, nid);
}

    private readonly Dictionary<long, DialogueSession> _dialogueByPlayer = new();

    internal bool IsPlayerInDialogue(long playerId) => _dialogueByPlayer.ContainsKey(playerId);

    private static string ResolveDialoguePath(string name)
    {
        string path = name;
        if (!path.StartsWith("dialogue/")) path = "dialogue/" + path;
        if (!path.EndsWith(".gfs")) path += ".gfs";
        return path;
    }

    private bool HasDialogueScript(ActiveNpc npc)
    {
        if (string.IsNullOrEmpty(npc.DialogueScript)) return false;
        return _gfs.GetAst(ResolveDialoguePath(npc.DialogueScript)) != null;
    }

    internal void OpenDialogue(long playerId, long npcId)
    {
        if (!_npcs.TryGetValue(npcId, out var npc)) return;
        if (string.IsNullOrEmpty(npc.DialogueScript)) return;


        string path = ResolveDialoguePath(npc.DialogueScript);
        var ast = _gfs.GetAst(path);
        if (ast == null)
        {
            GD.PrintErr($"[Dialogue] Script not found: {path}");
            return;
        }
        if (ast.Namespace != "server" || ast.Section != "dialogue")
        {
            GD.PrintErr($"[Dialogue] Script {path} is not a [server][dialogue] file");
            return;
        }
        if (ast.Topics == null || !ast.Topics.ContainsKey("start"))
        {
            GD.PrintErr($"[Dialogue] Script {path} missing 'topic start:'");
            return;
        }

        CancelTradeForPlayer(playerId, ErrorKeys.TradeCancelDialogue);
        CancelTradeQueuesForPlayer(playerId, ErrorKeys.TradeCancelDialogue);
        CancelBankForPlayer(playerId, "");
        CancelPendingBankFor(playerId);
        _perTick?.EndByStem(playerId, "follow_player");
        RemovePlayerIntent(playerId);
        _pendingPickup.Remove(playerId);
        _pendingEngage.Remove(playerId);
        if (_combatByPlayer.TryGetValue(playerId, out var pair) && !pair.StateAnnounced)
            EndCombat(pair);
        
        SnapAndFaceForDialogue(playerId, npc);

        var session = new DialogueSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            PlayerId = playerId,
            NpcId = npcId,
            ScriptPath = path,
            Ast = ast,
            CurrentTopic = "start",
        };
        _dialogueByPlayer[playerId] = session;

        ExecuteAndSendTopic(session, npc);
    }

    internal void HandleDialogueChoose(long peerId, JsonElement root)
    {
        if (!_sessions.TryGetValue(peerId, out var player)) return;
        if (!_dialogueByPlayer.TryGetValue(player.PlayerId, out var session)) return;
        if (!root.TryGetProperty("session_id", out var sidEl) || sidEl.GetString() != session.SessionId) return;
        if (!root.TryGetProperty("goto", out var gotoEl) || gotoEl.ValueKind != JsonValueKind.String) return;
        string goto_ = gotoEl.GetString() ?? "";
        if (goto_ == "end")
        {
            CloseDialogue(session, silent: true);
            return;
        }
        if (session.Ast.Topics == null || !session.Ast.Topics.ContainsKey(goto_))
        {
            GD.PrintErr($"[Dialogue] Invalid goto '{goto_}' in {session.ScriptPath}");
            return;
        }
        if (!_npcs.TryGetValue(session.NpcId, out var npc))
        {
            CloseDialogue(session, silent: true);
            return;
        }
        session.CurrentTopic = goto_;
        ExecuteAndSendTopic(session, npc);
    }

    internal void CancelDialogueForPlayer(long playerId, string errorKey)
    {
        if (!_dialogueByPlayer.TryGetValue(playerId, out var session)) return;
        CloseDialogue(session, silent: string.IsNullOrEmpty(errorKey), errorKey);
    }

    private void ExecuteAndSendTopic(DialogueSession session, ActiveNpc npc)
    {
        if (!_sessions.TryGetValue(GetPeerIdForPlayer(session.PlayerId), out var player))
        {
            CloseDialogue(session, silent: true);
            return;
        }
        if (session.Ast.Topics == null || !session.Ast.Topics.TryGetValue(session.CurrentTopic, out var topic))
        {
            CloseDialogue(session, silent: true);
            return;
        }

        var ctx = CreateDialogueContext(session, player, npc);
        try
        {
            Gfs.Interpreter.ExecuteDialogueTopic(session.Ast, topic, ctx, _gfsFns);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Dialogue] Runtime error in {session.ScriptPath} '{session.CurrentTopic}': {e.Message}");
            CloseDialogue(session, silent: true);
            return;
        }

        string say = ctx.Locals.TryGetValue("say", out var s) ? s?.ToString() ?? "" : "";
        var options = ExtractOptions(ctx);
        SendDialogueState(session, npc, say, options);
    }

    private Gfs.CallContext CreateDialogueContext(DialogueSession session, PlayerSession player, ActiveNpc npc)
    {
        int credits = _creditsByPlayer.TryGetValue(session.PlayerId, out var c) ? c : 0;
        var combat = _playerCombat.TryGetValue(session.PlayerId, out var cd) ? cd : null;
        var system = new Dictionary<string, object>
        {
            ["SELF_ID"] = session.PlayerId,
            ["username"] = player.Username,
            ["PLAYER_CREDITS"] = (long)credits,
            ["PLAYER_HP"] = (long)(combat?.CurrentHp ?? 10),
            ["PLAYER_MAX_HP"] = (long)(combat?.MaxHp ?? 10),
            ["npc_id"] = session.NpcId,
            ["npc_display"] = npc.Display,
            ["npc_name"] = npc.Name,
        };
        return new Gfs.CallContext
        {
            System = system,
            Locals = new Dictionary<string, object>(),
            Args = new Dictionary<string, object>(),
            SelfPlayerId = session.PlayerId,
            ScriptPath = session.ScriptPath,
            TaskLookup = _gfs.GetTask,
        };
    }

    private static List<Dictionary<string, object>> ExtractOptions(Gfs.CallContext ctx)
    {
        var result = new List<Dictionary<string, object>>();
        if (!ctx.Locals.TryGetValue("options", out var raw)) return result;
        if (raw is not List<object?> list) return result;
        int index = 1;
        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> dict) continue;
            string goto_ = dict.TryGetValue("goto", out var g) ? g?.ToString() ?? "" : "";
            string label = dict.TryGetValue("label", out var l) ? l?.ToString() ?? "" : "";
            if (string.IsNullOrEmpty(goto_) || string.IsNullOrEmpty(label)) continue;
            result.Add(new Dictionary<string, object>
            {
                ["index"] = index,
                ["label"] = label,
                ["goto"] = goto_,
            });
            index++;
        }
        return result;
    }

    private void SendDialogueState(
        DialogueSession session, ActiveNpc npc, string say,
        List<Dictionary<string, object>> options)
    {
        if (!_peerByPlayerId.TryGetValue(session.PlayerId, out var peerId)) return;
        Send(peerId, new
        {
            type = session.IsOpened ? "dialogue_state" : "dialogue_open",
            session_id = session.SessionId,
            npc_id = session.NpcId,
            npc_display = npc.Display,
            node = session.CurrentTopic,
            text = say,
            options = options,
        });
        session.IsOpened = true;
    }

    private void CloseDialogue(DialogueSession session, bool silent, string errorKey = "")
    {
        if (!_dialogueByPlayer.TryGetValue(session.PlayerId, out var current)
            || current.SessionId != session.SessionId) return;
        _dialogueByPlayer.Remove(session.PlayerId);
        if (!_peerByPlayerId.TryGetValue(session.PlayerId, out var peerId)) return;
        Send(peerId, new
        {
            type = "dialogue_close",
            session_id = session.SessionId,
            error_key = silent ? "" : errorKey,
            error_params = new { },
        });
    }

    private long GetPeerIdForPlayer(long playerId)
    {
        if (_peerByPlayerId.TryGetValue(playerId, out var peerId)) return peerId;
        return -1;
    }
}