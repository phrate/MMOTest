using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns the login/register session flow: hello, register, login, session
/// creation, world entry, spawn placement.
///
/// Dev auto-login (passwordless phrate path) stays on Main.DevAutoLogin.cs
/// until Phase 3b-F2.
/// </summary>
internal sealed class AuthSystem
{
    private const int MinUsernameLength = 3;
    private const int MaxUsernameLength = 20;
    private const int MinPasswordLength = 8;
    private const int MaxPasswordLength = 128;

    private static readonly Regex UsernameRegex =
        new($"^[A-Za-z0-9_]{{{MinUsernameLength},{MaxUsernameLength}}}$");

    private readonly ServerState _state;
    private readonly OutboundChannel _outbound;
    private readonly WorldService _world;
    private readonly Database _db;
    private readonly NpcSystem _npc;
    private readonly BankSystem _bank;
    private readonly ItemSystem _inv;
    private readonly HashSet<long> _connectedPeers;
    private readonly Dictionary<string, Dictionary<string, (int Min, int Max)>> _lootTables;
    private readonly Dictionary<string, (int Min, int Max)> _creditsLoot;

    private readonly HashSet<long> _authInProgress = new();
    private readonly string _dummyHash;

    private readonly Func<long, bool> _isLoopback;
    private readonly Random _combatRng;

    private const bool DevAutoLoginEnabled = true;
    private const string DevUsername = "phrate";
    private const string DevPassword = "12345678";
    private const string DevDisabledKey = "account.dev.error.disabled";
    private const string DevAccountWrongPasswordKey = "account.dev.error.account_wrong_password";
    private const string DevFailedKey = "account.dev.error.failed";
    private readonly HashSet<string> _devUsernameClaims = new();

    public AuthSystem(ServerState state, OutboundChannel outbound, WorldService world,
                    Database db, NpcSystem npc, BankSystem bank, ItemSystem inv,
                    HashSet<long> connectedPeers,
                    Dictionary<string, Dictionary<string, (int Min, int Max)>> lootTables,
                    Dictionary<string, (int Min, int Max)> creditsLoot,
                    Func<long, bool> isLoopback,
                    Random combatRng)
    {
        _state = state;
        _outbound = outbound;
        _world = world;
        _db = db;
        _npc = npc;
        _bank = bank;
        _inv = inv;
        _connectedPeers = connectedPeers;
        _lootTables = lootTables;
        _creditsLoot = creditsLoot;
        _dummyHash = PasswordHasher.Hash("dummy-password-for-timing");
        _isLoopback = isLoopback;
        _combatRng = combatRng;
    }

    internal void ClearAuthInProgress(long peerId) => _authInProgress.Remove(peerId);

    // ============================================================
    // Hello
    // ============================================================

    internal async Task HandleHelloAsync(long peerId)
    {
        string version = await _db.GetSqliteVersionAsync();
        long total = await _db.Players.CountAsync();
        _outbound.Send(peerId, new { type = "hello_ok", sqlite_version = version, total_players = total });
    }

    // ============================================================
    // Register
    // ============================================================

    internal async Task HandleRegisterAsync(long peerId, string username, string password, bool isDev = false)
    {
        if (!UsernameRegex.IsMatch(username))
        {
            _outbound.Send(peerId, new
            {
                type = "register_result",
                ok = false,
                error_key = ErrorKeys.RegisterUsernameInvalid,
                error_params = new { min = MinUsernameLength, max = MaxUsernameLength },
            });
            return;
        }
        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
        {
            _outbound.Send(peerId, new
            {
                type = "register_result",
                ok = false,
                error_key = ErrorKeys.RegisterPasswordLength,
                error_params = new { min = MinPasswordLength, max = MaxPasswordLength },
            });
            return;
        }
        if (!_authInProgress.Add(peerId))
        {
            _outbound.Send(peerId, new
            {
                type = "register_result",
                ok = false,
                error_key = ErrorKeys.RequestInProgress,
                error_params = new { },
            });
            return;
        }

        try
        {
            string hash = await PasswordHasher.HashAsync(password);
            bool makeAdmin = isDev && OS.IsDebugBuild();
            var (created, playerId) = await _db.Players.TryCreateAsync(username, hash, makeAdmin);
            if (!created)
            {
                _outbound.Send(peerId, new
                {
                    type = "register_result",
                    ok = false,
                    error_key = ErrorKeys.RegisterUsernameTaken,
                    error_params = new { },
                });
                return;
            }
            GD.Print($"[Server] Registered: {username} (id {playerId}{(makeAdmin ? ", dev admin" : "")})");
            await _db.Stats.EnsureStatsAsync(playerId);
            await _db.Stats.EnsureInventoryAsync(playerId);

            if (makeAdmin)
            {
                await _inv.GiveLootTableAsync(playerId, "trade_testing", _lootTables, forceAll: true);

                if (_creditsLoot.TryGetValue("trade_testing_credits", out var creditRange))
                {
                    var rng = new Random();
                    int amount = rng.Next(creditRange.Min, creditRange.Max + 1);
                    if (amount > 0) await _db.Players.SetCreditsAsync(playerId, amount);
                    GD.Print($"[Server] Dev trade-testing kit: rolled {amount} credits for {username}");
                }
                else
                {
                    GD.PrintErr($"[Server] Dev trade-testing kit: credits_loot 'trade_testing_credits' missing");
                }

                GD.Print($"[Server] Dev trade-testing kit for {username}");
            }

            _outbound.Send(peerId, new { type = "register_result", ok = true, username });
        }
        finally
        {
            _authInProgress.Remove(peerId);
        }
    }

    // ============================================================
    // Login
    // ============================================================

    internal async Task HandleLoginAsync(long peerId, string username, string password, string reason = "")
    {
        GD.Print($"[Server] Got login from peer {peerId}: user='{username}'");
        if (_state.Sessions.ContainsKey(peerId))
        {
            _outbound.Send(peerId, new
            {
                type = "login_result",
                ok = false,
                error_key = ErrorKeys.LoginAlreadyLoggedIn,
                error_params = new { },
            });
            return;
        }
        if (!_authInProgress.Add(peerId))
        {
            _outbound.Send(peerId, new
            {
                type = "login_result",
                ok = false,
                error_key = ErrorKeys.RequestInProgress,
                error_params = new { },
            });
            return;
        }

        try
        {
            PlayerRecord player = await _db.Players.GetByUsernameAsync(username);
            bool passwordOk = await PasswordHasher.VerifyAsync(password, player?.PasswordHash ?? _dummyHash);
            if (player == null || !passwordOk)
            {
                Diag.Log("diag_login", $"Failed login for '{username}' from peer {peerId}");
                _outbound.Send(peerId, new
                {
                    type = "login_result",
                    ok = false,
                    error_key = ErrorKeys.LoginInvalidCredentials,
                    error_params = new { },
                });
                return;
            }
            if (reason == "world_update")
                Diag.Log("diag_login", $"Hotfix reconnect: {player.Username} (id {player.Id})");
            await CompleteLoginAsync(peerId, player);
        }
        finally
        {
            _authInProgress.Remove(peerId);
        }
    }

    private async Task CompleteLoginAsync(long peerId, PlayerRecord player)
    {
        long loginCount = await _db.Players.RecordLoginAsync(player.Id);
        long totalPlayers = await _db.Players.CountAsync();
        if (!_connectedPeers.Contains(peerId)) return;

        if (!await _db.Stats.EnsureStatsAsync(player.Id))
        {
            GD.PrintErr($"[Server] Failed to ensure stats for {player.Username} (id {player.Id}); login aborted");
            _outbound.Send(peerId, new
            {
                type = "login_result",
                ok = false,
                error_key = ErrorKeys.LoginStatsInitFailed,
                error_params = new { },
            });
            return;
        }

        if (!await _db.Stats.EnsureInventoryAsync(player.Id))
        {
            GD.PrintErr($"[Server] Failed to ensure inventory/bank for {player.Username} (id {player.Id}); login aborted");
            _outbound.Send(peerId, new
            {
                type = "login_result",
                ok = false,
                error_key = ErrorKeys.LoginStatsInitFailed,
                error_params = new { },
            });
            return;
        }

        var combatData = await _db.Stats.GetCombatAsync(player.Id);
        _state.PlayerCombat[player.Id] = combatData ?? new PlayerCombatData(1, 1, 1, 0, 0, 10, 10, 1);

        var inv = await _db.Inventory.GetSlotsAsync(player.Id);
        if (inv == null) inv = new long[28];
        _state.Inventories[player.Id] = inv;

        _bank.LoadForPlayer(player.Id);

        int credits = await _db.Players.GetCreditsAsync(player.Id);
        _state.Credits[player.Id] = credits;

        _state.SkillsByPlayer[player.Id] = await _db.Stats.GetSkillsAsync(player.Id);

        PlayerPosition spawnPos;
        var saved = await _db.Players.GetTileAsync(player.Id);
        if (saved.HasValue)
        {
            if (_world.IsWalkable(saved.Value.X, saved.Value.Y))
            {
                var info = _world.GetTileInfo(saved.Value.X, saved.Value.Y);
                spawnPos = new PlayerPosition(saved.Value.X, info.Z, saved.Value.Y, 0f);
                GD.Print($"[Server] Resuming {player.Username} at tile ({saved.Value.X}, {saved.Value.Y})");
            }
            else
            {
                spawnPos = _world.FindNearestValidTile(saved.Value.X, saved.Value.Y);
                GD.Print($"[Server] Saved tile ({saved.Value.X}, {saved.Value.Y}) for {player.Username} is invalid; spawned at ({spawnPos.X}, {spawnPos.Z})");
            }
        }
        else
        {
            spawnPos = _world.ComputeSpawnPosition(player.Id);
        }
        _state.Positions[player.Id] = spawnPos;

        long sid = Random.Shared.NextInt64(1, long.MaxValue);
        _ = _db.Players.SetSidAsync(player.Id, sid);
        var session = new PlayerSession(player.Id, player.Username, player.IsAdmin, sid);
        _state.Sessions[peerId] = session;
        _state.PeerByPlayerId[player.Id] = peerId;

        GD.Print($"[Server] Login: {player.Username} (id {player.Id}, count {loginCount}{(player.IsAdmin ? ", admin" : "")})");
        _outbound.Send(peerId, new
        {
            type = "login_result",
            ok = true,
            player_id = player.Id,
            username = player.Username,
            is_admin = player.IsAdmin,
            login_count = loginCount,
            total_players = totalPlayers,
            credits = credits,
            hitpoints = combatData?.CurrentHp ?? 10,
            max_hitpoints = combatData?.MaxHp ?? 10,
            spawn = new { x = spawnPos.X, y = spawnPos.Y, z = spawnPos.Z, yaw = spawnPos.Yaw }
        });

        _outbound.SendInventory(peerId);
        _outbound.SendSkills(peerId);

        EnterWorld(peerId, session, spawnPos);
    }

    // ============================================================
    // Dev auto-login (TEMP-DEV: delete this block, the constants above,
    // and the "dev_auto_login" case in Main.cs before any real deployment)
    // ============================================================

    private bool IsDevAutoLoginAllowed(long peerId)
        => DevAutoLoginEnabled && OS.IsDebugBuild() && _isLoopback(peerId);

    internal async Task HandleDevAutoLoginAsync(long peerId)
    {
        if (!IsDevAutoLoginAllowed(peerId))
        {
            GD.PrintErr($"[Server] Dev auto-login refused for peer {peerId}");
            SendError(peerId, "login_result", DevDisabledKey);
            return;
        }
        if (_state.Sessions.ContainsKey(peerId))
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
            bool alreadyInSession = false;
            foreach (var kv in _state.Sessions)
            {
                if (kv.Value.Username.Equals(DevUsername, StringComparison.OrdinalIgnoreCase))
                { alreadyInSession = true; break; }
            }

            bool alreadyClaimed = !alreadyInSession && !_devUsernameClaims.Add(DevUsername);

            if (alreadyInSession || alreadyClaimed)
            {
                GD.Print($"[Server] DEV: '{DevUsername}' already in use; generating fallback account");
                await CreateFallbackDevAccountAsync(peerId);
                return;
            }

            try
            {
                PlayerRecord player = await _db.Players.GetByUsernameAsync(DevUsername);

                if (player == null)
                {
                    GD.Print($"[Server] DEV: '{DevUsername}' missing; creating on demand");
                    string hash = await PasswordHasher.HashAsync(DevPassword);
                    await _db.Players.TryCreateDevAdminAsync(DevUsername, hash);
                    player = await _db.Players.GetByUsernameAsync(DevUsername);
                    if (player != null)
                    {
                        await _db.Stats.EnsureStatsAsync(player.Id);
                        await _db.Stats.EnsureInventoryAsync(player.Id);
                    }
                }
                else if (!await PasswordHasher.VerifyAsync(DevPassword, player.PasswordHash))
                {
                    SendError(peerId, "login_result", DevAccountWrongPasswordKey,
                              new { username = DevUsername });
                    return;
                }

                if (player == null || !player.IsAdmin)
                {
                    SendError(peerId, "login_result", DevFailedKey);
                    return;
                }

                GD.Print($"[Server] DEV: auto-login as '{player.Username}'");
                await CompleteLoginAsync(peerId, player);
            }
            finally
            {
                _devUsernameClaims.Remove(DevUsername);
            }
        }
        finally
        {
            _authInProgress.Remove(peerId);
        }
    }

    private async Task CreateFallbackDevAccountAsync(long peerId)
    {
        string username = "dev_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string password = Guid.NewGuid().ToString("N");

        string hash = await PasswordHasher.HashAsync(password);
        var (created, playerId) = await _db.Players.TryCreateAsync(username, hash, isAdmin: true);
        if (!created)
        {
            SendError(peerId, "login_result", DevFailedKey);
            return;
        }

        await _db.Stats.EnsureStatsAsync(playerId);
        await _db.Stats.EnsureInventoryAsync(playerId);
        await _inv.GiveLootTableAsync(playerId, "trade_testing", _lootTables, forceAll: true);

        if (_creditsLoot.TryGetValue("trade_testing_credits", out var range))
        {
            int amount = _combatRng.Next(range.Min, range.Max + 1);
            if (amount > 0) await _db.Players.SetCreditsAsync(playerId, amount);
        }

        var player = await _db.Players.GetByUsernameAsync(username);
        if (player == null)
        {
            SendError(peerId, "login_result", DevFailedKey);
            return;
        }

        GD.Print($"[Server] DEV: fallback account '{username}' (id {playerId}) created and logged in");
        await CompleteLoginAsync(peerId, player);
    }

    private void SendError(long peerId, string type, string errorKey, object? errorParams = null)
        => _outbound.Send(peerId, new
        {
            type,
            ok = false,
            error_key = errorKey,
            error_params = errorParams ?? new { },
        });

    private void EnterWorld(long peerId, PlayerSession session, PlayerPosition spawnPos)
    {
        var others = new List<object>();
        foreach (var (otherPeerId, other) in _state.Sessions)
        {
            if (otherPeerId == peerId) continue;
            if (!_state.Positions.TryGetValue(other.PlayerId, out var pos)) continue;
            others.Add(new
            {
                player_id = other.PlayerId,
                username = other.Username,
                is_admin = other.IsAdmin,
                x = pos.X, y = pos.Y, z = pos.Z, yaw = pos.Yaw,
                sid = other.Sid,
            });
        }

        _outbound.Send(peerId, new { type = "world_state", players = others });

        _outbound.Broadcast(new
        {
            type = "player_joined",
            player_id = session.PlayerId,
            username = session.Username,
            is_admin = session.IsAdmin,
            x = spawnPos.X, y = spawnPos.Y, z = spawnPos.Z, yaw = spawnPos.Yaw,
            sid = session.Sid,
        }, exceptPeerId: peerId);

        int tx = (int)MathF.Round(spawnPos.X);
        int ty = (int)MathF.Round(spawnPos.Z);
        _npc.RefreshNpcVisibility(peerId, tx, ty);
    }

    internal static void EnsureDevAccountSync(Database db, string username, string password)
    {
        var existing = db.Players.GetByUsernameAsync(username).Result;
        if (existing != null)
        {
            if (!existing.IsAdmin)
            {
                db.Players.SetAdminSync(existing.Id, true);
                GD.Print($"[Server] Elevated dev account '{username}' to admin");
            }
            return;
        }

        string hash = PasswordHasher.Hash(password);
        var (created, playerId) = db.Players.TryCreateAsync(username, hash, isAdmin: true).Result;
        if (!created)
        {
            GD.PrintErr($"[Server] Failed to create dev account '{username}'");
            return;
        }
        db.Stats.EnsureStatsAsync(playerId).Wait();
        db.Stats.EnsureInventoryAsync(playerId).Wait();
        GD.Print($"[Server] Created dev account '{username}' (id {playerId})");
    }

}