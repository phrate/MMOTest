using System.Collections.Generic;
using Godot;
using Xunit;
using MMOTest.Server;

namespace MMOTest.Server.Tests;

public class PathfinderTests
{
    private static Pathfinder Build(HashSet<Vector2I> walkable)
    {
        return new Pathfinder((x, y) =>
        {
            var v = new Vector2I(x, y);
            bool w = walkable.Contains(v);
            return (w, w);  // For test scenarios: tile exists iff it's walkable
        });
    }

    // --- Basic pathfinding ---

    [Fact]
    public void FindPath_StraightLine_Returns_Corridor()
    {
        var walkable = new HashSet<Vector2I>
        {
            new(0, 0), new(1, 0), new(2, 0), new(3, 0)
        };
        var path = Build(walkable).FindPath(0, 0, 3, 0);
        Assert.Equal(new[] { new Vector2I(1, 0), new Vector2I(2, 0), new Vector2I(3, 0) }, path);
    }

    [Fact]
    public void FindPath_Diagonal_OpenArea_TakesDiagonal()
    {
        // 3x3 open area centered on start
        var walkable = new HashSet<Vector2I>();
        for (int x = 0; x <= 3; x++)
            for (int y = 0; y <= 3; y++)
                walkable.Add(new Vector2I(x, y));

        var path = Build(walkable).FindPath(0, 0, 2, 2);
        // Diagonal should be allowed since all corners are walkable
        Assert.Equal(new Vector2I(2, 2), path[^1]);
        Assert.Equal(2, path.Count);  // Direct diagonal: [(1,1), (2,2)]
    }

    [Fact]
    public void FindPath_Obstacle_DetoursAround()
    {
        // Corridor with a wall at (2,0)
        // (0,0) - (1,0) - WALL - (3,0) - (4,0)
        //                 \
        //                 (2,1)  <- detour tile
        var walkable = new HashSet<Vector2I>
        {
            new(0, 0), new(1, 0), new(3, 0), new(4, 0),
            new(2, 1)  // detour; but need to connect to it
        };
        // (1,0) -> (2,1) is diagonal; corners (2,0) and (1,1) both must be walkable.
        // (2,0) is wall. So diagonal is blocked. This path is unreachable.
        // Better test: 3-wide corridor
        walkable = new HashSet<Vector2I>
        {
            new(0, 0), new(1, 0), new(3, 0), new(4, 0),
            new(0, 1), new(1, 1), new(2, 1), new(3, 1), new(4, 1)
        };
        var path = Build(walkable).FindPath(0, 0, 4, 0);
        Assert.Equal(new Vector2I(4, 0), path[^1]);
        // Path must go around the wall, not through (2,0)
        Assert.DoesNotContain(new Vector2I(2, 0), path);
        Assert.Contains(new Vector2I(2, 1), path);
    }

    [Fact]
    public void FindPath_Diagonal_BlockedByCorner_DoesNotCut()
    {
        // (0,0) walkable, (1,0) BLOCKED, (0,1) walkable, (1,1) walkable
        // Diagonal (0,0)→(1,1) should not be taken because corner (1,0) is blocked.
        var walkable = new HashSet<Vector2I>
        {
            new(0, 0), new(0, 1), new(1, 1)
        };
        var path = Build(walkable).FindPath(0, 0, 1, 1);
        // Must go through (0,1), not direct
        Assert.Equal(2, path.Count);
        Assert.Equal(new Vector2I(0, 1), path[0]);
        Assert.Equal(new Vector2I(1, 1), path[1]);
    }

    // --- Edge cases ---

    [Fact]
    public void FindPath_StartEqualsGoal_ReturnsEmpty()
    {
        var walkable = new HashSet<Vector2I> { new(0, 0) };
        var path = Build(walkable).FindPath(0, 0, 0, 0);
        Assert.Empty(path);
    }

    [Fact]
    public void FindPath_StartIsVoid_ReturnsEmpty()
    {
        var walkable = new HashSet<Vector2I> { new(1, 0), new(2, 0) };
        var path = Build(walkable).FindPath(0, 0, 2, 0);
        Assert.Empty(path);  // start not in walkable set
    }

        [Fact]
    public void FindPath_Unreachable_ReturnsPartialPath()
    {
        // Player at (0,0) wants to reach (5,0). Tiles (2,0), (3,0), (4,0) are
        // not walkable, so the goal is isolated. The reachable pocket is
        // {(0,0), (1,0)}. Chebyshev distance to goal:
        //   (0,0) -> 5
        //   (1,0) -> 4  (strictly closer)
        // So the BFS's best-tile tracking stops at (1,0) and returns a path
        // to it — "as close as you can get" behavior.
        var walkable = new HashSet<Vector2I>
        {
            new(0, 0), new(1, 0),   // reachable pocket (start + one tile)
            new(5, 0)               // isolated goal
        };
        var path = Build(walkable).FindPath(0, 0, 5, 0);
        Assert.NotEmpty(path);
        Assert.Equal(new Vector2I(1, 0), path[^1]);
    }

    [Fact]
    public void FindPath_TrivialOneTile_ReturnsSingleTile()
    {
        var walkable = new HashSet<Vector2I> { new(0, 0), new(1, 0) };
        var path = Build(walkable).FindPath(0, 0, 1, 0);
        Assert.Single(path);
        Assert.Equal(new Vector2I(1, 0), path[0]);
    }

    [Fact]
    public void FindPath_AlreadyBlockedStart_StillExpands()
    {
        // Start exists but blocked. Should still attempt to reach goal through
        // walkable neighbors — matches "spawned on bad tile" recovery behavior.
        var walkable = new HashSet<Vector2I>
        {
            new(0, 0),   // start tile
            new(1, 0), new(2, 0)  // path forward
        };
        // Make start blocked but present
        var pf = new Pathfinder((x, y) =>
        {
            if (x == 0 && y == 0) return (true, false);  // exists, blocked
            var v = new Vector2I(x, y);
            return (walkable.Contains(v), walkable.Contains(v));
        });
        var path = pf.FindPath(0, 0, 2, 0);
        Assert.NotEmpty(path);
        Assert.Equal(new Vector2I(2, 0), path[^1]);
    }

    // --- FindAdjacentWalkable ---

    [Fact]
    public void FindAdjacentWalkable_Returns_OrthogonalFirst()
    {
        var walkable = new HashSet<Vector2I>
        {
            new(1, 0),   // orth: right
            new(1, 1)    // diag: southeast (would be returned only if (1,0) were blocked)
        };
        var result = Build(walkable).FindAdjacentWalkable(0, 0);
        Assert.Single(result);
        Assert.Equal(new Vector2I(1, 0), result[0]);  // orth preferred over diag
    }

    [Fact]
    public void FindAdjacentWalkable_NoneAvailable_ReturnsEmpty()
    {
        var walkable = new HashSet<Vector2I> { new(0, 0) };
        var result = Build(walkable).FindAdjacentWalkable(0, 0);
        Assert.Empty(result);
    }

    [Fact]
    public void FindAdjacentWalkable_PicksFirstAvailable_ByDirectionOrder()
    {
        // Only east and west are walkable. Direction order is (1,0), (-1,0), ...
        // So east should win.
        var walkable = new HashSet<Vector2I> { new(1, 0), new(-1, 0) };
        var result = Build(walkable).FindAdjacentWalkable(0, 0);
        Assert.Single(result);
        Assert.Equal(new Vector2I(1, 0), result[0]);
    }

    [Fact]
    public void FindPath_ReachablePocketNotCloserToGoal_ReturnsEmpty()
    {
        // Reachable pocket is {(0,0), (1,0)}. Goal is (10,10).
        // Chebyshev dist from (0,0) to goal = 10, from (1,0) = 10. Tie.
        // best never advances past start, so no path is returned.
        // This is the "already as close as you can get" case — start tile
        // IS the best-effort destination, and start is excluded from the
        // returned path.
        var walkable = new HashSet<Vector2I>
        {
            new(0, 0), new(1, 0),
            new(10, 10), new(11, 10)
        };
        var path = Build(walkable).FindPath(0, 0, 10, 10);
        Assert.Empty(path);
    }
}