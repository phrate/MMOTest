namespace MMOTest.Server;

/// <summary>
/// The server never sends player-facing prose. Failures are sent as these keys (plus optional params),
/// and the client turns them into text using its Data/Texts/*.json files.
/// Every key here MUST exist in the client's JSON; keys are built from folder + file name + nested keys
/// (see the client's Data/Texts/README.md).
/// </summary>
public static class ErrorKeys
{
    public const string RequestInProgress = "account.common.error.request_in_progress";

    public const string RegisterUsernameInvalid = "account.register.error.username_invalid";   // params: min, max
    public const string RegisterPasswordLength = "account.register.error.password_length";     // params: min, max
    public const string RegisterUsernameTaken = "account.register.error.username_taken";

    public const string LoginInvalidCredentials = "account.login.error.invalid_credentials";
    public const string LoginAlreadyLoggedIn = "account.login.error.already_logged_in";
}
