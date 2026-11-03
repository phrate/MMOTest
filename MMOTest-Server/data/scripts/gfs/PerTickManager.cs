using Godot;
using System;
using System.Collections.Generic;
using MMOTest.Server;


namespace MMOTest.Server.Gfs;

/// <summary>
/// Tracks &lt;once_per_tick&gt; scripts. Each active script keeps its own persisted
/// locals so counters, caches, etc. survive between ticks. Re-triggering the same
/// (player, script) pair replaces the active instance with fresh locals/args.
/// </summary>
public sealed class PerTickManager
{
    private sealed class Active
    {
        public long PlayerId;
        public string ScriptPath = "";
        public SuiteNode Ast = null!;
        public CallContext Ctx = new();
    }

    private readonly Dictionary<(long, string), Active> _active = new();
    private readonly FunctionRegistry _functions;

    public PerTickManager(FunctionRegistry functions) { _functions = functions; }

    public void Start(long playerId, string scriptPath, SuiteNode ast, CallContext ctx)
    {
        var key = (playerId, scriptPath);
        _active[key] = new Active
        {
            PlayerId = playerId,
            ScriptPath = scriptPath,
            Ast = ast,
            Ctx = ctx,
        };
    }

    public void End(long playerId, string scriptPath) => _active.Remove((playerId, scriptPath));

    public void EndAllFor(long playerId)
    {
        var toRemove = new List<(long, string)>();
        foreach (var kv in _active) if (kv.Key.Item1 == playerId) toRemove.Add(kv.Key);
        foreach (var k in toRemove) _active.Remove(k);
    }


    /// <summary>
    /// Ends a per-tick script for this player by filename stem. Matches against
    /// the last path segment, so "follow_player" matches "player/follow_player.gfs"
    /// and "follow_player.gfs" matches too. Returns true if at least one active
    /// script was removed.
    /// </summary>
    public bool EndByStem(long playerId, string stem)
    {
        if (string.IsNullOrEmpty(stem)) return false;
        if (!stem.EndsWith(".gfs")) stem += ".gfs";

        var toRemove = new List<(long, string)>();
        foreach (var kv in _active)
        {
            if (kv.Key.Item1 != playerId) continue;
            var path = kv.Value.ScriptPath;
            int slash = path.LastIndexOf('/');
            var name = slash >= 0 ? path.Substring(slash + 1) : path;
            if (name == stem) toRemove.Add(kv.Key);
        }
        foreach (var k in toRemove) _active.Remove(k);
        return toRemove.Count > 0;
    }


    /// <summary>
    /// Called once per tick. Runs every active script once; removes ended/errored.
    /// Returns effects grouped by player so the host can route them.
    /// </summary>
    public List<(long PlayerId, List<Dictionary<string, object>> Effects)> TickAll()
    {
        var outList = new List<(long, List<Dictionary<string, object>>)>();
        // Snapshot keys. Scripts can call RUN (which adds entries) or _end_script
        // (which removes them) while we're running, so we must not enumerate
        // _active directly. New entries added this tick run next tick — which is
        // the correct "start next tick" semantics anyway.
        var keys = new List<(long, string)>(_active.Keys);

        //if (keys.Count > 0)
        //    GD.Print($"[DIAG] TickAll: active={keys.Count}");

        foreach (var key in keys)
        {
            // An earlier script in this tick may have ended or replaced this one.
            if (!_active.TryGetValue(key, out var a)) continue;

            a.Ctx.EndScriptRequested = false;
            try
            {
                var effects = Interpreter.Run(a.Ast, a.Ctx, _functions);
                if (effects.Count > 0) outList.Add((a.PlayerId, effects));
                if (a.Ctx.EndScriptRequested) _active.Remove(key);
            }
            catch (Exception e)
            {
                Diag.Error("diag_gfs", $"per-tick error in {a.ScriptPath} (player {a.PlayerId}): {e.Message}");
                _active.Remove(key);
            }
        }
        return outList;
    }
    public Dictionary<string, object>? GetArgsForActive(long playerId, string stem)
    {
        if (!stem.EndsWith(".gfs")) stem += ".gfs";
        foreach (var kv in _active)
        {
            if (kv.Key.Item1 != playerId) continue;
            var path = kv.Value.ScriptPath;
            int slash = path.LastIndexOf('/');
            var name = slash >= 0 ? path.Substring(slash + 1) : path;
            if (name == stem) return kv.Value.Ctx.Args;
        }
        return null;
    }
}

