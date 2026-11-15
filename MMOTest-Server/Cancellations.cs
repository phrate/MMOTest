namespace MMOTest.Server;

// The "cancel surface" — the methods Coordinator needs to call on each modal
// system. Extracted as interfaces so Coordinator can be tested with stubs
// without unsealing the systems or constructing their many dependencies.

internal interface ITradeCanceller
{
    void CancelTradeForPlayer(long playerId, string errorKey);
    void CancelTradeQueuesForPlayer(long playerId, string errorKey);
}

internal interface IDialogueCanceller
{
    void CancelDialogueForPlayer(long playerId, string errorKey);
    void CancelPendingTalkFor(long playerId);
}

internal interface IBankCanceller
{
    void CancelBankForPlayer(long playerId, string errorKey);
    void CancelPendingBankFor(long playerId);
}