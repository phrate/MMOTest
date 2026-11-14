using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MMOTest.Server;

/// <summary>
/// GFS packet handlers and script-execution plumbing. Owns the run_action,
/// gfs_info, and tooltips_info flows, the recursive RUN->per-tick wiring, and
/// the ScriptsChanged broadcast. Also owns the static script/tooltips path
/// arrays so Main doesn't have to.
///
/// GfsManager / FunctionRegistry / PerTickManager instances are constructed on
/// Main and passed in — they're shared with DialogueSystem, RecipeSystem, and
/// NpcSystem, so they aren't owned here.
/// </summary>
internal sealed class GfsIntegration
{
    internal static readonly string[] GfsScriptDirs =
    {
        ProjectSettings.GlobalizePath("user://data/scripts/gfs"),
        ProjectSettings.GlobalizePath("res://data/scripts/gfs"),
    };

    internal static readonly string[] TooltipsScriptDirs =
    {
        ProjectSettings.GlobalizePath("user://data/scripts/gfs/tooltips"),
        ProjectSettings.GlobalizePath("res://data/scripts/gfs/tooltips"),
    };

    internal static readonly string[] TooltipsFileCandidates =
    {
        ProjectSettings.GlobalizePath("user://tooltips.json"),
        ProjectSettings.GlobalizePath("res://data/tooltips.json"),
    };

    private readonly ServerState _state;
    private readonly OutboundChannel _outbound;
    private readonly WorldService _world;
    private readonly Gfs.GfsManager _gfs;
    private readonly Gfs.FunctionRegistry _gfsFns;
    private readonly Gfs.PerTickManager _perTick;
    private readonly Coordinator _coordinator;

    public GfsIntegration(ServerState state, OutboundChannel outbound, WorldService world,
                          Gfs.GfsManager gfs, Gfs.FunctionRegistry gfsFns,
                          Gfs.PerTickManager perTick, Coordinator coordinator)
    {
        _state = state;
        _outbound = outbound;
        _world = world;
        _gfs = gfs;
        _gfsFns = gfsFns;
        _perTick = perTick;
        _coordinator = coordinator;
        _gfs.ScriptsChanged += OnGfsScriptsChanged;
    }

    // ============================================================
    // run_action — execute a [server][generic] script for a player
    // ============================================================

    internal void HandleRunAction(long peerId, JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out PlayerSession session))
        {
            GD.Print("[DIAG] run_action: no session");
            return;
        }
        if (!root.TryGetProperty("script", out var scriptEl))
        {
            GD.Print("[DIAG] run_action: no script");
            return;
        }
        string scriptPath = scriptEl.GetString() ?? "";
        var ast = _gfs.GetAst(scriptPath);
        if (ast == null)
        {
            _outbound.SendError(peerId, "run_action_result", ErrorKeys.GfsScriptNotFound);
            return;
        }

        var args = new Dictionary<string, object>();
        if (root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in argsEl.EnumerateObject())
            {
                args[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out long l) ? l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }
        }
        int targetId = 0;
        if (root.TryGetProperty("target_id", out var tidEl) && tidEl.TryGetInt32(out int tid))
            targetId = tid;

        var system = new Dictionary<string, object>
        {
            ["SELF_ID"] = session.PlayerId,
            ["PLAYER_ID"] = targetId,
            ["SELF_USERNAME"] = session.Username,
        };

        var ctx = new Gfs.CallContext
        {
            System = system,
            Args = args,
            Locals = new Dictionary<string, object>(),
            SelfPlayerId = session.PlayerId,
            ScriptPath = scriptPath,
            TaskLookup = _gfs.GetTask,
        };
        ctx.RunScript = (s, a) => RunScriptFromGfs(session.PlayerId, s, a, ctx);

        List<Dictionary<string, object>> effects;
        try
        {
            effects = Gfs.Interpreter.Run(ast, ctx, _gfsFns);
        }
        catch (Exception e)
        {
            Diag.Error("diag_gfs", $"run_action error: {e.Message}");
            _outbound.SendError(peerId, "run_action_result", ErrorKeys.GfsRuntimeError);
            return;
        }

        if (_state.Positions.TryGetValue(session.PlayerId, out var pos))
        {
            int cx = (int)MathF.Floor((float)MathF.Round(pos.X) / _world.ChunkSize);
            int cy = (int)MathF.Floor((float)MathF.Round(pos.Z) / _world.ChunkSize);
            _outbound.RouteChatEffects(session.PlayerId, session.Username, cx, cy, effects, peerId);
        }

        if (ast.Tags.Contains(Gfs.GfsTags.OncePerTick) && !ctx.EndScriptRequested)
        {
            if (scriptPath.EndsWith("follow_player.gfs"))
                _coordinator.OnFollowStarted(session.PlayerId);
            _perTick.Start(session.PlayerId, scriptPath, ast, ctx);
        }

        _outbound.Send(peerId, new { type = "run_action_result", ok = true, effects });
    }

    private void RunScriptFromGfs(long playerId, string stem,
                                  Dictionary<string, object> args, Gfs.CallContext parentCtx)
    {
        if (stem == "follow_player")
            _coordinator.OnFollowStarted(playerId);
        var (path, ast) = _gfs.ResolveByStem(stem);
        if (ast == null)
        {
            GD.PrintErr($"[Server] RUN: script not found: {stem}");
            return;
        }
        var newCtx = new Gfs.CallContext
        {
            System = new Dictionary<string, object>(parentCtx.System),
            Args = args,
            Locals = new Dictionary<string, object>(),
            SelfPlayerId = playerId,
            ScriptPath = path,
            TaskLookup = _gfs.GetTask,
        };
        newCtx.RunScript = (s, a) => RunScriptFromGfs(playerId, s, a, newCtx);
        _perTick.Start(playerId, path, ast, newCtx);
    }

    // ============================================================
    // gfs_info — ship client tooltip ASTs
    // ============================================================

    internal void HandleGfsInfo(long peerId)
    {
        if (!_state.Sessions.ContainsKey(peerId))
        {
            _outbound.SendError(peerId, "gfs_info_result", ErrorKeys.WorldNotLoggedIn);
            return;
        }
        var bundle = _gfs.BuildBundle();
        bundle["type"] = "gfs_info_result";
        bundle["ok"] = true;
        _outbound.Send(peerId, bundle);
    }

    // ============================================================
    // tooltips_info — legacy separate tooltips endpoint
    // ============================================================

    internal void HandleTooltipsInfo(long peerId)
    {
        if (!_state.Sessions.ContainsKey(peerId))
        {
            _outbound.SendError(peerId, "tooltips_info_result", ErrorKeys.WorldNotLoggedIn);
            return;
        }

        string tooltipsText = null;
        foreach (string path in TooltipsFileCandidates)
        {
            if (File.Exists(path)) { tooltipsText = File.ReadAllText(path); break; }
        }
        if (tooltipsText == null)
        {
            _outbound.SendError(peerId, "tooltips_info_result", ErrorKeys.WorldUnavailable);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(tooltipsText);
            var root = doc.RootElement;
            if (!root.TryGetProperty("format_version", out JsonElement fmt) ||
                !root.TryGetProperty("version", out JsonElement ver) ||
                !root.TryGetProperty("entries", out JsonElement entries) ||
                entries.ValueKind != JsonValueKind.Object)
            {
                _outbound.SendError(peerId, "tooltips_info_result", ErrorKeys.WorldUnavailable);
                return;
            }

            var entriesDict = new Dictionary<string, string>();
            foreach (JsonProperty entry in entries.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String) continue;
                string scriptName = entry.Value.GetString();
                if (string.IsNullOrEmpty(scriptName)) continue;
                entriesDict[entry.Name] = scriptName;
            }

            var scriptsDict = new Dictionary<string, string>();
            foreach (string dir in TooltipsScriptDirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.gfs"))
                {
                    string name = Path.GetFileName(file);
                    if (scriptsDict.ContainsKey(name)) continue;
                    try { scriptsDict[name] = File.ReadAllText(file); }
                    catch (Exception e) { GD.PrintErr($"[Server] Cannot read {file}: {e.Message}"); }
                }
            }

            if (OS.IsDebugBuild())
            {
                GD.Print($"[Server] Tooltips: entries={entriesDict.Count}, scripts={scriptsDict.Count}");
                foreach (var kv in scriptsDict)
                    GD.Print($"[Server]   script: {kv.Key} ({kv.Value.Length} bytes)");
            }

            _outbound.Send(peerId, new
            {
                type = "tooltips_info_result",
                ok = true,
                version = ver.GetInt32(),
                format_version = fmt.GetInt32(),
                entries = entriesDict,
                scripts = scriptsDict
            });
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Server] tooltips_info error: {e.Message}");
            _outbound.SendError(peerId, "tooltips_info_result", ErrorKeys.WorldUnavailable);
        }
    }

    // ============================================================
    // ScriptsChanged broadcast (wired in ctor)
    // ============================================================

    private void OnGfsScriptsChanged(Dictionary<string, Dictionary<string, object>> changed, int version)
    {
        if (_state.Sessions.Count == 0) return;
        _outbound.Broadcast(new
        {
            type = "gfs_updated",
            version,
            scripts = changed
        });
        GD.Print($"[Server] GFS: broadcast {changed.Count} changed script(s), version {version}");
    }
}