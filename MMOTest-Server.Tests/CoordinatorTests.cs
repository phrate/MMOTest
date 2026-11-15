using System.Collections.Generic;
using System.Linq;
using Xunit;
using MMOTest.Server;

namespace MMOTest.Server.Tests;

// ============================================================================
// Coordinator — cross-cutting cancel policy
// ============================================================================
//
// Every event that starts something (combat, walk, bank, dialogue, trade,
// follow, death, disconnect) has to tell the OTHER modal systems to close.
// This class is the single owner of "what cancels what" — the whole point
// of pulling that logic out of ten scattered call sites.
//
// The tests below lock down the cancel-policy matrix. Each test maps one
// Coordinator event to the exact set of systems it fans out to, with the
// exact error keys (which control what message the player sees).
//
// Two things to watch for when a future feature touches this class:
//
//   1. **Negative cases** — six of the nine events deliberately do NOT
//      cancel one system. Those are the interesting assertions. If a
//      future change makes them start passing, that means a cancel was
//      accidentally added (e.g. trade cancellation added to walk, which
//      would break trade-then-walk-to-each-other).
//
//   2. **Error keys** — walking and disconnect pass empty error keys
//      (silent cancel, no player-facing message); combat, death, and
//      follow pass loud ones ("You have no time for that!"). That
//      distinction is player-visible behavior, not an implementation
//      detail.
//
// Coverage:
//   OnCombatStarted          — fan out to all three
//   OnPlayerWalked           — fan out to all three, silent
//   OnPlayerDied             — fan out to all three, loud
//   OnPlayerDisconnected     — only trade (dialogue/bank clean up elsewhere)
//   OnDialogueOpened         — trade + bank (dialogue just opened)
//   OnBankOpened             — trade + dialogue (bank just opened)
//   OnTradeInitiated         — dialogue + bank (trade just initiated)
//   OnTradeAccepted          — dialogue + bank, both players (trade just accepted)
//   OnFollowStarted          — dialogue + bank (follow allows trade)
// ============================================================================

public class CoordinatorTests
{
    private const long PlayerA = 100;
    private const long PlayerB = 200;

    private readonly StubTrade _stubTrade = new();
    private readonly StubDialogue _stubDialogue = new();
    private readonly StubBank _stubBank = new();
    private readonly Coordinator _coordinator = new();

    public CoordinatorTests()
    {
        _coordinator.Wire(_stubTrade, _stubDialogue, _stubBank);
    }

    // ========================================================================
    // Event: OnCombatStarted
    // ========================================================================

    [Fact]
    public void Combat_Started_Cancels_All_Three_Modal_Sessions()
    {
        // Rule: entering combat overrides everything else. If you were
        // trading, talking, or banking when combat started (typically an
        // aggressive NPC reached you), all three terminate immediately.
        //
        // This is the loudest cancel path — the player sees "You have no
        // time for that!" because the interruption was not their choice.
        // That's the exact case most likely to break when attack styles
        // (Accurate/Aggressive/Defensive/Controlled) land, since they add
        // new triggers for combat start.

        _stubTrade.Reset();
        _stubDialogue.Reset();
        _stubBank.Reset();

        _coordinator.OnCombatStarted(PlayerA);

        // Trade: both the session and the queued intents are cancelled
        // with the combat-specific error key. Two calls because the
        // trade system processes session cancellation and intent-queue
        // flushing separately.
        Assert.Single(_stubTrade.Calls, c => c.Method == "CancelTradeForPlayer"
                                          && c.PlayerId == PlayerA
                                          && c.ErrorKey == ErrorKeys.TradeCancelCombat);
        Assert.Single(_stubTrade.Calls, c => c.Method == "CancelTradeQueuesForPlayer"
                                          && c.PlayerId == PlayerA
                                          && c.ErrorKey == ErrorKeys.TradeCancelCombat);

        // Dialogue: session closed loudly (player was interrupted) and
        // the pending "walk to talk" queue is cleared (no error key on
        // pending cancel — there's nothing open to tell the player about).
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelDialogueForPlayer"
                                            && c.PlayerId == PlayerA
                                            && c.ErrorKey == ErrorKeys.DialogueCancelInterrupted);
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelPendingTalkFor"
                                            && c.PlayerId == PlayerA);

        // Bank: same pattern — session closed loudly, pending approach cleared.
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelBankForPlayer"
                                         && c.PlayerId == PlayerA
                                         && c.ErrorKey == ErrorKeys.BankCancelInterrupted);
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelPendingBankFor"
                                         && c.PlayerId == PlayerA);
    }

    // ========================================================================
    // Event: OnPlayerWalked
    // ========================================================================

    [Fact]
    public void Walking_Cancels_All_Three_Modal_Sessions_Silently()
    {
        // Rule: walking away is the player's own choice, so it's a silent
        // cancel — no "You have no time for that!" message, because they
        // weren't interrupted, they just left.
        //
        // Walking cancels all three systems, but with the trade error key
        // specifically noting "moved" so the OTHER player (if any) sees
        // "Trade cancelled because a player moved" rather than a generic
        // cancellation. Solo cancels (dialogue, bank) get empty keys
        // because only the walking player is affected.

        _stubTrade.Reset();
        _stubDialogue.Reset();
        _stubBank.Reset();

        _coordinator.OnPlayerWalked(PlayerA);

        // Trade: cancelled with the "moved" key so a trade partner sees
        // the correct reason.
        Assert.Single(_stubTrade.Calls, c => c.Method == "CancelTradeForPlayer"
                                          && c.PlayerId == PlayerA
                                          && c.ErrorKey == ErrorKeys.TradeCancelMoved);
        Assert.Single(_stubTrade.Calls, c => c.Method == "CancelTradeQueuesForPlayer"
                                          && c.PlayerId == PlayerA
                                          && c.ErrorKey == ErrorKeys.TradeCancelMoved);

        // Dialogue and bank: cancelled silently (empty error key). This
        // is the difference from combat — the player chose to walk, so
        // there's no error message to show.
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelDialogueForPlayer"
                                            && c.PlayerId == PlayerA
                                            && c.ErrorKey == "");
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelPendingTalkFor"
                                            && c.PlayerId == PlayerA);

        Assert.Single(_stubBank.Calls, c => c.Method == "CancelBankForPlayer"
                                         && c.PlayerId == PlayerA
                                         && c.ErrorKey == "");
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelPendingBankFor"
                                         && c.PlayerId == PlayerA);
    }

    // ========================================================================
    // Event: OnPlayerDied
    // ========================================================================

    [Fact]
    public void Player_Death_Cancels_All_Three_Modal_Sessions_Loudly()
    {
        // Rule: dying ends everything. Same loud-cancel shape as combat
        // start (the player was interrupted, not choosing to leave), but
        // with trade using the "death" error key so the trade partner
        // sees "Trade cancelled because a player died" rather than the
        // generic "combat" message.

        _stubTrade.Reset();
        _stubDialogue.Reset();
        _stubBank.Reset();

        _coordinator.OnPlayerDied(PlayerA);

        // Trade: cancelled with the death-specific key.
        Assert.Single(_stubTrade.Calls, c => c.Method == "CancelTradeForPlayer"
                                          && c.PlayerId == PlayerA
                                          && c.ErrorKey == ErrorKeys.TradeCancelDeath);
        Assert.Single(_stubTrade.Calls, c => c.Method == "CancelTradeQueuesForPlayer"
                                          && c.PlayerId == PlayerA
                                          && c.ErrorKey == ErrorKeys.TradeCancelDeath);

        // Dialogue and bank: same interruption keys as combat start.
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelDialogueForPlayer"
                                            && c.PlayerId == PlayerA
                                            && c.ErrorKey == ErrorKeys.DialogueCancelInterrupted);
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelBankForPlayer"
                                         && c.PlayerId == PlayerA
                                         && c.ErrorKey == ErrorKeys.BankCancelInterrupted);
    }

    // ========================================================================
    // Event: OnPlayerDisconnected
    // ========================================================================

    [Fact]
    public void Disconnect_Only_Cancels_Trade()
    {
        // Rule: dialogue and bank have their own disconnect cleanup —
        // Main.OnPeerDisconnected calls _dialogue.ClearPendingFor and
        // _bank.ClearPlayer directly. So the ONLY system Coordinator
        // needs to fan out to on disconnect is trade, because a trade
        // session involves TWO players and the surviving partner needs
        // to be told the other player dropped.
        //
        // Negative cases below are the interesting part: if a future
        // change makes dialogue or bank get cancelled here, that's
        // a redundant call (harmless) but more importantly it signals
        // that someone misunderstood the ownership of disconnect cleanup.

        _stubTrade.Reset();
        _stubDialogue.Reset();
        _stubBank.Reset();

        _coordinator.OnPlayerDisconnected(PlayerA);

        // Trade: cancelled with the disconnect-specific key so the
        // surviving partner sees "Trade cancelled because the other
        // player disconnected".
        Assert.Single(_stubTrade.Calls, c => c.Method == "CancelTradeForPlayer"
                                          && c.PlayerId == PlayerA
                                          && c.ErrorKey == ErrorKeys.TradeCancelDisconnected);
        Assert.Single(_stubTrade.Calls, c => c.Method == "CancelTradeQueuesForPlayer"
                                          && c.PlayerId == PlayerA
                                          && c.ErrorKey == ErrorKeys.TradeCancelDisconnected);

        // Dialogue and bank: MUST NOT be touched here. They clean up
        // their own state from Main.OnPeerDisconnected. Asserting the
        // absence locks in that ownership split.
        Assert.Empty(_stubDialogue.Calls);
        Assert.Empty(_stubBank.Calls);
    }

    // ========================================================================
    // Event: OnDialogueOpened
    // ========================================================================

    [Fact]
    public void Dialogue_Open_Cancels_Trade_And_Bank_But_Not_Dialogue()
    {
        // Rule: starting a dialogue closes any open trade ("You can't
        // trade and talk") and closes the bank panel (dialogue and bank
        // both take over the bottom-center UI panel). Dialogue itself
        // is obviously not cancelled — we just opened one.
        //
        // The trade cancellation uses the dialogue error key so the
        // partner sees "Trade cancelled because a player started a
        // conversation".

        _stubTrade.Reset();
        _stubDialogue.Reset();
        _stubBank.Reset();

        _coordinator.OnDialogueOpened(PlayerA);

        // Trade: cancelled with the dialogue-specific key.
        Assert.Single(_stubTrade.Calls, c => c.Method == "CancelTradeForPlayer"
                                          && c.PlayerId == PlayerA
                                          && c.ErrorKey == ErrorKeys.TradeCancelDialogue);
        Assert.Single(_stubTrade.Calls, c => c.Method == "CancelTradeQueuesForPlayer"
                                          && c.PlayerId == PlayerA
                                          && c.ErrorKey == ErrorKeys.TradeCancelDialogue);

        // Bank: cancelled silently (empty key) — the player is choosing
        // to talk instead of bank, no error to show.
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelBankForPlayer"
                                         && c.PlayerId == PlayerA
                                         && c.ErrorKey == "");
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelPendingBankFor"
                                         && c.PlayerId == PlayerA);

        // Dialogue: MUST NOT be cancelled — we just opened one.
        Assert.Empty(_stubDialogue.Calls);
    }

    // ========================================================================
    // Event: OnBankOpened
    // ========================================================================

    [Fact]
    public void Bank_Open_Cancels_Trade_And_Dialogue_But_Not_Bank()
    {
        // Rule: opening the bank closes any open trade ("You can't trade
        // and bank") and closes any open dialogue (same UI panel).
        // Bank itself is obviously not cancelled.
        //
        // Note: the trade cancellation here uses the DIALOGUE error key
        // (TradeCancelDialogue), not a bank-specific one. That's the
        // current behavior — the player-facing message is "Trade
        // cancelled because a player started a conversation", which is
        // slightly odd for a bank open but matches the pre-refactor
        // behavior. If someone later adds ErrorKeys.TradeCancelBank,
        // this assertion will need updating. That's the point of the
        // test — make the choice visible.

        _stubTrade.Reset();
        _stubDialogue.Reset();
        _stubBank.Reset();

        _coordinator.OnBankOpened(PlayerA);

        // Trade: cancelled with the dialogue key (see comment above).
        Assert.Single(_stubTrade.Calls, c => c.Method == "CancelTradeForPlayer"
                                          && c.PlayerId == PlayerA
                                          && c.ErrorKey == ErrorKeys.TradeCancelDialogue);
        Assert.Single(_stubTrade.Calls, c => c.Method == "CancelTradeQueuesForPlayer"
                                          && c.PlayerId == PlayerA
                                          && c.ErrorKey == ErrorKeys.TradeCancelDialogue);

        // Dialogue: cancelled silently (empty key) — player chose to bank.
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelDialogueForPlayer"
                                            && c.PlayerId == PlayerA
                                            && c.ErrorKey == "");

        // Bank: MUST NOT be cancelled — we just opened one.
        Assert.Empty(_stubBank.Calls);
    }

    // ========================================================================
    // Event: OnTradeInitiated
    // ========================================================================

    [Fact]
    public void Trade_Initiated_Cancels_Dialogue_And_Bank_But_Not_Trade()
    {
        // Rule: initiating a trade closes any open dialogue (you can't
        // talk while trading) and closes the bank panel (both are
        // concurrent UI panels that don't coexist).
        //
        // Dialogue and bank both cancelled silently here — the player
        // chose to trade, no error message. That's the difference from
        // a trade being cancelled BY combat/death, where the key is loud.

        _stubTrade.Reset();
        _stubDialogue.Reset();
        _stubBank.Reset();

        _coordinator.OnTradeInitiated(PlayerA);

        // Dialogue: cancelled silently (empty key).
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelDialogueForPlayer"
                                            && c.PlayerId == PlayerA
                                            && c.ErrorKey == "");
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelPendingTalkFor"
                                            && c.PlayerId == PlayerA);

        // Bank: cancelled silently.
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelBankForPlayer"
                                         && c.PlayerId == PlayerA
                                         && c.ErrorKey == "");
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelPendingBankFor"
                                         && c.PlayerId == PlayerA);

        // Trade: MUST NOT be cancelled — we just initiated one.
        Assert.Empty(_stubTrade.Calls);
    }

    // ========================================================================
    // Event: OnTradeAccepted
    // ========================================================================

    [Fact]
    public void Trade_Accepted_Cancels_Dialogue_And_Bank_For_Both_Players()
    {
        // Rule: when a trade request is accepted, BOTH participants
        // (the requester and the recipient) have their dialogue and
        // bank closed. The recipient might have been mid-dialogue with
        // an NPC when the invite arrived; accepting ends that cleanly.
        //
        // This is the only two-player fan-out event, so the test verifies
        // that both PlayerA and PlayerB get cancelled — a bug that only
        // cancels the initiating player would still leave the recipient
        // stranded in an incompatible UI state.

        _stubTrade.Reset();
        _stubDialogue.Reset();
        _stubBank.Reset();

        _coordinator.OnTradeAccepted(PlayerA, PlayerB);

        // Both players: dialogue cancelled silently + pending talk cleared.
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelDialogueForPlayer"
                                            && c.PlayerId == PlayerA);
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelDialogueForPlayer"
                                            && c.PlayerId == PlayerB);
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelPendingTalkFor"
                                            && c.PlayerId == PlayerA);
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelPendingTalkFor"
                                            && c.PlayerId == PlayerB);

        // Both players: bank cancelled silently + pending bank cleared.
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelBankForPlayer"
                                         && c.PlayerId == PlayerA);
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelBankForPlayer"
                                         && c.PlayerId == PlayerB);
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelPendingBankFor"
                                         && c.PlayerId == PlayerA);
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelPendingBankFor"
                                         && c.PlayerId == PlayerB);

        // Trade: MUST NOT be cancelled — we just accepted one.
        Assert.Empty(_stubTrade.Calls);
    }

    // ========================================================================
    // Event: OnFollowStarted
    // ========================================================================

    [Fact]
    public void Follow_Started_Cancels_Dialogue_And_Bank_But_Not_Trade()
    {
        // Rule: following another player closes any dialogue (you can't
        // talk to an NPC and follow someone simultaneously) and closes
        // the bank panel. But following does NOT cancel trade — this is
        // the specific design choice that lets a player follow their
        // trade partner to a meeting spot. Cancelling trade on follow
        // would break that flow.
        //
        // The negative case below (trade not cancelled) is the most
        // important assertion in this file. If a future refactor "helpfully"
        // adds trade cancellation to OnFollowStarted, it will break the
        // follow-to-trade flow and this test is the only thing that
        // will catch it.

        _stubTrade.Reset();
        _stubDialogue.Reset();
        _stubBank.Reset();

        _coordinator.OnFollowStarted(PlayerA);

        // Dialogue and bank: cancelled loudly. Following is an
        // interruption of the NPC/session interaction, same shape as
        // combat start — the player chose to do something else, but
        // the NPC/session was mid-action.
        Assert.Single(_stubDialogue.Calls, c => c.Method == "CancelDialogueForPlayer"
                                            && c.PlayerId == PlayerA
                                            && c.ErrorKey == ErrorKeys.DialogueCancelInterrupted);
        Assert.Single(_stubBank.Calls, c => c.Method == "CancelBankForPlayer"
                                         && c.PlayerId == PlayerA
                                         && c.ErrorKey == ErrorKeys.BankCancelInterrupted);

        // Trade: MUST NOT be cancelled. This is the load-bearing
        // negative case — see header comment.
        Assert.Empty(_stubTrade.Calls);
    }

    // ========================================================================
    // Test doubles
    // ========================================================================

    private readonly record struct CancelCall(long PlayerId, string Method, string ErrorKey);

    private sealed class StubTrade : ITradeCanceller
    {
        public List<CancelCall> Calls { get; } = new();
        public void Reset() => Calls.Clear();

        public void CancelTradeForPlayer(long playerId, string errorKey)
            => Calls.Add(new(playerId, nameof(CancelTradeForPlayer), errorKey));

        public void CancelTradeQueuesForPlayer(long playerId, string errorKey)
            => Calls.Add(new(playerId, nameof(CancelTradeQueuesForPlayer), errorKey));
    }

    private sealed class StubDialogue : IDialogueCanceller
    {
        public List<CancelCall> Calls { get; } = new();
        public void Reset() => Calls.Clear();

        public void CancelDialogueForPlayer(long playerId, string errorKey)
            => Calls.Add(new(playerId, nameof(CancelDialogueForPlayer), errorKey));

        public void CancelPendingTalkFor(long playerId)
            => Calls.Add(new(playerId, nameof(CancelPendingTalkFor), ""));
    }

    private sealed class StubBank : IBankCanceller
    {
        public List<CancelCall> Calls { get; } = new();
        public void Reset() => Calls.Clear();

        public void CancelBankForPlayer(long playerId, string errorKey)
            => Calls.Add(new(playerId, nameof(CancelBankForPlayer), errorKey));

        public void CancelPendingBankFor(long playerId)
            => Calls.Add(new(playerId, nameof(CancelPendingBankFor), ""));
    }
}