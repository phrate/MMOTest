using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MMOTest.Server;

/// <summary>
/// Authoritative server. Uses ENet + SceneMultiplayer raw packets carrying JSON,
/// so no RPC signatures/node paths need to match the GDScript client.
///
/// Threading model: packets arrive on the main thread. Slow work (SQLite, password hashing) is awaited
/// on the thread pool, and Godot's synchronization context resumes each handler back on the main thread.
/// So all server state below is only touched on the main thread, and no locks are needed.
/// Do NOT use ConfigureAwait(false) in this class, and never call Godot APIs inside Task.Run.
/// </summary>
public partial class Main : Node
{
    private const int Port = 9999;
    private const int MaxClients = 64;
    private const int MinUsernameLength = 3;
    private const int MaxUsernameLength = 20;
    private const int MinPasswordLength = 8;
    private const int MaxPasswordLength = 128;

    private static readonly Regex UsernameRegex = new($"^[A-Za-z0-9_]{{{MinUsernameLength},{MaxUsernameLength}}}$");

    private record PlayerSession(long PlayerId, string Username, bool IsAdmin);

    private Database _db;
    private SceneMultiplayer _mp;
    private ENetMultiplayerPeer _enet;

    // Peers currently connected. Checked after every await, since a peer can vanish mid-request.
    private readonly HashSet<long> _connectedPeers = new();

    // Peers with a login/register in flight. Only one at a time per peer, which also stops
    // one connection from queuing up piles of expensive hashes or double-logging-in.
    private readonly HashSet<long> _authInProgress = new();

    // peer id -> session, for peers that have authenticated.
    // Server-side permission checks should read IsAdmin from here, never from anything the client sends.
    private readonly Dictionary<long, PlayerSession> _sessions = new();

    // Verified against when a username doesn't exist, so response time doesn't reveal valid usernames.
    private string _dummyHash;

    public override void _Ready()
    {
        string dbPath = ProjectSettings.GlobalizePath("user://mmotest.db");
        _db = new Database(dbPath);
        _db.Initialize();
        _dummyHash = PasswordHasher.Hash("dummy-password-for-timing");
        GD.Print($"[Server] SQLite {_db.GetSqliteVersion()} ready at {dbPath}");

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

        GD.Print($"[Server] Listening on port {Port}");
    }

    private void OnPeerConnected(long peerId)
    {
        _connectedPeers.Add(peerId);
        GD.Print($"[Server] Peer connected: {peerId}");
    }

    private void OnPeerDisconnected(long peerId)
    {
        _connectedPeers.Remove(peerId);
        _authInProgress.Remove(peerId);
        _sessions.Remove(peerId);
        GD.Print($"[Server] Peer disconnected: {peerId}");
    }

    // Fire-and-forget is safe here: HandlePacketAsync catches everything itself.
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
                case "hello":
                    await HandleHelloAsync(peerId);
                    break;

                case "register":
                    await HandleRegisterAsync(peerId, GetString(root, "username"), GetString(root, "password"));
                    break;

                case "login":
                    await HandleLoginAsync(peerId, GetString(root, "username"), GetString(root, "password"));
                    break;

                case "dev_auto_login": // TEMP-DEV: remove with Main.DevAutoLogin.cs
                    await HandleDevAutoLoginAsync(peerId);
                    break;

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

    private async Task HandleHelloAsync(long peerId)
    {
        string version = await _db.GetSqliteVersionAsync();
        long total = await _db.CountPlayersAsync();
        Send(peerId, new { type = "hello_ok", sqlite_version = version, total_players = total });
    }

    private async Task HandleRegisterAsync(long peerId, string username, string password)
    {
        if (!UsernameRegex.IsMatch(username))
        {
            SendError(peerId, "register_result", ErrorKeys.RegisterUsernameInvalid, new { min = MinUsernameLength, max = MaxUsernameLength });
            return;
        }
        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
        {
            SendError(peerId, "register_result", ErrorKeys.RegisterPasswordLength, new { min = MinPasswordLength, max = MaxPasswordLength });
            return;
        }
        if (!_authInProgress.Add(peerId))
        {
            SendError(peerId, "register_result", ErrorKeys.RequestInProgress);
            return;
        }

        try
        {
            string hash = await PasswordHasher.HashAsync(password);
            var (created, playerId) = await _db.TryCreatePlayerAsync(username, hash);

            if (!created)
            {
                SendError(peerId, "register_result", ErrorKeys.RegisterUsernameTaken);
                return;
            }

            GD.Print($"[Server] Registered: {username} (id {playerId})");
            Send(peerId, new { type = "register_result", ok = true, username });
        }
        finally
        {
            _authInProgress.Remove(peerId);
        }
    }

    private async Task HandleLoginAsync(long peerId, string username, string password)
    {
        if (_sessions.ContainsKey(peerId))
        {
            SendError(peerId, "login_result", ErrorKeys.LoginAlreadyLoggedIn);
            return;
        }
        if (!_authInProgress.Add(peerId))
        {
            SendError(peerId, "login_result", ErrorKeys.RequestInProgress);
            return;
        }

        try
        {
            PlayerRecord player = await _db.GetPlayerByUsernameAsync(username);

            // Always run one hash verification, found or not.
            bool passwordOk = await PasswordHasher.VerifyAsync(password, player?.PasswordHash ?? _dummyHash);

            if (player == null || !passwordOk)
            {
                GD.Print($"[Server] Failed login for '{username}' from peer {peerId}");
                SendError(peerId, "login_result", ErrorKeys.LoginInvalidCredentials);
                return;
            }

            await CompleteLoginAsync(peerId, player);
        }
        finally
        {
            _authInProgress.Remove(peerId);
        }
    }

    /// <summary>Shared tail of every successful login: records it, creates the session, replies.</summary>
    private async Task CompleteLoginAsync(long peerId, PlayerRecord player)
    {
        long loginCount = await _db.RecordLoginAsync(player.Id);
        long totalPlayers = await _db.CountPlayersAsync();

        // The peer may have disconnected while we were waiting. Don't create a session for a ghost.
        if (!_connectedPeers.Contains(peerId)) return;

        _sessions[peerId] = new PlayerSession(player.Id, player.Username, player.IsAdmin);
        GD.Print($"[Server] Login: {player.Username} (id {player.Id}, count {loginCount}{(player.IsAdmin ? ", admin" : "")})");
        Send(peerId, new
        {
            type = "login_result",
            ok = true,
            player_id = player.Id,
            username = player.Username,
            is_admin = player.IsAdmin,
            login_count = loginCount,
            total_players = totalPlayers
        });
    }

    /// <summary>Sends a failed result. The client resolves errorKey to text; params fill its {placeholders}.</summary>
    private void SendError(long peerId, string type, string errorKey, object errorParams = null)
    {
        Send(peerId, new { type, ok = false, error_key = errorKey, error_params = errorParams ?? new { } });
    }

    private void Send(long peerId, object message)
    {
        if (!_connectedPeers.Contains(peerId)) return; // peer left while we were busy

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        _mp.SendBytes(bytes, (int)peerId, MultiplayerPeer.TransferModeEnum.Reliable, 0);
    }
}
