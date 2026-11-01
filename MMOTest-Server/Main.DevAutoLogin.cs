using Godot;
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
//   - If any account has is_admin = 1, log the peer into the first one.
//   - Otherwise use the account 'abc' / '12345678', creating it as an admin (is_admin = 1) if needed.
//     This is the one exception to "no code sets is_admin", via Database.TryCreateDevAdminAsync.
//     An 'abc' account that already exists is never modified; fix its flag by hand in DB Browser.
// ============================================================================
public partial class Main
{
    private const bool DevAutoLoginEnabled = true;
    private const string DevUsername = "abc";
    private const string DevPassword = "12345678";

    // Keys resolved by the client from Data/Texts/account/dev_texts.json
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
            PlayerRecord player = await _db.GetFirstAdminAsync();

            if (player == null)
            {
                player = await _db.GetPlayerByUsernameAsync(DevUsername);

                if (player == null)
                {
                    GD.Print($"[Server] DEV: no admin found, creating admin '{DevUsername}'");
                    string hash = await PasswordHasher.HashAsync(DevPassword);
                    await _db.TryCreateDevAdminAsync(DevUsername, hash);
                    player = await _db.GetPlayerByUsernameAsync(DevUsername);
                }
                else if (!await PasswordHasher.VerifyAsync(DevPassword, player.PasswordHash))
                {
                    SendError(peerId, "login_result", DevAccountWrongPasswordKey, new { username = DevUsername });
                    return;
                }
            }

            if (player == null)
            {
                SendError(peerId, "login_result", DevFailedKey);
                return;
            }

            GD.Print($"[Server] DEV: auto-login as '{player.Username}'");
            await CompleteLoginAsync(peerId, player);
        }
        finally
        {
            _authInProgress.Remove(peerId);
        }
    }
}
