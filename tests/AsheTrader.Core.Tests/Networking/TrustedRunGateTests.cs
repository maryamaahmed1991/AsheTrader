using AsheTrader.Core.Networking;

namespace AsheTrader.Core.Tests.Networking;

public sealed class TrustedRunGateTests
{
    [Fact]
    public void ReloadDoesNotResumeSavedWorkEvenWhenBothPcsAreReady()
    {
        var gate = new TrustedRunGate();
        Assert.False(gate.CanProceed(true,true,true,true));
        gate.Queue(); Assert.True(gate.CanProceed(true,true,true,true));
        var reloaded = new TrustedRunGate();
        Assert.False(reloaded.CanProceed(true,true,true,true));
    }
    [Theory]
    [InlineData(false,true,true,true)]
    [InlineData(true,false,true,true)]
    [InlineData(true,true,false,true)]
    [InlineData(true,true,true,false)]
    public void QueuedWorkWaitsForEveryReadinessCondition(bool authenticated,bool control,bool local,bool remote)
    {
        var gate = new TrustedRunGate(); gate.Queue();
        Assert.False(gate.CanProceed(authenticated,control,local,remote));
        Assert.Equal(TrustedRunIntent.StartQueued,gate.Intent);
        Assert.True(gate.CanProceed(true,true,true,true));
    }
    [Fact]
    public void ReconnectOnlyRecoversAnActiveRunWhenEnabledAndPauseCancelsIt()
    {
        var gate=new TrustedRunGate();
        gate.RecoverConnection(false,true); Assert.False(gate.Armed);
        gate.RecoverConnection(true,false); Assert.False(gate.Armed);
        gate.RecoverConnection(true,true); Assert.Equal(TrustedRunIntent.ReconcileInterrupted,gate.Intent);
        gate.Cancel(); Assert.False(gate.CanProceed(true,true,true,true));
    }
}
