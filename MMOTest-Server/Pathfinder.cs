using Godot;
using System;
using System.Collections.Generic;

namespace MMOTest.Server;

/// <summary>
/// Pure BFS pathfinding over the world tile grid. Extracted from Main so the
/// search logic can be unit-tested without loading a world file or spinning
/// up Godot's SceneTree. The tile-lookup function is injected by the caller
/// and returns (Exists, Walkable) for a tile coordinate.
///
/// Rules:
///   - 8-connected (including diagonals).
///   - Diagonal moves are permitted only if BOTH shared corners are walkable.
///   - The start tile must exist; blocked-but-existing start is allowed (the
///     search proceeds from there, expanding to walkable neighbors).
///   - Unreachable targets return the best-effort path to the tile closest to
///     the goal, matching the existing "move as close as you can" semantics.
///   - Search is bounded by MaxExpansions (20000) and falls back to the best
///     tile found so far.
/// </summary>
public sealed class Pathfinder
{
    private readonly Func<int, int, (bool Exists, bool Walkable)> _tileLookup;
    private const int MaxExpansions = 20000;

    public Pathfinder(Func<int, int, (bool Exists, bool Walkable)> tileLookup)
    {
        _tileLookup = tileLookup;
    }

    public List<Vector2I> FindPath(int startX, int startY, int goalX, int goalY)
    {
        var (startExists, _) = _tileLookup(startX, startY);
        if (!startExists) return new();

        var queue = new Queue<Vector2I>();
        var cameFrom = new Dictionary<Vector2I, Vector2I?>();
        var start = new Vector2I(startX, startY);
        var goal = new Vector2I(goalX, goalY);
        queue.Enqueue(start);
        cameFrom[start] = null;

        Vector2I[] dirs = {
            new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
            new(1, 1), new(1, -1), new(-1, 1), new(-1, -1)
        };

        Vector2I? best = null;
        int bestDist = int.MaxValue;
        int expansions = 0;

        while (queue.Count > 0)
        {
            if (++expansions > MaxExpansions) break;

            var cur = queue.Dequeue();
            int d = Math.Max(Math.Abs(cur.X - goal.X), Math.Abs(cur.Y - goal.Y));
            if (d < bestDist) { bestDist = d; best = cur; }
            if (cur == goal) break;

            foreach (var dir in dirs)
            {
                var next = cur + dir;
                if (cameFrom.ContainsKey(next)) continue;

                var (nextExists, nextWalkable) = _tileLookup(next.X, next.Y);
                if (!nextExists || !nextWalkable) continue;

                if (dir.X != 0 && dir.Y != 0)
                {
                    var (aExists, aWalkable) = _tileLookup(cur.X + dir.X, cur.Y);
                    if (!aExists || !aWalkable) continue;
                    var (bExists, bWalkable) = _tileLookup(cur.X, cur.Y + dir.Y);
                    if (!bExists || !bWalkable) continue;
                }

                cameFrom[next] = cur;
                queue.Enqueue(next);
            }
        }

        if (best == null) return new();

        var path = new List<Vector2I>();
        Vector2I? node = best;
        while (node.HasValue && node.Value != start)
        {
            path.Insert(0, node.Value);
            node = cameFrom[node.Value];
        }
        return path;
    }

    public List<Vector2I> FindAdjacentWalkable(int x, int y)
    {
        Vector2I[] dirs = {
            new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
            new(1, 1), new(1, -1), new(-1, 1), new(-1, -1)
        };
        foreach (var d in dirs)
        {
            var cand = new Vector2I(x + d.X, y + d.Y);
            var (exists, walkable) = _tileLookup(cand.X, cand.Y);
            if (exists && walkable)
                return new List<Vector2I> { cand };
        }
        return new List<Vector2I>();
    }
}