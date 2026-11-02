using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMOTest.Server.Gfs;

public sealed class GfsManager
{
    private readonly string[] _scriptDirs;
    private readonly string[] _tooltipsFileCandidates;

    private readonly Dictionary<string, SuiteNode> _asts = new();
    private readonly Dictionary<string, DateTime> _mtimes = new();
    private Dictionary<string, string> _entries = new();
    private int _version;
    private double _pollAccumulator;
    private const double PollIntervalSeconds = 1.0;

    public event Action<int, Dictionary<string, string>, Dictionary<string, Dictionary<string, object>>>? BundleUpdated;
    public event Action<Dictionary<string, Dictionary<string, object>>, int>? ScriptsChanged;

    public int Version => _version;

    public GfsManager(string[] scriptDirs, string[] tooltipsFileCandidates)
    {
        _scriptDirs = scriptDirs;
        _tooltipsFileCandidates = tooltipsFileCandidates;
    }

    public void InitialLoad()
    {
        LoadAllScripts(true);
        LoadEntries();
        _version = 1;
        GD.Print($"[Server] GFS: {_asts.Count} scripts parsed, version {_version}");
    }

    /// <summary>
    /// Call from _Process. Cheap: mtime check per file, no work if nothing changed.
    /// On any change, re-parses changed files and fires ScriptsChanged with only
    /// the newly-serialized ASTs, plus an incremented version.
    /// </summary>
    public void Poll()
    {
        _pollAccumulator += 1.0 / 60.0;
        if (_pollAccumulator < PollIntervalSeconds) return;
        _pollAccumulator = 0;

        var changed = new Dictionary<string, Dictionary<string, object>>();

        foreach (string dir in _scriptDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.GetFiles(dir, "*.gfs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                var mtime = File.GetLastWriteTimeUtc(file);
                if (_mtimes.TryGetValue(rel, out var known) && known == mtime) continue;
                try
                {
                    var text = File.ReadAllText(file);
                    var ast = new Parser(new Lexer(text).Tokenize()).Parse();
                    _asts[rel] = ast;
                    _mtimes[rel] = mtime;
                    changed[rel] = Serializer.Serialize(ast);
                    GD.Print($"[Server] GFS reload: {rel}");
                }
                catch (Exception e)
                {
                    GD.PrintErr($"[Server] GFS parse error in {rel}: {e.Message}");
                }
            }
        }

        if (changed.Count > 0)
        {
            _version++;
            ScriptsChanged?.Invoke(changed, _version);
        }
    }

    /// <summary>Full bundle: entries + all ASTs, serialized for JSON transport.</summary>
    public Dictionary<string, object> BuildBundle()
    {
        LoadEntries();
        var asts = new Dictionary<string, Dictionary<string, object>>();
        foreach (var kv in _asts) asts[kv.Key] = Serializer.Serialize(kv.Value);
        return new Dictionary<string, object>
        {
            ["version"] = _version,
            ["format_version"] = 1,
            ["entries"] = _entries,
            ["scripts"] = asts
        };
    }

    public SuiteNode? GetAst(string scriptPath) =>
        _asts.TryGetValue(scriptPath, out var ast) ? ast : null;

    public Dictionary<string, string> GetEntries() => _entries;

    private void LoadAllScripts(bool log)
    {
        _asts.Clear();
        _mtimes.Clear();
        foreach (string dir in _scriptDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.GetFiles(dir, "*.gfs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                try
                {
                    var text = File.ReadAllText(file);
                    var ast = new Parser(new Lexer(text).Tokenize()).Parse();
                    _asts[rel] = ast;
                    _mtimes[rel] = File.GetLastWriteTimeUtc(file);
                    if (log) GD.Print($"[Server] GFS parsed: {rel} [{ast.Section}]");
                }
                catch (Exception e)
                {
                    GD.PrintErr($"[Server] GFS parse error in {rel}: {e.Message}");
                }
            }
        }
    }

    private void LoadEntries()
    {
        foreach (string path in _tooltipsFileCandidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("entries", out var entriesEl) ||
                    entriesEl.ValueKind != JsonValueKind.Object) return;
                var dict = new Dictionary<string, string>();
                foreach (var prop in entriesEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        dict[prop.Name] = prop.Value.GetString();
                }
                _entries = dict;
                return;
            }
            catch (Exception e)
            {
                GD.PrintErr($"[Server] tooltips.json parse error: {e.Message}");
            }
        }
    }
    public (string Path, SuiteNode? Ast) ResolveByStem(string stem)
    {
        if (!stem.EndsWith(".gfs")) stem += ".gfs";
        foreach (var kv in _asts)
        {
            int slash = kv.Key.LastIndexOf('/');
            var name = slash >= 0 ? kv.Key.Substring(slash + 1) : kv.Key;
            if (name == stem) return (kv.Key, kv.Value);
        }
        return ("", null);
    }
}