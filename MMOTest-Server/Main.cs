using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MMOTest.Server;

public partial class Main : Node
{
    private const int Port = 9999;
    private const int MaxClients = 64;
    private const int MinUsernameLength = 3;
    private const int MaxUsernameLength = 20;
    private const int MinPasswordLength = 8;
    private const int MaxPasswordLength = 128;

    private const double TickIntervalSeconds = 0.3;
    private const float WalkSpeed = 2.0f;
    private const double WorldCheckIntervalSeconds = 1.0;

    private const int MaxPathLength = 2000;

    private static readonly string[] WorldFileCandidates =
    {
        ProjectSettings.GlobalizePath("user://world.json"),
        ProjectSettings.GlobalizePath("res://data/world.json"),
    };

    private static readonly Regex UsernameRegex = new($"^[A-Za-z0-9_]{{{MinUsernameLength},{MaxUsernameLength}}}$");

    private record PlayerSession(long PlayerId, string Username, bool IsAdmin);
    internal record PlayerPosition(float X, float Y, float Z, float Yaw);
    internal record TileInfo(int Z, bool Walkable);

    private Database _db;
    private SceneMultiplayer _mp;
    private ENetMultiplayerPeer _enet;

    private readonly HashSet<long> _connectedPeers = new();
    private readonly HashSet<long> _authInProgress = new();
    private readonly Dictionary<long, PlayerSession> _sessions = new();
    private readonly Dictionary<long, long> _peerByPlayerId = new();
    private readonly Dictionary<long, PlayerPosition> _positions = new();

    // Per-peer remaining path. Front of the list is the next waypoint. Empty list
    // (transient) means "arrived, about to be cleaned up in the same tick".
    private readonly Dictionary<long, List<Vector2I>> _intents = new();

    private readonly Dictionary<(int X, int Y), TileInfo> _tiles = new();

    private double _tickAccumulator = 0.0;
    private long _currentTick = 0;

    private double _worldCheckAccumulator = 0.0;
    private int _lastBroadcastVersion = -1;

    private string _dummyHash;

    private JsonDocument _worldDoc;
    private string _worldPath;
    private int _worldVersion;
    private int _worldFormatVersion;
    private int _chunkSize = 8;
    private DateTime _worldMtime;
    private long _worldSize;

    private Gfs.GfsManager _gfs;
    private Gfs.FunctionRegistry _gfsFns;
    private Gfs.PerTickManager _perTick;

    public override void _Ready()
    {
        string dbPath = ProjectSettings.GlobalizePath("user://mmotest.db");
        _db = new Database(dbPath);
        _db.Initialize();
        _dummyHash = PasswordHasher.Hash("dummy-password-for-timing");
        GD.Print($"[Server] SQLite {_db.GetSqliteVersion()} ready at {dbPath}");

        if (EnsureWorldLoaded())
        {
            _lastBroadcastVersion = _worldVersion;
            GD.Print($"[Server] World ready: v{_worldVersion} format {_worldFormatVersion}, {_tiles.Count} tiles ({_worldPath})");
        }
        else
        {
            GD.PrintErr("[Server] No world file loaded; world_info/world_fetch will report unavailable");
        }
        

        if (File.Exists(TooltipsFileCandidates[0]) || File.Exists(TooltipsFileCandidates[1]))
            GD.Print("[Server] Tooltips: available");
        else
            GD.PrintErr("[Server] Tooltips: not found (right-click menus will be empty)");


        _gfs = new Gfs.GfsManager(GfsScriptDirs, GfsTooltipsFileCandidates);
        _gfs.InitialLoad();
        _gfsFns = new Gfs.FunctionRegistry();
        Gfs.Utils.TilesUtils.Register(_gfsFns, this);
        Gfs.Utils.PlayerUtils.Register(_gfsFns, this);
        _perTick = new Gfs.PerTickManager(_gfsFns);
        _gfs.ScriptsChanged += OnGfsScriptsChanged;


        var peer = new ENetMultiplayerPeer();
        Error err = peer.CreateServer(Port, MaxClients);
        if (err != Error.Ok)
        {
            GD.PrintErr($"[Server] Failed to listen on port {Port}: {err}");
            GetTree().Quit(1);
            return;
        }

        _enet = peer;
        _mp = new SceneMultiplayer();
        _mp.MultiplayerPeer = peer;
        GetTree().SetMultiplayer(_mp);

        _mp.PeerConnected += OnPeerConnected;
        _mp.PeerDisconnected += OnPeerDisconnected;
        _mp.PeerPacket += OnPacket;

        GD.Print($"[Server] Listening on port {Port} (tick = {TickIntervalSeconds * 1000}ms)");
    }

    public override void _ExitTree()
    {
        _worldDoc?.Dispose();
        _worldDoc = null;
    }

    public override void _Process(double delta)
    {
        _tickAccumulator += delta;
        while (_tickAccumulator >= TickIntervalSeconds)
        {
            _tickAccumulator -= TickIntervalSeconds;
            RunTick();
        }

        _worldCheckAccumulator += delta;
        if (_worldCheckAccumulator >= WorldCheckIntervalSeconds)
        {
            _worldCheckAccumulator = 0;
            CheckWorldForUpdates();
        }

        _gfs.Poll();
    }

    // ---------- Tick ----------

    private void RunTick()
    {
        _currentTick++;

        // 1. Movement processing (only if there are intents to process)
        if (_intents.Count > 0)
        {
            var updates = new List<object>();
            var donePeers = new List<long>();

            foreach (long peerId in _intents.Keys.ToList())
            {
                if (!_sessions.TryGetValue(peerId, out PlayerSession session)) { donePeers.Add(peerId); continue; }
                if (!_positions.TryGetValue(session.PlayerId, out PlayerPosition current)) { donePeers.Add(peerId); continue; }

                var path = _intents[peerId];
                if (path.Count == 0) { donePeers.Add(peerId); continue; }

                var (next, finished) = StepTowardPath(current, path);
                _positions[session.PlayerId] = next;
                updates.Add(new { player_id = session.PlayerId, x = next.X, y = next.Y, z = next.Z, yaw = next.Yaw });

                if (finished) donePeers.Add(peerId);
            }

            foreach (long p in donePeers) _intents.Remove(p);
            if (updates.Count > 0)
            {
                Broadcast(new { type = "tick_positions", tick = _currentTick, updates },
                    channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
            }
        }

        // 2. Per-tick scripts (ALWAYS run, even when no movement is in flight).
       var tickEffects = _perTick.TickAll();
        if (tickEffects.Count > 0)
        {
            foreach (var (pid, effects) in tickEffects)
            {
                if (!_positions.TryGetValue(pid, out var ppos)) continue;
                int pcx = (int)MathF.Floor((float)MathF.Round(ppos.X) / _chunkSize);
                int pcy = (int)MathF.Floor((float)MathF.Round(ppos.Z) / _chunkSize);

                string username = "";
                long selfPeer = -1;
                foreach (var kv in _sessions)
                    if (kv.Value.PlayerId == pid) { username = kv.Value.Username; selfPeer = kv.Key; break; }

                RouteChatEffects(pid, username, pcx, pcy, effects, selfPeer);
            }
        }
    }

    /// <summary>
    /// Advances up to WalkSpeed*Tick seconds of distance along the path, consuming waypoints
    /// as they're reached and carrying leftover distance into the next segment. The result
    /// is a constant-velocity trajectory with no stops at intermediate tiles.
    /// </summary>
    private (PlayerPosition, bool) StepTowardPath(PlayerPosition current, List<Vector2I> path)
    {
        float remaining = WalkSpeed * (float)TickIntervalSeconds;
        float x = current.X;
        float z = current.Z;
        float yaw = current.Yaw;

        while (remaining > 0.0001f && path.Count > 0)
        {
            var target = path[0];
            float dx = target.X - x;
            float dz = target.Y - z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);

            if (dist < 0.0001f)
            {
                path.RemoveAt(0);
                continue;
            }

            yaw = MathF.Atan2(-dx, -dz);

            if (dist <= remaining)
            {
                // Reach the waypoint and continue with whatever distance is left.
                x = target.X;
                z = target.Y;
                remaining -= dist;
                path.RemoveAt(0);
            }
            else
            {
                // Partial step; we're mid-segment at the end of this tick.
                float ratio = remaining / dist;
                x += dx * ratio;
                z += dz * ratio;
                remaining = 0;
            }
        }

        int tileX = (int)MathF.Round(x);
        int tileY = (int)MathF.Round(z);
        var tinfo = GetTileInfo(tileX, tileY);
        return (new PlayerPosition(x, tinfo.Z, z, yaw), path.Count == 0);
    }

    private TileInfo GetTileInfo(int x, int y)
    {
        if (_tiles.TryGetValue((x, y), out TileInfo t)) return t;
        return new TileInfo(100, true);
    }

    // ---------- World file updates ----------

    private void CheckWorldForUpdates()
    {
        if (!EnsureWorldLoaded()) return;
        if (_worldVersion == _lastBroadcastVersion) return;

        _lastBroadcastVersion = _worldVersion;
        GD.Print($"[Server] World hotfix: v{_worldVersion}, notifying {_sessions.Count} client(s)");

        Broadcast(new
        {
            type = "world_updated",
            world_version = _worldVersion,
            format_version = _worldFormatVersion
        });
    }

    private static readonly string[] TooltipsFileCandidates =
    {
        ProjectSettings.GlobalizePath("user://tooltips.json"),
        ProjectSettings.GlobalizePath("res://data/tooltips.json"),
    };

    private static readonly string[] TooltipsScriptDirs =
    {
        ProjectSettings.GlobalizePath("user://data/scripts/gfs/tooltips"),
        ProjectSettings.GlobalizePath("res://data/scripts/gfs/tooltips"),
    };

    private static readonly string[] GfsScriptDirs =
    {
        ProjectSettings.GlobalizePath("user://data/scripts/gfs"),
        ProjectSettings.GlobalizePath("res://data/scripts/gfs"),
    };

    private static readonly string[] GfsTooltipsFileCandidates =
    {
        ProjectSettings.GlobalizePath("user://tooltips.json"),
        ProjectSettings.GlobalizePath("res://data/tooltips.json"),
    };

    // ---------- Connection events ----------

    private void OnPeerConnected(long peerId)
    {
        _connectedPeers.Add(peerId);
        GD.Print($"[Server] Peer connected: {peerId}");
    }

    private void OnPeerDisconnected(long peerId)
    {
        _connectedPeers.Remove(peerId);
        _authInProgress.Remove(peerId);
        _intents.Remove(peerId);

        if (_sessions.Remove(peerId, out PlayerSession session))
        {
            _peerByPlayerId.Remove(session.PlayerId);

            if (_positions.TryGetValue(session.PlayerId, out PlayerPosition pos))
            {
                int tx = (int)MathF.Round(pos.X);
                int ty = (int)MathF.Round(pos.Z);
                int tz = (int)MathF.Round(pos.Y);
                _ = _db.SetPlayerTileAsync(session.PlayerId, tx, ty, tz);
            }
            _positions.Remove(session.PlayerId);

            Broadcast(
                new { type = "player_left", player_id = session.PlayerId, username = session.Username },
                exceptPeerId: peerId);
        }

        GD.Print($"[Server] Peer disconnected: {peerId}");

        _perTick?.EndAllFor(session.PlayerId);
    }

    private void OnPacket(long peerId, byte[] packet) => _ = HandlePacketAsync(peerId, packet);

    private async Task HandlePacketAsync(long peerId, byte[] packet)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(packet));
            JsonElement root = doc.RootElement;
            string type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "hello":       await HandleHelloAsync(peerId); break;
                case "register":    await HandleRegisterAsync(peerId, GetString(root, "username"), GetString(root, "password")); break;
                case "login":       await HandleLoginAsync(peerId, GetString(root, "username"), GetString(root, "password"), GetString(root, "reason")); break;
                case "world_info":  HandleWorldInfo(peerId); break;
                case "world_fetch": HandleWorldFetch(peerId); break;
                case "dev_auto_login": await HandleDevAutoLoginAsync(peerId); break;
                case "player_walk": HandlePlayerWalkBuffered(peerId, root); break;
                case "ping":         HandlePing(peerId, root); break;
                case "dev_shutdown": HandleDevShutdown(peerId); break;
                case "chat_send": HandleChatSend(peerId, root); break;
                case "tooltips_info": HandleTooltipsInfo(peerId); break;
                case "gfs_info":    HandleGfsInfo(peerId); break;
                case "run_action":  HandleRunAction(peerId, root); break;
                default:
                    GD.PrintErr($"[Server] Unknown message type '{type}' from {peerId}");
                    break;
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Server] Error handling packet from {peerId}: {e.Message}");
        }
    }

    private bool _shuttingDown = false;

    private void HandleTooltipsInfo(long peerId)
    {
        if (!_sessions.ContainsKey(peerId))
        {
            SendError(peerId, "tooltips_info_result", ErrorKeys.WorldNotLoggedIn);
            return;
        }

        string tooltipsText = null;
        foreach (string path in TooltipsFileCandidates)
        {
            if (File.Exists(path)) { tooltipsText = File.ReadAllText(path); break; }
        }
        if (tooltipsText == null)
        {
            SendError(peerId, "tooltips_info_result", ErrorKeys.WorldUnavailable);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(tooltipsText);
            var root = doc.RootElement;
            if (!root.TryGetProperty("format_version", out JsonElement fmt) ||
                !root.TryGetProperty("version", out JsonElement ver) ||
                !root.TryGetProperty("entries", out JsonElement entries) ||
                entries.ValueKind != JsonValueKind.Object)
            {
                SendError(peerId, "tooltips_info_result", ErrorKeys.WorldUnavailable);
                return;
            }

            // Entries from tooltips.json: entity_type -> script filename.
            var entriesDict = new Dictionary<string, string>();
            foreach (JsonProperty entry in entries.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String) continue;
                string scriptName = entry.Value.GetString();
                if (string.IsNullOrEmpty(scriptName)) continue;
                entriesDict[entry.Name] = scriptName;
            }

            // Enumerate every .gfs file in both script directories. user:// takes
            // precedence over res:// on a name collision, matching Godot's loading order.
            var scriptsDict = new Dictionary<string, string>();
            foreach (string dir in TooltipsScriptDirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.gfs"))
                {
                    string name = Path.GetFileName(file);
                    if (scriptsDict.ContainsKey(name)) continue;
                    try { scriptsDict[name] = File.ReadAllText(file); }
                    catch (Exception e) { GD.PrintErr($"[Server] Cannot read {file}: {e.Message}"); }
                }
            }

            if (OS.IsDebugBuild())
            {
                GD.Print($"[Server] Tooltips: entries={entriesDict.Count}, scripts={scriptsDict.Count}");
                foreach (var kv in scriptsDict)
                    GD.Print($"[Server]   script: {kv.Key} ({kv.Value.Length} bytes)");
            }

            Send(peerId, new
            {
                type = "tooltips_info_result",
                ok = true,
                version = ver.GetInt32(),
                format_version = fmt.GetInt32(),
                entries = entriesDict,
                scripts = scriptsDict
            });
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Server] tooltips_info error: {e.Message}");
            SendError(peerId, "tooltips_info_result", ErrorKeys.WorldUnavailable);
        }
    }

    private void HandleGfsInfo(long peerId)
    {
        if (!_sessions.ContainsKey(peerId))
        {
            SendError(peerId, "gfs_info_result", ErrorKeys.WorldNotLoggedIn);
            return;
        }
        var bundle = _gfs.BuildBundle();
        bundle["type"] = "gfs_info_result";
        bundle["ok"] = true;
        Send(peerId, bundle);
    }

    private void OnGfsScriptsChanged(Dictionary<string, Dictionary<string, object>> changed, int version)
    {
        if (_sessions.Count == 0) return;
        Broadcast(new
        {
            type = "gfs_updated",
            version,
            scripts = changed
        });
        GD.Print($"[Server] GFS: broadcast {changed.Count} changed script(s), version {version}");
    }

    private void HandleRunAction(long peerId, JsonElement root)
    {
        if (!_sessions.TryGetValue(peerId, out PlayerSession session)) return;
        if (!root.TryGetProperty("script", out var scriptEl)) return;
        string scriptPath = scriptEl.GetString() ?? "";
        var ast = _gfs.GetAst(scriptPath);
        if (ast == null)
        {
            SendError(peerId, "run_action_result", "gfs.script_not_found");
            return;
        }

        var args = new Dictionary<string, object>();
        if (root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in argsEl.EnumerateObject())
            {
                args[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out long l) ? l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
            }
        }
        int targetId = 0;
        if (root.TryGetProperty("target_id", out var tidEl) && tidEl.TryGetInt32(out int tid)) targetId = tid;

        var system = new Dictionary<string, object>
        {
            ["SELF_ID"] = session.PlayerId,
            ["PLAYER_ID"] = targetId,
            ["SELF_USERNAME"] = session.Username,
        };

        var ctx = new Gfs.CallContext
        {
            System = system,
            Args = args,
            Locals = new Dictionary<string, object>(),
            SelfPlayerId = session.PlayerId,
            ScriptPath = scriptPath,
        };
        ctx.RunScript = (s, a) => RunScriptFromGfs(session.PlayerId, s, a, ctx);

        List<Dictionary<string, object>> effects;
        try { effects = Gfs.Interpreter.Run(ast, ctx, _gfsFns); }
        catch (Exception e)
        {
            GD.PrintErr($"[Server] run_action error: {e.Message}");
            SendError(peerId, "run_action_result", "gfs.runtime_error");
            return;
        }

        if (_positions.TryGetValue(session.PlayerId, out var pos))
        {
            int cx = (int)MathF.Floor((float)MathF.Round(pos.X) / _chunkSize);
            int cy = (int)MathF.Floor((float)MathF.Round(pos.Z) / _chunkSize);
            RouteChatEffects(session.PlayerId, session.Username, cx, cy, effects, peerId);
        }

        if (ast.Tags.Contains(Gfs.GfsTags.OncePerTick) && !ctx.EndScriptRequested)
            _perTick.Start(session.PlayerId, scriptPath, ast, ctx);

        Send(peerId, new { type = "run_action_result", ok = true, effects });
    }

    private void HandlePing(long peerId, JsonElement root)
    {
        if (!_sessions.ContainsKey(peerId)) return;
        if (!root.TryGetProperty("t", out JsonElement tEl) || !tEl.TryGetInt64(out long clientT)) return;
        Send(peerId, new { type = "pong", t = clientT });
    }

    /// <summary>
    /// Debug-only: an admin client can trigger a graceful shutdown over the network.
    /// Handy when the server runs headless and the editor stop button isn't available.
    /// </summary>
    private void HandleDevShutdown(long peerId)
    {
        if (!OS.IsDebugBuild()) return;
        if (!_sessions.TryGetValue(peerId, out PlayerSession s) || !s.IsAdmin) return;
        GD.Print($"[Server] dev_shutdown requested by {s.Username}");
        GracefulShutdown();
    }

    public override void _Notification(int what)
    {
        if (what == (int)NotificationWMCloseRequest)
        {
            GracefulShutdown();
        }
    }

    /// <summary>
    /// Broadcasts a shutdown message so clients can auto-reconnect, lets the main
    /// loop run long enough for ENet to flush, then closes the peer and quits.
    ///
    /// Async on purpose. OS.DelayMsec blocks the main thread — and ENet's poll
    /// runs on the main thread — so the shutdown packet would sit in the outgoing
    /// queue and be discarded by Close(). Awaiting a SceneTreeTimer yields to the
    /// main loop, letting the poll flush the packet before the peer closes.
    /// </summary>
    private async void GracefulShutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        GD.Print($"[Server] Graceful shutdown: notifying {_sessions.Count} client(s)");

        Broadcast(new
        {
            type = "shutdown",
            reason = "restart",
            reconnect_after_ms = 500
        });

        // ~18 frames at 60 fps. ENet flushes on the first poll after the broadcast,
        // so this is generous headroom for a slow frame.
        await ToSignal(GetTree().CreateTimer(0.3), "timeout");

        if (_mp != null && _mp.MultiplayerPeer != null)
            _mp.MultiplayerPeer.Close();

        await ToSignal(GetTree().CreateTimer(0.1), "timeout");

        GetTree().Quit();
    }

    private static string GetString(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out JsonElement value) ? value.GetString() ?? "" : "";
    }

    // ---------- Gameplay ----------

    /// <summary>
    /// Buffers a walk path. Validates: session, path length, all tiles walkable,
    /// consecutive tiles adjacent (8-connected, no diagonal corner-cutting), and
    /// first step adjacent to the player's current tile (guards against
    /// teleport-by-crafted-path).
    ///
    /// Diagonal corner rule: a step from (x, y) to (x±1, y±1) is only valid if
    /// both (x±1, y) and (x, y±1) are walkable. Matches the client's A* setting
    /// (AStarGrid2D.DIAGONAL_MODE_ONLY_IF_NO_OBSTACLES); if these ever diverge,
    /// a client's valid-looking path will be silently rejected server-side.
    /// </summary>
    private void HandlePlayerWalkBuffered(long peerId, JsonElement root)
    {
        if (!_sessions.TryGetValue(peerId, out PlayerSession session)) return;
        // Manual click-to-walk cancels script-driven movement AND intent.
        _perTick?.EndByStem(session.PlayerId, "follow_player");
        _perTick?.EndByStem(session.PlayerId, "trade_player");
        if (!_positions.TryGetValue(session.PlayerId, out PlayerPosition current)) return;

        if (!root.TryGetProperty("path", out JsonElement pathEl) || pathEl.ValueKind != JsonValueKind.Array)
            return;

        var path = new List<Vector2I>();
        Vector2I? prev = null;
        foreach (JsonElement t in pathEl.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() != 2) return;
            if (!t[0].TryGetInt32(out int x) || !t[1].TryGetInt32(out int y)) return;
            if (Math.Abs(x) > 1_000_000 || Math.Abs(y) > 1_000_000) return;
            if (!IsWalkableTile(x, y)) return;

            if (prev.HasValue)
            {
                int ddx = Math.Abs(x - prev.Value.X);
                int ddy = Math.Abs(y - prev.Value.Y);
                if (ddx > 1 || ddy > 1 || (ddx == 0 && ddy == 0)) return;
                if (ddx == 1 && ddy == 1)
                {
                    if (!IsWalkableTile(prev.Value.X, y)) return;
                    if (!IsWalkableTile(x, prev.Value.Y)) return;
                }
            }
            path.Add(new Vector2I(x, y));
            prev = new Vector2I(x, y);
        }

        if (path.Count == 0 || path.Count > MaxPathLength) return;

        // First step must be adjacent to the server's current tile.
        int curX = (int)MathF.Round(current.X);
        int curY = (int)MathF.Round(current.Z);
        int ddx0 = Math.Abs(path[0].X - curX);
        int ddy0 = Math.Abs(path[0].Y - curY);
        if (ddx0 > 1 || ddy0 > 1) return;
        if (ddx0 == 1 && ddy0 == 1)
        {
            if (!IsWalkableTile(curX, path[0].Y)) return;
            if (!IsWalkableTile(path[0].X, curY)) return;
        }

        _intents[peerId] = path;
    }

    /// <summary>
    /// True only if (x, y) is an explicit, walkable tile. Unlike GetTileInfo, this
    /// does NOT treat unlisted tiles as walkable: void tiles block movement.
    /// </summary>
    private bool IsWalkableTile(int x, int y)
    {
        return _tiles.TryGetValue((x, y), out TileInfo info) && info.Walkable;
    }

    // ---------- Auth ----------

    private async Task HandleHelloAsync(long peerId)
    {
        string version = await _db.GetSqliteVersionAsync();
        long total = await _db.CountPlayersAsync();
        Send(peerId, new { type = "hello_ok", sqlite_version = version, total_players = total });
    }

    private async Task HandleRegisterAsync(long peerId, string username, string password)
    {
        if (!UsernameRegex.IsMatch(username))
        { SendError(peerId, "register_result", ErrorKeys.RegisterUsernameInvalid, new { min = MinUsernameLength, max = MaxUsernameLength }); return; }
        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
        { SendError(peerId, "register_result", ErrorKeys.RegisterPasswordLength, new { min = MinPasswordLength, max = MaxPasswordLength }); return; }
        if (!_authInProgress.Add(peerId))
        { SendError(peerId, "register_result", ErrorKeys.RequestInProgress); return; }

        try
        {
            string hash = await PasswordHasher.HashAsync(password);
            var (created, playerId) = await _db.TryCreatePlayerAsync(username, hash);
            if (!created) { SendError(peerId, "register_result", ErrorKeys.RegisterUsernameTaken); return; }
            GD.Print($"[Server] Registered: {username} (id {playerId})");
            Send(peerId, new { type = "register_result", ok = true, username });
        }
        finally { _authInProgress.Remove(peerId); }
    }

    private async Task HandleLoginAsync(long peerId, string username, string password, string reason = "")
    {
        if (_sessions.ContainsKey(peerId))
        { SendError(peerId, "login_result", ErrorKeys.LoginAlreadyLoggedIn); return; }
        if (!_authInProgress.Add(peerId))
        { SendError(peerId, "login_result", ErrorKeys.RequestInProgress); return; }

        try
        {
            PlayerRecord player = await _db.GetPlayerByUsernameAsync(username);
            bool passwordOk = await PasswordHasher.VerifyAsync(password, player?.PasswordHash ?? _dummyHash);
            if (player == null || !passwordOk)
            {
                GD.Print($"[Server] Failed login for '{username}' from peer {peerId}");
                SendError(peerId, "login_result", ErrorKeys.LoginInvalidCredentials);
                return;
            }
            if (reason == "world_update")
                GD.Print($"[Server] Hotfix reconnect: {player.Username} (id {player.Id})");
            await CompleteLoginAsync(peerId, player);
        }
        finally { _authInProgress.Remove(peerId); }
    }

    private async Task CompleteLoginAsync(long peerId, PlayerRecord player)
    {
        long loginCount = await _db.RecordLoginAsync(player.Id);
        long totalPlayers = await _db.CountPlayersAsync();
        if (!_connectedPeers.Contains(peerId)) return;

        var session = new PlayerSession(player.Id, player.Username, player.IsAdmin);
        _sessions[peerId] = session;
        _peerByPlayerId[player.Id] = peerId;

        PlayerPosition spawnPos;
        var saved = await _db.GetPlayerTileAsync(player.Id);
        if (saved.HasValue && _tiles.TryGetValue((saved.Value.X, saved.Value.Y), out TileInfo savedTile) && savedTile.Walkable)
        {
            spawnPos = new PlayerPosition(saved.Value.X, savedTile.Z, saved.Value.Y, 0f);
            GD.Print($"[Server] Resuming {player.Username} at tile ({saved.Value.X}, {saved.Value.Y})");
        }
        else
        {
            spawnPos = ComputeSpawnPosition(player.Id);
        }
        _positions[player.Id] = spawnPos;

        GD.Print($"[Server] Login: {player.Username} (id {player.Id}, count {loginCount}{(player.IsAdmin ? ", admin" : "")})");
        Send(peerId, new
        {
            type = "login_result",
            ok = true,
            player_id = player.Id,
            username = player.Username,
            is_admin = player.IsAdmin,
            login_count = loginCount,
            total_players = totalPlayers,
            spawn = new { x = spawnPos.X, y = spawnPos.Y, z = spawnPos.Z, yaw = spawnPos.Yaw }
        });

        EnterWorld(peerId, session, spawnPos);
    }

    private PlayerPosition ComputeSpawnPosition(long playerId)
    {
        int pid = (int)Math.Max(playerId, 1);
        int sx = (pid * 3) % 16 - 8;
        int sz = (pid * 7) % 16 - 8;
        var info = GetTileInfo(sx, sz);
        return new PlayerPosition(sx, info.Z, sz, 0f);
    }

    private void EnterWorld(long peerId, PlayerSession session, PlayerPosition spawnPos)
    {
        var others = new List<object>();
        foreach (var (otherPeerId, other) in _sessions)
        {
            if (otherPeerId == peerId) continue;
            var pos = _positions.TryGetValue(other.PlayerId, out var p) ? p : new PlayerPosition(0f, 0f, 0f, 0f);
            others.Add(new
            {
                player_id = other.PlayerId,
                username = other.Username,
                is_admin = other.IsAdmin,
                x = pos.X, y = pos.Y, z = pos.Z, yaw = pos.Yaw
            });
        }
        Send(peerId, new { type = "world_state", players = others });
        Broadcast(new
        {
            type = "player_joined",
            player_id = session.PlayerId,
            username = session.Username,
            is_admin = session.IsAdmin,
            x = spawnPos.X, y = spawnPos.Y, z = spawnPos.Z, yaw = spawnPos.Yaw
        }, exceptPeerId: peerId);
    }

    // ---------- World file ----------

    private bool EnsureWorldLoaded()
    {
        foreach (string path in WorldFileCandidates)
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
                    GD.PrintErr($"[Server] {path}: missing format_version or world_version");
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

                _tiles.Clear();
                foreach (var kv in newTiles) _tiles[kv.Key] = kv.Value;
                return true;
            }
            catch (JsonException e) { GD.PrintErr($"[Server] {path}: parse error: {e.Message}"); }
            catch (Exception e) { GD.PrintErr($"[Server] {path}: {e.Message}"); }
        }
        return false;
    }

    private void HandleWorldInfo(long peerId)
    {
        if (!_sessions.ContainsKey(peerId)) { SendError(peerId, "world_info_result", ErrorKeys.WorldNotLoggedIn); return; }
        if (!EnsureWorldLoaded())         { SendError(peerId, "world_info_result", ErrorKeys.WorldUnavailable); return; }
        Send(peerId, new { type = "world_info_result", ok = true, world_version = _worldVersion, format_version = _worldFormatVersion });
    }

    private void HandleWorldFetch(long peerId)
    {
        if (!_sessions.ContainsKey(peerId)) { SendError(peerId, "world_data", ErrorKeys.WorldNotLoggedIn); return; }
        if (!EnsureWorldLoaded())         { SendError(peerId, "world_data", ErrorKeys.WorldUnavailable); return; }
        Send(peerId, new { type = "world_data", ok = true, world_version = _worldVersion, format_version = _worldFormatVersion, data = _worldDoc.RootElement });
    }

    // ---------- Outbound ----------

    private void SendError(long peerId, string type, string errorKey, object errorParams = null)
        => Send(peerId, new { type, ok = false, error_key = errorKey, error_params = errorParams ?? new { } });

    private void Send(long peerId, object message, int channel = 0,
        MultiplayerPeer.TransferModeEnum mode = MultiplayerPeer.TransferModeEnum.Reliable)
    {
        if (!_connectedPeers.Contains(peerId)) return;
        SendBytes(peerId, JsonSerializer.SerializeToUtf8Bytes(message), channel, mode);
    }

    private void RouteChatEffects(long pid, string username, int cx, int cy,
        List<Dictionary<string, object>> effects, long selfPeer)
    {
        foreach (var effect in effects)
        {
            string kind = effect.TryGetValue("kind", out var k) ? k as string ?? "" : "";
            string text = effect.TryGetValue("text", out var t) ? t as string ?? "" : "";
            switch (kind)
            {
                case "chat_local":
                    if (selfPeer >= 0) Send(selfPeer, new { type = "private_message", text });
                    break;
                case "chat_public":
                    Broadcast(new { type = "chat_message", username, message = text, from_chunk = new[] { cx, cy } });
                    break;
                case "chat_overhead":
                    Broadcast(new { type = "overhead_message", username, text, from_chunk = new[] { cx, cy } });
                    break;
                case "chat_private":
                    long targetPid = effect.TryGetValue("target_player_id", out var tpid) ? Convert.ToInt64(tpid) : 0;
                    if (targetPid > 0)
                    {
                        long targetPeer = -1;
                        foreach (var kv in _sessions) if (kv.Value.PlayerId == targetPid) { targetPeer = kv.Key; break; }
                        if (targetPeer >= 0) Send(targetPeer, new { type = "private_message", text });
                    }
                    break;
            }
        }
    }

    private void Broadcast(object message, long exceptPeerId = 0, int channel = 0,
        MultiplayerPeer.TransferModeEnum mode = MultiplayerPeer.TransferModeEnum.Reliable)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        foreach (long peerId in _sessions.Keys)
        {
            if (peerId == exceptPeerId) continue;
            SendBytes(peerId, bytes, channel, mode);
        }
    }

    private void SendBytes(long peerId, byte[] bytes, int channel, MultiplayerPeer.TransferModeEnum mode)
    {
        if (!_connectedPeers.Contains(peerId)) return;
        _mp.SendBytes(bytes, (int)peerId, mode, channel);
    }
    private void HandleChatSend(long peerId, JsonElement root)
        {
            if (!_sessions.TryGetValue(peerId, out PlayerSession session)) return;
            if (!_positions.TryGetValue(session.PlayerId, out PlayerPosition pos)) return;
            if (!root.TryGetProperty("message", out JsonElement msgEl)) return;

            string text = (msgEl.GetString() ?? "").Trim();
            if (text.Length == 0 || text.Length > 200) return;

            int tileX = (int)MathF.Round(pos.X);
            int tileY = (int)MathF.Round(pos.Z);
            int cx = (int)MathF.Floor((float)tileX / _chunkSize);
            int cy = (int)MathF.Floor((float)tileY / _chunkSize);

            Broadcast(new
            {
                type = "chat_message",
                username = session.Username,
                message = text,
                from_chunk = new[] { cx, cy }
            });

            GD.Print($"[Chat] {session.Username}: {text}");
        }
    

    internal bool TryGetPosition(long playerId, out PlayerPosition pos) =>
        _positions.TryGetValue(playerId, out pos);

    internal bool TryGetTileInfo(int x, int y, out TileInfo info) =>
        _tiles.TryGetValue((x, y), out info);

    internal int ChunkSize => _chunkSize;

    internal void SetIntent(long playerId, List<Vector2I> route)
    {
        long peerId = -1;
        foreach (var kv in _sessions) if (kv.Value.PlayerId == playerId) { peerId = kv.Key; break; }
        if (peerId >= 0) _intents[peerId] = new List<Vector2I>(route);
    }

    internal Dictionary<string, object>? GetActiveScriptArgs(long playerId, string stem) =>
        _perTick?.GetArgsForActive(playerId, stem);

    internal bool EndPerTickScript(long playerId, string stem) =>
        _perTick != null && _perTick.EndByStem(playerId, stem);

    private void RunScriptFromGfs(long playerId, string stem, Dictionary<string, object> args, Gfs.CallContext parentCtx)
    {
        var (path, ast) = _gfs.ResolveByStem(stem);
        if (ast == null) { GD.PrintErr($"[Server] RUN: script not found: {stem}"); return; }
        var newCtx = new Gfs.CallContext
        {
            System = new Dictionary<string, object>(parentCtx.System),
            Args = args,
            Locals = new Dictionary<string, object>(),
            SelfPlayerId = playerId,
            ScriptPath = path,
        };
        newCtx.RunScript = (s, a) => RunScriptFromGfs(playerId, s, a, newCtx);
        _perTick.Start(playerId, path, ast, newCtx);
    }


    internal List<Vector2I> PathfindToNearest(long selfPlayerId, int goalX, int goalY)
    {
        if (!_positions.TryGetValue(selfPlayerId, out var pos)) return new();
        int sx = (int)MathF.Round(pos.X);
        int sy = (int)MathF.Round(pos.Z);

        // Simple BFS over walkable tiles. For now this matches the client's A*
        // without the corner-cut rule (which is enforced by the client — the server
        // accepts the resulting route as-is).
        var queue = new Queue<Vector2I>();
        var cameFrom = new Dictionary<Vector2I, Vector2I?>();
        var start = new Vector2I(sx, sy);
        var goal = new Vector2I(goalX, goalY);
        if (!TryGetTileInfo(start.X, start.Y, out _)) return new();

        queue.Enqueue(start);
        cameFrom[start] = null;
        Vector2I[] dirs = { new(1,0), new(-1,0), new(0,1), new(0,-1),
                            new(1,1), new(1,-1), new(-1,1), new(-1,-1) };

        Vector2I? best = null;
        int bestDist = int.MaxValue;

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            int d = Math.Max(Math.Abs(cur.X - goal.X), Math.Abs(cur.Y - goal.Y));
            if (d < bestDist) { bestDist = d; best = cur; }
            if (cur == goal) break;

            foreach (var dir in dirs)
            {
                var next = cur + dir;
                if (cameFrom.ContainsKey(next)) continue;
                if (!TryGetTileInfo(next.X, next.Y, out var info) || !info.Walkable) continue;
                if (dir.X != 0 && dir.Y != 0)
                {
                    if (!TryGetTileInfo(cur.X + dir.X, cur.Y, out var a) || !a.Walkable) continue;
                    if (!TryGetTileInfo(cur.X, cur.Y + dir.Y, out var b) || !b.Walkable) continue;
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
    }}