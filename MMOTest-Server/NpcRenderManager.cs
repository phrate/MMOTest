using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace MMOTest.Server;

public sealed record RenderFile(string RelativePath, byte[] Bytes);
public sealed record RenderBundle(string Slug, string Hash, List<RenderFile> Files);

public sealed class NpcRenderManager
{
    private readonly string[] _searchDirs;
    private readonly Dictionary<string, RenderBundle> _bundles = new();
    private readonly Dictionary<string, DateTime> _mtimes = new();
    private double _pollAccumulator;
    private const double PollIntervalSeconds = 2.0;

    public NpcRenderManager(string[] searchDirs) { _searchDirs = searchDirs; }

    public void LoadAll()
    {
        _bundles.Clear();
        _mtimes.Clear();
        var slugs = _collectAllSlugs();
        foreach (var slug in slugs)
        {
            var bundle = _buildBundleForSlug(slug);
            if (bundle == null) continue;
            _bundles[slug] = bundle;
            _mtimes[slug] = GetSlugMtime(slug);
        }

        int npcCount = 0;
        foreach (var slug in _bundles.Keys)
            if (slug != "player") npcCount++;
        bool hasPlayer = _bundles.ContainsKey("player");

        string msg = $"[Server] Loaded {npcCount} NPC render bundle(s)";
        if (hasPlayer) msg += " + 1 shared player render";
        GD.Print(msg);
    }

    public bool Poll()
    {
        _pollAccumulator += 1.0 / 60.0;
        if (_pollAccumulator < PollIntervalSeconds) return false;
        _pollAccumulator = 0;

        var changed = false;
        var currentSlugs = _collectAllSlugs();

        foreach (var slug in currentSlugs)
        {
            var mtime = GetSlugMtime(slug);
            if (_mtimes.TryGetValue(slug, out var known) && known == mtime) continue;
            var bundle = _buildBundleForSlug(slug);
            if (bundle == null) continue;
            if (_bundles.TryGetValue(slug, out var old) && old.Hash == bundle.Hash)
            {
                _mtimes[slug] = mtime;
                continue;
            }
            _bundles[slug] = bundle;
            _mtimes[slug] = mtime;
            changed = true;
            GD.Print($"[Server] Render bundle updated: {slug}");
        }

        var toRemove = new List<string>();
        foreach (var slug in _bundles.Keys)
            if (!currentSlugs.Contains(slug)) toRemove.Add(slug);
        foreach (var slug in toRemove)
        {
            _bundles.Remove(slug);
            _mtimes.Remove(slug);
            changed = true;
            GD.Print($"[Server] Render bundle removed: {slug}");
        }

        return changed;
    }

    private HashSet<string> _collectAllSlugs()
    {
        var slugs = new HashSet<string>();
        foreach (var dir in _searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var sub in Directory.GetDirectories(dir))
                slugs.Add(Path.GetFileName(sub));
            foreach (var file in Directory.GetFiles(dir, "*.tscn"))
                slugs.Add(Path.GetFileNameWithoutExtension(file));
        }
        return slugs;
    }

    private DateTime GetSlugMtime(string slug)
    {
        DateTime latest = DateTime.MinValue;
        foreach (var dir in _searchDirs)
        {
            string sub = Path.Combine(dir, slug);
            if (Directory.Exists(sub))
            {
                var m = _folderMtime(sub);
                if (m > latest) latest = m;
            }
            string single = Path.Combine(dir, slug + ".tscn");
            if (File.Exists(single))
            {
                var m = File.GetLastWriteTimeUtc(single);
                if (m > latest) latest = m;
            }
        }
        return latest;
    }

    private static DateTime _folderMtime(string dir)
    {
        DateTime latest = DateTime.MinValue;
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var m = File.GetLastWriteTimeUtc(f);
                if (m > latest) latest = m;
            }
        }
        catch { }
        return latest;
    }

    /// <summary>
    /// Merges files from every search dir for a slug. Later search dirs
    /// override earlier ones for files with the same relative path
    /// (so user:// overrides res://).
    /// </summary>
    private RenderBundle? _buildBundleForSlug(string slug)
    {
        var files = new List<RenderFile>();
        var indexByRel = new Dictionary<string, int>();

        foreach (var dir in _searchDirs)
        {
            string sub = Path.Combine(dir, slug);
            if (Directory.Exists(sub))
            {
                foreach (var f in Directory.GetFiles(sub, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(sub, f).Replace('\\', '/');
                    var file = new RenderFile(rel, File.ReadAllBytes(f));
                    if (indexByRel.TryGetValue(rel, out int idx))
                        files[idx] = file;
                    else
                    {
                        indexByRel[rel] = files.Count;
                        files.Add(file);
                    }
                }
            }
            string single = Path.Combine(dir, slug + ".tscn");
            if (File.Exists(single))
            {
                string rel = $"{slug}.tscn";
                var file = new RenderFile(rel, File.ReadAllBytes(single));
                if (indexByRel.TryGetValue(rel, out int idx))
                    files[idx] = file;
                else
                {
                    indexByRel[rel] = files.Count;
                    files.Add(file);
                }
            }
        }

        if (files.Count == 0) return null;
        files.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));
        return new RenderBundle(slug, ComputeHash(files), files);
    }

    private static string ComputeHash(List<RenderFile> files)
    {
        using var sha = SHA256.Create();
        using var stream = new MemoryStream();
        foreach (var f in files)
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(f.RelativePath + "\0"));
            stream.Write(f.Bytes);
            stream.WriteByte(0);
        }
        stream.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public Dictionary<string, string> GetManifest()
    {
        var d = new Dictionary<string, string>();
        foreach (var kv in _bundles) d[kv.Key] = kv.Value.Hash;
        return d;
    }

    public RenderBundle? GetBundle(string slug) =>
        _bundles.TryGetValue(slug, out var b) ? b : null;
}