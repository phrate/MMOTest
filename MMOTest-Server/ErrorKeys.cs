namespace MMOTest.Server;

public static class ErrorKeys
{
    public const string RequestInProgress = "account.common.error.request_in_progress";

    public const string RegisterUsernameInvalid = "account.register.error.username_invalid";
    public const string RegisterPasswordLength = "account.register.error.password_length";
    public const string RegisterUsernameTaken = "account.register.error.username_taken";
    public const string LoginStatsInitFailed = "account.login.error.stats_init_failed";

    public const string LoginInvalidCredentials = "account.login.error.invalid_credentials";
    public const string LoginAlreadyLoggedIn = "account.login.error.already_logged_in";

    public const string WorldNotLoggedIn = "world.error.not_logged_in";
    public const string WorldUnavailable = "world.error.unavailable";

    public const string GfsScriptNotFound = "gfs.action.error.script_not_found";
    public const string GfsRuntimeError = "gfs.action.error.runtime_error";

    // ---------- Trade (recoverable errors) ----------
    public const string TradeSelf           = "trade.error.self";
    public const string TradeUnavailable    = "trade.error.unavailable";
    public const string TradeBusy           = "trade.error.busy";
    public const string TradeAlreadyActive  = "trade.error.already_active";
    public const string TradeInCombat       = "trade.error.in_combat";
    public const string TradeCooldown       = "trade.error.cooldown";
    public const string TradeInviteExpired  = "trade.error.invite_expired";
    public const string TradeOfferInvalid   = "trade.error.offer_invalid";
    public const string TradeOfferLocked    = "trade.error.offer_locked";
    public const string TradeCreditInvalid  = "trade.error.credit_invalid";
    public const string TradeInventoryFull  = "trade.error.inventory_full";
    public const string TradeCommitFailed   = "trade.error.commit_failed";
    public const string TradeCreditOverflow = "trade.error.credit_overflow";
    public const string TradeNothingToOffer = "trade.error.nothing_to_offer";

    // ---------- Trade (terminal cancels) ----------
    public const string TradeCancelByPlayer     = "trade.cancel.by_player";
    public const string TradeCancelDeclined     = "trade.cancel.declined";
    public const string TradeCancelMoved        = "trade.cancel.moved";
    public const string TradeCancelCombat       = "trade.cancel.combat";
    public const string TradeCancelDeath        = "trade.cancel.death";
    public const string TradeCancelDisconnected = "trade.cancel.disconnected";
    public const string TradeTimeout            = "trade.cancel.timeout";
    public const string TradeCancelDialogue = "trade.cancel.dialogue";

    public const string DialogueCancelInterrupted = "dialogue.cancel.interrupted";
    
    // ---------- Bank ----------
    public const string BankUnavailable  = "bank.error.unavailable";
    public const string BankFull         = "bank.error.full";
    public const string BankPartialWithdraw = "bank.error.partial_withdraw";
    public const string BankNonBankable  = "bank.error.non_bankable";
    public const string BankNothingToDeposit = "bank.error.nothing_to_deposit";
    public const string BankNothingToWithdraw = "bank.error.nothing_to_withdraw";
    public const string BankSlotInvalid  = "bank.error.slot_invalid";
    public const string BankCancelInterrupted = "bank.cancel.interrupted";
}