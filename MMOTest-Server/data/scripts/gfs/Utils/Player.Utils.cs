using Godot;
using System;
using System.Collections.Generic;

namespace MMOTest.Server.Gfs.Utils;

public static class PlayerUtils
{
    public static void Register(FunctionRegistry reg, Main main)
    {
        reg.Register("Player.MoveToNearestTile", (ctx, args) =>
        {
            if (args.Count < 1) return new List<object?>();
            var tile = AsTile(args[0]);
            if (tile == null)
            {
                ctx.System["SELF_PATHFINDING_ROUTE"] = new List<object?>();
                return new List<object?>();
            }
            var route = main.PathfindToNearest(ctx.SelfPlayerId, tile.Value.X, tile.Value.Y);
            var goal = new Vector2I(tile.Value.X, tile.Value.Y);
            if (route.Count > 0 && route[^1] == goal)
                route.RemoveAt(route.Count - 1);
            var outList = new List<object?>();
            foreach (var t in route) outList.Add(new List<object?> { (long)t.X, (long)t.Y });
            ctx.System["SELF_PATHFINDING_ROUTE"] = outList;
            if (route.Count > 0) main.SetIntent(ctx.SelfPlayerId, route);
            return outList;
        });

        reg.Register("_end_script", (ctx, args) =>
        {
            if (args.Count == 0) { ctx.EndScriptRequested = true; return null; }
            string stem = ExtractStem(args[0], ctx.CurrentCall);
            if (string.IsNullOrEmpty(stem)) return null;
            main.EndPerTickScript(ctx.SelfPlayerId, stem);
            var current = ctx.ScriptPath;
            int slash = current.LastIndexOf('/');
            var currentName = slash >= 0 ? current.Substring(slash + 1) : current;
            if (currentName == stem) ctx.EndScriptRequested = true;
            return null;
        });


        // GetPlayerFollowingActive(player_id) -> player_id currently being followed, or null.
        reg.Register("GetPlayerFollowingActive", (ctx, args) =>
        {
            long pid = args.Count >= 1 && args[0] != null
                ? Convert.ToInt64(args[0])
                : ctx.SelfPlayerId;
            var a = main.GetActiveScriptArgs(pid, "follow_player");
            if (a == null) return null;
            if (a.TryGetValue("target_id", out var t1)) return t1;
            if (a.TryGetValue("follow_id", out var t2)) return t2;
            return null;
        });

        // GetPlayerAdjacentToTile(tile) -> bool. Self is implicit.
        reg.Register("GetPlayerAdjacentToTile", (ctx, args) =>
        {
            if (args.Count < 1) return false;
            var tile = AsTile(args[0]);
            if (tile == null) return false;
            if (!main.TryGetPosition(ctx.SelfPlayerId, out var pos)) return false;
            int sx = (int)Math.Round(pos.X);
            int sy = (int)Math.Round(pos.Z);
            int dx = Math.Abs(sx - tile.Value.X);
            int dy = Math.Abs(sy - tile.Value.Y);
            return dx <= 1 && dy <= 1 && !(dx == 0 && dy == 0);
        });
    }

    private static string ExtractStem(object? value, CallNode? call)
    {
        if (value is string s && !string.IsNullOrEmpty(s))
        {
            var name = s;
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            if (!name.EndsWith(".gfs")) name += ".gfs";
            return name;
        }
        if (call != null && call.Arguments.Count > 0 && call.Arguments[0] is RefNode r && r.RefKind == "local")
        {
            var name = r.Name;
            if (!name.EndsWith(".gfs")) name += ".gfs";
            return name;
        }
        return "";
    }

    private static (int X, int Y)? AsTile(object? v)
    {
        if (v is List<object?> list && list.Count >= 2)
            return ((int)Convert.ToInt64(list[0]), (int)Convert.ToInt64(list[1]));
        return null;
    }
}