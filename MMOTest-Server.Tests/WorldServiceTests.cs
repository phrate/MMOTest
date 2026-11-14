using System.IO;
using Xunit;
using MMOTest.Server;

namespace MMOTest.Server.Tests;

public class WorldServiceTests
{
    private static string WriteTempJson(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void EnsureLoaded_MissingFile_ReturnsFalse()
    {
        var world = new WorldService(new[] { "C:/definitely/not/a/real/path.json" });
        Assert.False(world.EnsureLoaded());
        Assert.False(world.IsLoaded);
    }

    [Fact]
    public void EnsureLoaded_MalformedJson_ReturnsFalse()
    {
        string path = WriteTempJson("{ not valid json");
        var world = new WorldService(new[] { path });
        Assert.False(world.EnsureLoaded());
        Assert.False(world.IsLoaded);
    }

    [Fact]
    public void EnsureLoaded_MissingFormatVersion_ReturnsFalse()
    {
        string path = WriteTempJson(@"{ ""world_version"": 1 }");
        var world = new WorldService(new[] { path });
        Assert.False(world.EnsureLoaded());
        Assert.False(world.IsLoaded);
    }

    [Fact]
    public void EnsureLoaded_MissingWorldVersion_ReturnsFalse()
    {
        string path = WriteTempJson(@"{ ""format_version"": 2 }");
        var world = new WorldService(new[] { path });
        Assert.False(world.EnsureLoaded());
        Assert.False(world.IsLoaded);
    }

    [Fact]
    public void EnsureLoaded_ValidMinimalWorld_ReturnsTrue()
    {
        string path = WriteTempJson(@"{ ""format_version"": 2, ""world_version"": 7 }");
        var world = new WorldService(new[] { path });
        Assert.True(world.EnsureLoaded());
        Assert.True(world.IsLoaded);
        Assert.Equal(2, world.FormatVersion);
        Assert.Equal(7, world.Version);
        Assert.Equal(8, world.ChunkSize);  // default
        Assert.Equal(0, world.TileCount);
    }

    [Fact]
    public void EnsureLoaded_ValidWorldWithChunks_LoadsTiles()
    {
        string path = WriteTempJson(@"{
            ""format_version"": 2,
            ""world_version"": 3,
            ""chunk_size"": 4,
            ""chunks"": {
                ""0,0"": [
                    { ""x"": 0, ""y"": 0, ""z"": 100, ""flags"": { ""walkable"": true } },
                    { ""x"": 1, ""y"": 0, ""z"": 100, ""flags"": { ""walkable"": false } }
                ]
            }
        }");
        var world = new WorldService(new[] { path });
        Assert.True(world.EnsureLoaded());
        Assert.Equal(4, world.ChunkSize);
        Assert.Equal(2, world.TileCount);
        Assert.True(world.TryGetTile(0, 0, out var t1));
        Assert.Equal(100, t1.Z);
        Assert.True(t1.Walkable);
        Assert.True(world.TryGetTile(1, 0, out var t2));
        Assert.False(t2.Walkable);
    }

    [Fact]
    public void TryGetTile_Missing_ReturnsFalse()
    {
        string path = WriteTempJson(@"{ ""format_version"": 2, ""world_version"": 1 }");
        var world = new WorldService(new[] { path });
        world.EnsureLoaded();
        Assert.False(world.TryGetTile(99, 99, out _));
    }

    [Fact]
    public void GetTileInfo_Missing_ReturnsDefault()
    {
        string path = WriteTempJson(@"{ ""format_version"": 2, ""world_version"": 1 }");
        var world = new WorldService(new[] { path });
        world.EnsureLoaded();
        var info = world.GetTileInfo(99, 99);
        Assert.Equal(100, info.Z);
        Assert.True(info.Walkable);  // default fallback (movement code relies on this)
    }

    [Fact]
    public void IsWalkable_MissingTile_ReturnsFalse()
    {
        string path = WriteTempJson(@"{ ""format_version"": 2, ""world_version"": 1 }");
        var world = new WorldService(new[] { path });
        world.EnsureLoaded();
        Assert.False(world.IsWalkable(99, 99));
    }

    [Fact]
    public void IsWalkable_ExplicitWalkable_ReturnsTrue()
    {
        string path = WriteTempJson(@"{
            ""format_version"": 2, ""world_version"": 1,
            ""chunks"": { ""0,0"": [
                { ""x"": 5, ""y"": 5, ""z"": 100, ""flags"": { ""walkable"": true } }
            ] }
        }");
        var world = new WorldService(new[] { path });
        world.EnsureLoaded();
        Assert.True(world.IsWalkable(5, 5));
    }

    [Fact]
    public void IsWalkable_ExplicitBlocked_ReturnsFalse()
    {
        string path = WriteTempJson(@"{
            ""format_version"": 2, ""world_version"": 1,
            ""chunks"": { ""0,0"": [
                { ""x"": 5, ""y"": 5, ""z"": 100, ""flags"": { ""walkable"": false } }
            ] }
        }");
        var world = new WorldService(new[] { path });
        world.EnsureLoaded();
        Assert.False(world.IsWalkable(5, 5));
    }

    [Fact]
    public void EnsureLoaded_CalledTwice_SameContent_StaysLoaded()
    {
        // Idempotency: file unchanged, so second call should still return true
        // and the state should be unchanged.
        string path = WriteTempJson(@"{ ""format_version"": 2, ""world_version"": 5 }");
        var world = new WorldService(new[] { path });
        Assert.True(world.EnsureLoaded());
        Assert.True(world.EnsureLoaded());
        Assert.Equal(5, world.Version);
    }

    [Fact]
    public void EnsureLoaded_FallsBackToSecondCandidate()
    {
        // First path doesn't exist, second does. EnsureLoaded should try the
        // second.
        string path = WriteTempJson(@"{ ""format_version"": 2, ""world_version"": 9 }");
        var world = new WorldService(new[] { "C:/definitely/not/a/real/path.json", path });
        Assert.True(world.EnsureLoaded());
        Assert.Equal(9, world.Version);
    }
}