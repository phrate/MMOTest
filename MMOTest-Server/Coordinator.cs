namespace MMOTest.Server;

/// <summary>
/// Owns the cross-cutting cancel policy. Every place that says "X happened,
/// therefore cancel Y and Z for player P" goes through here. Systems expose
/// their cancel methods; Coordinator fans out the calls per event.
///
/// Wiring is two-phase: construct the three modals first (each takes a
/// Coordinator reference in its constructor and only invokes methods at
/// packet-handling time), then call Wire() to give Coordinator its refs.
/// By the time any packet or tick fires, wiring is complete.
/// </summary>
internal sealed class Coordinator
{
    private ITradeCanceller _trade = null!;
    private IDialogueCanceller _dialogue = null!;
    private IBankCanceller _bank = null!;

    public void Wire(ITradeCanceller trade, IDialogueCanceller dialogue, IBankCanceller bank)
    {
        _trade = trade;
        _dialogue = dialogue;
        _bank = bank;
    }

    public void OnCombatStarted(long playerId)
    {
        _trade.CancelTradeForPlayer(playerId, ErrorKeys.TradeCancelCombat);
        _trade.CancelTradeQueuesForPlayer(playerId, ErrorKeys.TradeCancelCombat);
        _dialogue.CancelDialogueForPlayer(playerId, ErrorKeys.DialogueCancelInterrupted);
        _dialogue.CancelPendingTalkFor(playerId);
        _bank.CancelBankForPlayer(playerId, ErrorKeys.BankCancelInterrupted);
        _bank.CancelPendingBankFor(playerId);
    }

    public void OnPlayerWalked(long playerId)
    {
        _trade.CancelTradeForPlayer(playerId, ErrorKeys.TradeCancelMoved);
        _trade.CancelTradeQueuesForPlayer(playerId, ErrorKeys.TradeCancelMoved);
        _dialogue.CancelDialogueForPlayer(playerId, "");
        _dialogue.CancelPendingTalkFor(playerId);
        _bank.CancelBankForPlayer(playerId, "");
        _bank.CancelPendingBankFor(playerId);
    }

    public void OnPlayerDied(long playerId)
    {
        _trade.CancelTradeForPlayer(playerId, ErrorKeys.TradeCancelDeath);
        _trade.CancelTradeQueuesForPlayer(playerId, ErrorKeys.TradeCancelDeath);
        _dialogue.CancelDialogueForPlayer(playerId, ErrorKeys.DialogueCancelInterrupted);
        _dialogue.CancelPendingTalkFor(playerId);
        _bank.CancelBankForPlayer(playerId, ErrorKeys.BankCancelInterrupted);
        _bank.CancelPendingBankFor(playerId);
    }

    public void OnPlayerDisconnected(long playerId)
    {
        _trade.CancelTradeForPlayer(playerId, ErrorKeys.TradeCancelDisconnected);
        _trade.CancelTradeQueuesForPlayer(playerId, ErrorKeys.TradeCancelDisconnected);
    }

    public void OnDialogueOpened(long playerId)
    {
        _trade.CancelTradeForPlayer(playerId, ErrorKeys.TradeCancelDialogue);
        _trade.CancelTradeQueuesForPlayer(playerId, ErrorKeys.TradeCancelDialogue);
        _bank.CancelBankForPlayer(playerId, "");
        _bank.CancelPendingBankFor(playerId);
    }

    public void OnBankOpened(long playerId)
    {
        _trade.CancelTradeForPlayer(playerId, ErrorKeys.TradeCancelDialogue);
        _trade.CancelTradeQueuesForPlayer(playerId, ErrorKeys.TradeCancelDialogue);
        _dialogue.CancelDialogueForPlayer(playerId, "");
    }

    public void OnTradeInitiated(long playerId)
    {
        _dialogue.CancelDialogueForPlayer(playerId, "");
        _dialogue.CancelPendingTalkFor(playerId);
        _bank.CancelBankForPlayer(playerId, "");
        _bank.CancelPendingBankFor(playerId);
    }

    public void OnTradeAccepted(long playerAId, long playerBId)
    {
        _dialogue.CancelDialogueForPlayer(playerAId, "");
        _dialogue.CancelDialogueForPlayer(playerBId, "");
        _dialogue.CancelPendingTalkFor(playerAId);
        _dialogue.CancelPendingTalkFor(playerBId);
        _bank.CancelBankForPlayer(playerAId, "");
        _bank.CancelBankForPlayer(playerBId, "");
        _bank.CancelPendingBankFor(playerAId);
        _bank.CancelPendingBankFor(playerBId);
    }

    public void OnFollowStarted(long playerId)
    {
        _dialogue.CancelDialogueForPlayer(playerId, ErrorKeys.DialogueCancelInterrupted);
        _dialogue.CancelPendingTalkFor(playerId);
        _bank.CancelBankForPlayer(playerId, ErrorKeys.BankCancelInterrupted);
        _bank.CancelPendingBankFor(playerId);
    }
}