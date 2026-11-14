using System.Numerics;
using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelSavedPathProgressTests
{
    private static readonly Vector3 Start = new(10, 2, 20);
    private static readonly Vector3 End = new(30, 2, 40);

    [Fact]
    public void IdleDoesNotConfirmAPathThatStoppedBeforeTheDoor()
    {
        var progress = New();
        Assert.Equal(FuelSavedPathState.Waiting, progress.Observe(true, new(15, 2, 25), 500));
        Assert.Equal(FuelSavedPathState.StoppedShort, progress.Observe(false, new(15, 2, 25), 1_500));
    }

    [Fact]
    public void WaitsForIdleBeforeAcceptingTheEndpoint()
    {
        var progress = New();
        Assert.Equal(FuelSavedPathState.Waiting, progress.Observe(true, End, 2_000));
        Assert.Equal(FuelSavedPathState.Arrived, progress.Observe(false, End, 2_100));
    }

    [Fact]
    public void AnAlreadyCompletedShortPathDoesNotRequireSeeingBusy()
        => Assert.Equal(FuelSavedPathState.Arrived, New().Observe(false, End, 100));

    [Theory]
    [InlineData(999, FuelSavedPathState.Waiting)]
    [InlineData(1_000, FuelSavedPathState.StoppedShort)]
    public void AllowsABoundedStartupGrace(long elapsed, FuelSavedPathState expected)
        => Assert.Equal(expected, New().Observe(false, Start, elapsed));

    [Theory]
    [InlineData(1.5f, 3f, FuelSavedPathState.Arrived)]
    [InlineData(1.5001f, 0f, FuelSavedPathState.StoppedShort)]
    [InlineData(0f, 3.001f, FuelSavedPathState.StoppedShort)]
    [InlineData(0f, -3.001f, FuelSavedPathState.StoppedShort)]
    public void CompletionRequiresHorizontalAndVerticalProximity(float horizontal, float vertical, FuelSavedPathState expected)
        => Assert.Equal(expected, New().Observe(false, End + new Vector3(horizontal, vertical, 0), 2_000));

    [Fact]
    public void RejectsTheSameXZOnAnotherFloor()
        => Assert.Equal(FuelSavedPathState.StoppedShort, New().Observe(false, End + new Vector3(0, 8, 0), 2_000));

    [Fact]
    public void DetectsACharacterWalkingIntoAWallWhileLifestreamStaysBusy()
    {
        var progress = New();
        Assert.Equal(FuelSavedPathState.Waiting, progress.Observe(true, Start, 14_999));
        Assert.Equal(FuelSavedPathState.Stalled, progress.Observe(true, Start, 15_000));
    }

    [Fact]
    public void SmallPositionJitterDoesNotKeepAStalledPathAlive()
    {
        var progress = New();
        Assert.Equal(FuelSavedPathState.Waiting, progress.Observe(true, Start + new Vector3(0.2f, 0, 0), 5_000));
        Assert.Equal(FuelSavedPathState.Waiting, progress.Observe(true, Start + new Vector3(-0.2f, 0, 0), 10_000));
        Assert.Equal(FuelSavedPathState.Stalled, progress.Observe(true, Start + new Vector3(0.1f, 0, 0), 15_000));
    }

    [Fact]
    public void MeaningfulMovementResetsTheProgressDeadlineEvenWhenThePathMovesAwayFromTheEndpoint()
    {
        var progress = New();
        var corner = Start - new Vector3(0.5f, 0, 0);
        Assert.Equal(FuelSavedPathState.Waiting, progress.Observe(true, corner, 14_000));
        Assert.Equal(FuelSavedPathState.Waiting, progress.Observe(true, corner, 28_999));
        Assert.Equal(FuelSavedPathState.Stalled, progress.Observe(true, corner, 29_000));
    }

    [Fact]
    public void CutsceneTimeDoesNotConsumeTheStallBudget()
    {
        var progress = New();
        progress.Suspend(30_000);
        Assert.Equal(FuelSavedPathState.Waiting, progress.Observe(true, Start, 44_999));
        Assert.Equal(FuelSavedPathState.Stalled, progress.Observe(true, Start, 45_000));
    }

    [Fact]
    public void CutsceneTimeDoesNotConsumeTheStartupGrace()
    {
        var progress = New();
        progress.Suspend(30_000);
        Assert.Equal(FuelSavedPathState.Waiting, progress.Observe(false, Start, 30_999));
        Assert.Equal(FuelSavedPathState.StoppedShort, progress.Observe(false, Start, 31_000));
    }

    [Fact]
    public void NonPositiveSuspensionDoesNotExtendTheBudget()
    {
        var progress = New();
        progress.Suspend(0);
        progress.Suspend(-1_000);
        Assert.Equal(FuelSavedPathState.Stalled, progress.Observe(true, Start, 15_000));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidPositionsFailImmediately(float invalid)
    {
        var bad = new Vector3(invalid, 0, 0);
        Assert.Equal(FuelSavedPathState.StoppedShort, New().Observe(true, bad, 1));
        Assert.Equal(FuelSavedPathState.StoppedShort, new FuelSavedPathProgress(bad, Start, 0).Observe(true, Start, 1));
        Assert.Equal(FuelSavedPathState.StoppedShort, new FuelSavedPathProgress(End, bad, 0).Observe(true, Start, 1));
    }

    private static FuelSavedPathProgress New() => new(End, Start, 0);
}
