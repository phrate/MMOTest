namespace MMOTest.Server.Gfs.Utils;

public static class TilesUtils
{
    public static void Register(FunctionRegistry reg, Main main)
    {
        // GetPlayerTile(id) -> [x, y] tile array, or null if the player isn't online.
        reg.Register("GetPlayerTile", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long pid = System.Convert.ToInt64(args[0]);
            if (!main.TryGetPosition(pid, out var pos)) return null;
            return new System.Collections.Generic.List<object?>
            {
                (long)System.MathF.Round(pos.X),
                (long)System.MathF.Round(pos.Z),
            };
        });

        // TileDistanceMoreThanOne(a, b) -> true if Chebyshev distance > 1.
        // Returns false when either argument is null (safer than crashing).
        reg.Register("TileDistanceMoreThanOne", (ctx, args) =>
        {
            if (args.Count < 2) return false;
            var a = AsTile(args[0]);
            var b = AsTile(args[1]);
            if (a == null || b == null) return false;
            int dx = System.Math.Abs(a.Value.X - b.Value.X);
            int dy = System.Math.Abs(a.Value.Y - b.Value.Y);
            return dx > 1 || dy > 1;
        });
    }

    private static (int X, int Y)? AsTile(object? v)
    {
        if (v is System.Collections.Generic.List<object?> list && list.Count >= 2)
            return ((int)System.Convert.ToInt64(list[0]), (int)System.Convert.ToInt64(list[1]));
        return null;
    }
}