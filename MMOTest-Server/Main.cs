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
    private record PlayerPosition(float X, float Y, float Z, float Yaw);
    private record TileInfo(int Z, bool Walkable);

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
    private DateTime _worldMtime;
    private long _worldSize;

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
    }

    // ---------- Tick ----------

    private void RunTick()
    {
        _currentTick++;
        if (_intents.Count == 0) return;

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
            updates.Add(new
            {
                player_id = session.PlayerId,
                x = next.X, y = next.Y, z = next.Z, yaw = next.Yaw
            });

            if (finished) donePeers.Add(peerId);
        }

        foreach (long p in donePeers) _intents.Remove(p);
        if (updates.Count == 0) return;

        Broadcast(new
        {
            type = "tick_positions",
            tick = _currentTick,
            updates
        }, channel: 1, mode: MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
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
}