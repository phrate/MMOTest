using System;

namespace MMOTest.Server.Gfs.Utils;

internal static class TilesUtils
{
    public static void Register(FunctionRegistry reg, ServerState state, WorldService world)
    {
        reg.Register("GetPlayerTile", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long pid = System.Convert.ToInt64(args[0]);
            if (!state.Positions.TryGetValue(pid, out var pos)) return null;
            return new System.Collections.Generic.List<object?>
            {
                (long)System.MathF.Round(pos.X),
                (long)System.MathF.Round(pos.Z),
            };
        });

        reg.Register("TileDistanceMoreThanOne", (ctx, args) =>
        {
            if (args.Count < 2) return false;
            var a = AsTile(args[0]);
            var b = AsTile(args[1]);
            if (a == null || b == null) return false;
            int dx = Math.Abs(a.Value.X - b.Value.X);
            int dy = Math.Abs(a.Value.Y - b.Value.Y);
            if (dx > 1 || dy > 1) return true;
            if (dx == 1 && dy == 1 && IsCornerCut(a.Value.X, a.Value.Y, b.Value.X, b.Value.Y, world))
                return true;
            return false;
        });

        reg.Register("IsSameTile", (ctx, args) =>
        {
            if (args.Count < 2) return false;
            var a = AsTile(args[0]);
            var b = AsTile(args[1]);
            if (a == null || b == null) return false;
            return a.Value.X == b.Value.X && a.Value.Y == b.Value.Y;
        });
    }

    private static (int X, int Y)? AsTile(object? v)
    {
        if (v is System.Collections.Generic.List<object?> list && list.Count >= 2)
            return ((int)System.Convert.ToInt64(list[0]), (int)System.Convert.ToInt64(list[1]));
        return null;
    }

    private static bool IsCornerCut(int fromX, int fromY, int toX, int toY, WorldService world)
    {
        int dx = toX - fromX;
        int dy = toY - fromY;
        if (Math.Abs(dx) != 1 || Math.Abs(dy) != 1) return false;
        if (!world.IsWalkable(fromX + dx, fromY)) return true;
        if (!world.IsWalkable(fromX, fromY + dy)) return true;
        return false;
    }
}