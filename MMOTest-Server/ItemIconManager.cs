using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace MMOTest.Server;

/// <summary>
/// Owns item icon files: scans item_render/icons/*.png across search dirs,
/// hashes each, exposes manifest + bytes for shipping to the client.
/// Icons are flat PNG files (no per-slug subdir like NPC render bundles).
/// </summary>
public sealed class ItemIconManager
{
    private readonly string[] _searchDirs;
    private readonly Dictionary<string, (string Hash, byte[] Bytes)> _icons = new();

    public ItemIconManager(string[] searchDirs) { _searchDirs = searchDirs; }

    public void LoadAll()
    {
        _icons.Clear();
        foreach (var dir in _searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.png"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (_icons.ContainsKey(name)) continue;
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    _icons[name] = (hash, bytes);
                }
                catch (Exception e)
                {
                    GD.PrintErr($"[ItemIconManager] {file}: {e.Message}");
                }
            }
        }
        GD.Print($"[Server] Loaded {_icons.Count} item icon(s)");
    }

    public Dictionary<string, string> GetManifest()
    {
        var d = new Dictionary<string, string>();
        foreach (var kv in _icons) d[kv.Key] = kv.Value.Hash;
        return d;
    }

    public byte[]? GetIconBytes(string name) =>
        _icons.TryGetValue(name, out var v) ? v.Bytes : null;

    public string? GetIconHash(string name) =>
        _icons.TryGetValue(name, out var v) ? v.Hash : null;
}