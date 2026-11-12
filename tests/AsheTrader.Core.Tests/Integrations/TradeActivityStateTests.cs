using AsheTrader.Trading;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class TradeActivityStateTests
{
    private static TradeActivityState Idle => new()
    {
        LoggedIn = true, PlayerAvailable = true, InventoryAvailable = true, AgentAvailable = true,
    };

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(1, 0, true)]
    [InlineData(1, 1, true)]
    public void BothNativeIdleValuesPermitOpeningAndTallyWithoutInventingAcknowledgement(int local, int remote, bool hiddenAddonReady)
    {
        var activity = Idle with { LocalState = local, RemoteState = remote, AddonReady = hiddenAddonReady };
        Assert.True(activity.Ready);
        Assert.False(activity.Acknowledged);
        Assert.Empty(activity.BlockReason);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void EveryActiveStateOnEitherSideBlocksEvenWhenAllTradeWindowsAreAbsent(int state, bool remote)
    {
        var activity = remote ? Idle with { RemoteState = state } : Idle with { LocalState = state };
        Assert.True(activity.Acknowledged);
        Assert.False(activity.Ready);
        Assert.Contains("activity", activity.BlockReason);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(7, false)]
    [InlineData(255, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(-1, true)]
    [InlineData(7, true)]
    [InlineData(255, true)]
    [InlineData(int.MaxValue, true)]
    public void UnknownStatesOnEitherSideBlockWithoutClaimingARequestWasAcknowledged(int state, bool remote)
    {
        var activity = remote ? Idle with { RemoteState = state } : Idle with { LocalState = state };
        Assert.False(activity.Acknowledged);
        Assert.False(activity.Ready);
        Assert.Contains("unsupported trade state", activity.BlockReason);
        Assert.Contains(state.ToString(), activity.BlockReason);
    }

    [Theory]
    [InlineData(nameof(TradeActivityState.SyncPending))]
    [InlineData(nameof(TradeActivityState.AddonVisible))]
    [InlineData(nameof(TradeActivityState.TradeOpenCondition))]
    [InlineData(nameof(TradeActivityState.AgentActive))]
    public void NativeActivitySignalsOverrideIdleCounters(string flag)
    {
        var activity = flag switch
        {
            nameof(TradeActivityState.SyncPending) => Idle with { SyncPending = true },
            nameof(TradeActivityState.AddonVisible) => Idle with { AddonVisible = true, AddonReady = false },
            nameof(TradeActivityState.TradeOpenCondition) => Idle with { TradeOpenCondition = true },
            nameof(TradeActivityState.AgentActive) => Idle with { AgentActive = true },
            _ => throw new ArgumentOutOfRangeException(nameof(flag)),
        };
        Assert.True(activity.Acknowledged);
        Assert.False(activity.Ready);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisibleQuantityOrConfirmationWindowBlocksTallyWithoutClaimingTradeActivity(bool confirmation)
    {
        var activity = confirmation ? Idle with { ConfirmationVisible = true } : Idle with { NumericVisible = true };
        Assert.False(activity.Ready);
        Assert.False(activity.Acknowledged);
        Assert.Contains("window visible", activity.BlockReason);
    }

    [Theory]
    [InlineData(nameof(TradeActivityState.LoggedIn))]
    [InlineData(nameof(TradeActivityState.PlayerAvailable))]
    [InlineData(nameof(TradeActivityState.InventoryAvailable))]
    [InlineData(nameof(TradeActivityState.AgentAvailable))]
    public void MissingNativeAvailabilityCannotMasqueradeAsZeroIdleState(string flag)
    {
        var activity = flag switch
        {
            nameof(TradeActivityState.LoggedIn) => Idle with { LoggedIn = false },
            nameof(TradeActivityState.PlayerAvailable) => Idle with { PlayerAvailable = false },
            nameof(TradeActivityState.InventoryAvailable) => Idle with { InventoryAvailable = false },
            nameof(TradeActivityState.AgentAvailable) => Idle with { AgentAvailable = false },
            _ => throw new ArgumentOutOfRangeException(nameof(flag)),
        };
        Assert.False(activity.Ready);
        Assert.Contains("unavailable", activity.BlockReason);
    }

    [Fact]
    public void DiagnosticSnapshotExplainsRawIdleStateWithoutAPartnerOrTradeWindow()
    {
        var text = Idle.Describe();
        Assert.Contains("ready=True", text);
        Assert.Contains("localState=0; remoteState=0", text);
        Assert.Contains("inventoryAvailable=True; agentAvailable=True", text);
        Assert.Contains("addonVisible=False; addonReady=False", text);
        Assert.Contains("tradeOpen=False; agentActive=False", text);
        Assert.Contains("numericVisible=False; confirmationVisible=False", text);
    }
}
