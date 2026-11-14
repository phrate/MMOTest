using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns NPC dialogue: topic state machine, option dispatch, walk-to-talk,
/// snap-and-face on arrival, session lifecycle.
///
/// Coupling note (temporary): holds Main for Send / ChatToPlayer / cross-cutting
/// cancels and NPC lookups. Phase 4's Coordinator + OutboundChannel absorb these.
/// </summary>
internal sealed class DialogueSystem
{
    private readonly Main _main;
    private readonly ServerState _state;
    private readonly Gfs.GfsManager _gfs;
    private readonly Gfs.FunctionRegistry _gfsFns;
    private readonly Gfs.PerTickManager _perTick;

    private readonly Dictionary<long, long> _pendingTalk = new(); // playerId -> npcId

    public DialogueSystem(Main main, ServerState state,
                          Gfs.GfsManager gfs, Gfs.FunctionRegistry gfsFns,
                          Gfs.PerTickManager perTick)
    {
        _main = main;
        _state = state;
        _gfs = gfs;
        _gfsFns = gfsFns;
        _perTick = perTick;
    }

    internal void ClearPendingFor(long playerId) => _pendingTalk.Remove(playerId);

    internal bool IsPlayerInDialogue(long playerId) => _state.DialogueByPlayer.ContainsKey(playerId);

    private static string ResolveDialoguePath(string name)
    {
        string path = name;
        if (!path.StartsWith("dialogue/")) path = "dialogue/" + path;
        if (!path.EndsWith(".gfs")) path += ".gfs";
        return path;
    }

    internal bool HasDialogueScript(ActiveNpc npc)
    {
        if (string.IsNullOrEmpty(npc.DialogueScript)) return false;
        return _gfs.GetAst(ResolveDialoguePath(npc.DialogueScript)) != null;
    }

    // ============================================================
    // Entry: request to talk, walk-to-approach, per-tick promote
    // ============================================================

    internal void RequestTalk(long playerId, long npcId)
    {
        if (!_main.TryGetNpcById(npcId, out var npc)) return;
        if (npc.IsDead) return;
        if (string.IsNullOrEmpty(npc.DialogueScript))
        {
            _main.ChatToPlayer(playerId, "They have nothing to say.");
            return;
        }
        if (!_state.PeerByPlayerId.ContainsKey(playerId) || !_state.Positions.ContainsKey(playerId)) return;

        if (_pendingTalk.TryGetValue(playerId, out var currentNpcId) && currentNpcId == npcId) return;

        _state.PendingEngage.Remove(playerId);
        _state.PendingPickup.Remove(playerId);
        _perTick?.EndByStem(playerId, "follow_player");
        _main.RemovePlayerIntent(playerId);

        CancelDialogueForPlayer(playerId, "");

        _pendingTalk[playerId] = npcId;

        if (!_main.ArePositionsAdjacent(playerId, npc))
            _main.MovePlayerAdjacentToNpc(playerId, npc);
    }

    internal void CancelPendingTalkFor(long playerId) => _pendingTalk.Remove(playerId);

    internal void ProcessPendingTalk()
    {
        if (_pendingTalk.Count == 0) return;

        var toRemove = new List<long>();
        var toOpen = new List<(long PlayerId, long NpcId)>();

        foreach (var kv in _pendingTalk)
        {
            long playerId = kv.Key;
            long npcId = kv.Value;

            if (!_main.TryGetNpcById(npcId, out var npc) || npc.IsDead
                || string.IsNullOrEmpty(npc.DialogueScript))
            {
                toRemove.Add(playerId);
                continue;
            }
            if (!_state.Positions.ContainsKey(playerId)
                || !_state.PeerByPlayerId.TryGetValue(playerId, out var peerId))
            {
                toRemove.Add(playerId);
                continue;
            }
            if (_state.CombatByPlayer.ContainsKey(playerId))
            {
                toRemove.Add(playerId);
                continue;
            }
            if (_main.ArePositionsAdjacent(playerId, npc))
            {
                toRemove.Add(playerId);
                toOpen.Add((playerId, npcId));
                continue;
            }
            if (!_main.MovePlayerAdjacentToNpc(playerId, npc))
            {
                toRemove.Add(playerId);
                _main.ChatToPlayer(playerId, "I can't seem to reach that.");
            }
        }

        foreach (var p in toRemove) _pendingTalk.Remove(p);
        foreach (var (pid, nid) in toOpen) OpenDialogue(pid, nid);
    }

    // ============================================================
    // Session open / dispatch / close
    // ============================================================

    private void OpenDialogue(long playerId, long npcId)
    {
        if (!_main.TryGetNpcById(npcId, out var npc)) return;
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

        _main.CancelTradeForPlayer(playerId, ErrorKeys.TradeCancelDialogue);
        _main.CancelTradeQueuesForPlayer(playerId, ErrorKeys.TradeCancelDialogue);
        _main.CancelBankForPlayer(playerId, "");
        _main.CancelPendingBankFor(playerId);
        _perTick?.EndByStem(playerId, "follow_player");
        _main.RemovePlayerIntent(playerId);
        _state.PendingPickup.Remove(playerId);
        _state.PendingEngage.Remove(playerId);
        if (_state.CombatByPlayer.TryGetValue(playerId, out var pair) && !pair.StateAnnounced)
            _main.EndCombat(pair);

        _main.SnapAndFaceForDialogue(playerId, npc);

        var session = new DialogueSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            PlayerId = playerId,
            NpcId = npcId,
            ScriptPath = path,
            Ast = ast,
            CurrentTopic = "start",
        };
        _state.DialogueByPlayer[playerId] = session;

        ExecuteAndSendTopic(session, npc);
    }

    internal void HandleDialogueChoose(long peerId, JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var player)) return;
        if (!_state.DialogueByPlayer.TryGetValue(player.PlayerId, out var session)) return;
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
        if (!_main.TryGetNpcById(session.NpcId, out var npc))
        {
            CloseDialogue(session, silent: true);
            return;
        }
        session.CurrentTopic = goto_;
        ExecuteAndSendTopic(session, npc);
    }

    internal void CancelDialogueForPlayer(long playerId, string errorKey)
    {
        if (!_state.DialogueByPlayer.TryGetValue(playerId, out var session)) return;
        CloseDialogue(session, silent: string.IsNullOrEmpty(errorKey), errorKey);
    }

    private void ExecuteAndSendTopic(DialogueSession session, ActiveNpc npc)
    {
        if (!_state.Sessions.TryGetValue(_main.GetPeerIdForPlayer(session.PlayerId), out var player))
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
        int credits = _state.Credits.TryGetValue(session.PlayerId, out var c) ? c : 0;
        var combat = _state.PlayerCombat.TryGetValue(session.PlayerId, out var cd) ? cd : null;
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

    private void SendDialogueState(DialogueSession session, ActiveNpc npc, string say,
                                    List<Dictionary<string, object>> options)
    {
        if (!_state.PeerByPlayerId.TryGetValue(session.PlayerId, out var peerId)) return;
        _main.Send(peerId, new
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
        if (!_state.DialogueByPlayer.TryGetValue(session.PlayerId, out var current)
            || current.SessionId != session.SessionId) return;
        _state.DialogueByPlayer.Remove(session.PlayerId);
        if (!_state.PeerByPlayerId.TryGetValue(session.PlayerId, out var peerId)) return;
        _main.Send(peerId, new
        {
            type = "dialogue_close",
            session_id = session.SessionId,
            error_key = silent ? "" : errorKey,
            error_params = new { },
        });
    }
}