using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MMOTest.Server;

public record TileInfo(int Z, bool Walkable);

/// <summary>
/// Owns the tile world: loads it from disk, exposes fast lookups, watches for
/// file changes. Extracted from Main so the parse logic can be unit-tested
/// without a Godot SceneTree.
///
/// Logging is injected via <paramref name="logError"/> so this class has zero
/// Godot dependencies. Production passes GD.PrintErr; tests default to
/// Console.Error.WriteLine (visible in test output on failure, harmless on
/// success).
/// </summary>
public sealed class WorldService : IDisposable
{
    private readonly string[] _fileCandidates;
    private readonly Action<string> _logError;
    private JsonDocument? _worldDoc;
    private string? _worldPath;
    private int _worldVersion;
    private int _worldFormatVersion;
    private int _chunkSize = 8;
    private DateTime _worldMtime;
    private long _worldSize;
    private Dictionary<(int X, int Y), TileInfo> _tiles = new();

    public int Version => _worldVersion;
    public int FormatVersion => _worldFormatVersion;
    public int ChunkSize => _chunkSize;
    public int TileCount => _tiles.Count;
    public string? Path => _worldPath;
    public bool IsLoaded => _worldDoc != null;

    public WorldService(string[] fileCandidates, Action<string>? logError = null)
    {
        _fileCandidates = fileCandidates;
        _logError = logError ?? Console.Error.WriteLine;
    }

    public bool EnsureLoaded()
    {
        foreach (string path in _fileCandidates)
        {
            if (!File.Exists(path)) continue;

            DateTime mtime = File.GetLastWriteTimeUtc(path);
            long size = new FileInfo(path).Length;
            if (_worldDoc != null && path == _worldPath && mtime == _worldMtime && size == _worldSize)
                return true;

            try
            {
                string text = File.ReadAllText(path);
                JsonDocument doc = JsonDocument.Parse(text);
                JsonElement root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("format_version", out JsonElement fmt) ||
                    !root.TryGetProperty("world_version", out JsonElement ver))
                {
                    _logError($"[Server] {path}: missing format_version or world_version");
                    doc.Dispose();
                    continue;
                }

                if (root.TryGetProperty("chunk_size", out JsonElement cs)) _chunkSize = cs.GetInt32();

                var newTiles = new Dictionary<(int, int), TileInfo>();
                if (root.TryGetProperty("chunks", out JsonElement chunks) && chunks.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty chunkProp in chunks.EnumerateObject())
                    {
                        if (chunkProp.Value.ValueKind != JsonValueKind.Array) continue;
                        foreach (JsonElement tile in chunkProp.Value.EnumerateArray())
                        {
                            if (tile.ValueKind != JsonValueKind.Object) continue;
                            int x = tile.GetProperty("x").GetInt32();
                            int y = tile.GetProperty("y").GetInt32();
                            int z = tile.GetProperty("z").GetInt32();
                            bool walkable = true;
                            if (tile.TryGetProperty("flags", out JsonElement flags) &&
                                flags.ValueKind == JsonValueKind.Object &&
                                flags.TryGetProperty("walkable", out JsonElement w))
                                walkable = w.GetBoolean();
                            newTiles[(x, y)] = new TileInfo(z, walkable);
                        }
                    }
                }

                _worldDoc?.Dispose();
                _worldDoc = doc;
                _worldPath = path;
                _worldFormatVersion = fmt.GetInt32();
                _worldVersion = ver.GetInt32();
                _worldMtime = mtime;
                _worldSize = size;
                _tiles = newTiles;
                return true;
            }
            catch (JsonException e)
            {
                _logError($"[Server] {path}: parse error: {e.Message}");
            }
            catch (Exception e)
            {
                _logError($"[Server] {path}: {e.Message}");
            }
        }
        return false;
    }

    public TileInfo GetTileInfo(int x, int y)
    {
        if (_tiles.TryGetValue((x, y), out TileInfo t)) return t;
        return new TileInfo(100, true);
    }

    public bool TryGetTile(int x, int y, out TileInfo info)
        => _tiles.TryGetValue((x, y), out info);

    public bool TileExists(int x, int y)
        => _tiles.ContainsKey((x, y));

    public bool IsWalkable(int x, int y)
        => _tiles.TryGetValue((x, y), out var info) && info.Walkable;

    public JsonElement? GetRootElement() => _worldDoc?.RootElement;

    public void Dispose()
    {
        _worldDoc?.Dispose();
        _worldDoc = null;
    }
}