using Godot;
using System;
using System.Threading.Tasks;

namespace MMOTest.Server;

// ============================================================================
// TEMP-DEV: development-only auto-login. DELETE THIS FILE before any real deployment,
// plus the "dev_auto_login" case in Main.cs, GetFirstAdminAsync in Database.cs,
// and the TEMP-DEV lines in the client's main.gd and network.gd.
//
// This is a passwordless login path, so it is locked down three ways:
//   1. DevAutoLoginEnabled must be true,
//   2. the server must be a debug build (not an exported release), and
//   3. the request must come from this machine (loopback address).
//
// Behavior:
//   - If phrate is logged in by another peer, spawn a fallback dev account
//     so multi-client testing works (trade/follow between two clients).
//   - If phrate exists but is not logged in, log in as phrate.
//   - If phrate is missing entirely, create it (admin, defaults) and log in.
// ============================================================================
public partial class Main
{
    private readonly System.Collections.Generic.HashSet<string> _devUsernameClaims = new();
    private const bool DevAutoLoginEnabled = true;
    private const string DevUsername = "phrate";
    private const string DevPassword = "12345678";

    private const string DevDisabledKey = "account.dev.error.disabled";
    private const string DevAccountWrongPasswordKey = "account.dev.error.account_wrong_password";
    private const string DevFailedKey = "account.dev.error.failed";

    private bool DevAutoLoginAllowed(long peerId)
    {
        if (!DevAutoLoginEnabled || !OS.IsDebugBuild()) return false;

        string address = _enet.GetPeer((int)peerId)?.GetRemoteAddress();
        return address == "127.0.0.1" || address == "::1";
    }

    private async Task HandleDevAutoLoginAsync(long peerId)
{
    if (!DevAutoLoginAllowed(peerId))
    {
        GD.PrintErr($"[Server] Dev auto-login refused for peer {peerId}");
        SendError(peerId, "login_result", DevDisabledKey);
        return;
    }
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
        // Atomic claim check. Must run BEFORE any await, otherwise two
        // simultaneous connections both see _sessions empty during the
        // GetPlayerByUsernameAsync await window and both proceed to log
        // in as phrate.
        bool alreadyInSession = false;
        foreach (var kv in _sessions)
        {
            if (kv.Value.Username.Equals(DevUsername, StringComparison.OrdinalIgnoreCase))
            {
                alreadyInSession = true;
                break;
            }
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
                SendError(peerId, "login_result", DevAccountWrongPasswordKey, new { username = DevUsername });
                return;
            }

            if (player == null)
            {
                SendError(peerId, "login_result", DevFailedKey);
                return;
            }
            if (!player.IsAdmin)
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
        await GiveLootTableAsync(playerId, "trade_testing", forceAll: true);

        if (_creditsLootBySlug.TryGetValue("trade_testing_credits", out var range))
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
}