using AsheTrader.Workflow.Native;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class CutscenePreservationTests
{
    [Fact]
    public void CutsceneDoesNotFinishUntilItEndsAndExtendsDeadlineOnlyOnce()
    {
        var wait = new CutsceneWait();
        Assert.Equal(0, wait.Observe(100, false));
        Assert.Null(wait.Observe(150, true));
        Assert.True(wait.IsWaiting);
        Assert.Null(wait.Observe(600_150, true));
        Assert.Equal(600_100, wait.Observe(600_250, false));
        Assert.False(wait.IsWaiting);
        Assert.Equal(0, wait.Observe(600_300, false));
    }

    [Fact]
    public void SeparateCutscenesAndOperationResetDoNotReuseElapsedPause()
    {
        var wait = new CutsceneWait();
        Assert.Null(wait.Observe(100, true));
        Assert.Equal(100, wait.Observe(200, false));
        Assert.Null(wait.Observe(300, true));
        Assert.Equal(50, wait.Observe(350, false));
        Assert.Null(wait.Observe(400, true));
        wait.Reset();
        Assert.False(wait.IsWaiting);
        Assert.Equal(0, wait.Observe(500, false));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void NonSkippingAutoRetainerModesPermitLogin(int mode, bool multiMode)
        => Assert.Null(InnCutscenePolicy.LoginBlockReason(mode, multiMode));

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(1, true)]
    public void ActiveInnSkippingBlocksBeforeLoginWithActionableSetting(int mode, bool multiMode)
    {
        var reason = InnCutscenePolicy.LoginBlockReason(mode, multiMode);
        Assert.Contains("Skip Inn Login Cutscene to Never", reason);
        Assert.Contains("has not requested the login", reason);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(9, false)]
    [InlineData(1, null)]
    public void UnknownSkipPolicyDoesNotPermitAutomaticLogin(int? mode, bool? multiMode)
        => Assert.Contains("Could not verify", InnCutscenePolicy.LoginBlockReason(mode, multiMode));
}
