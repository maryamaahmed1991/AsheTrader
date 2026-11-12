namespace AsheTrader.Trading;

/// <summary>Classifies native trade activity independently of enum names and addon readiness.</summary>
internal sealed record TradeActivityState
{
    public bool LoggedIn { get; init; }
    public bool PlayerAvailable { get; init; }
    public bool InventoryAvailable { get; init; }
    public bool AgentAvailable { get; init; }
    public int LocalState { get; init; }
    public int RemoteState { get; init; }
    public bool SyncPending { get; init; }
    public bool AddonVisible { get; init; }
    public bool AddonReady { get; init; }
    public bool TradeOpenCondition { get; init; }
    public bool AgentActive { get; init; }
    public bool NumericVisible { get; init; }
    public bool ConfirmationVisible { get; init; }

    // The game uses both the initial zero and named NotTrading (one) states while idle.
    public static bool IsIdleState(int state) => state is 0 or 1;
    public static bool IsActiveState(int state) => state is >= 2 and <= 6;

    public bool Acknowledged => IsActiveState(LocalState) || IsActiveState(RemoteState)
        || AddonVisible || TradeOpenCondition || AgentActive || SyncPending;

    public string BlockReason => !LoggedIn || !PlayerAvailable ? "local player unavailable"
        : !InventoryAvailable ? "inventory manager unavailable"
        : !AgentAvailable ? "trade agent unavailable"
        : Acknowledged ? "native trade activity still present"
        : !IsIdleState(LocalState) || !IsIdleState(RemoteState) ? $"unsupported trade state (local={LocalState}, remote={RemoteState})"
        : NumericVisible || ConfirmationVisible ? "quantity or confirmation window visible"
        : "";

    public bool Ready => BlockReason.Length == 0;

    public string Describe() => $"ready={Ready}; acknowledged={Acknowledged}; blocked={BlockReason}; "
        + $"loggedIn={LoggedIn}; playerAvailable={PlayerAvailable}; inventoryAvailable={InventoryAvailable}; agentAvailable={AgentAvailable}; "
        + $"localState={LocalState}; remoteState={RemoteState}; sync={SyncPending}; addonVisible={AddonVisible}; addonReady={AddonReady}; "
        + $"tradeOpen={TradeOpenCondition}; agentActive={AgentActive}; numericVisible={NumericVisible}; confirmationVisible={ConfirmationVisible}";
}
