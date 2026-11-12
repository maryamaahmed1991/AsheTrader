using AsheTrader.Workflow.Native;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class StorageRecoveryTallyTests
{
    private static (GameOperation Operation, StorageMovementCheckpoint Checkpoint) Pending(bool deposit = true)
    {
        var operation = new GameOperation
        {
            Id = Guid.NewGuid(), CharacterId = 7, ItemId = 10155, HighQuality = false,
            Kind = deposit ? GameOperationKind.Deposit : GameOperationKind.Withdraw, Quantity = 1000,
            AllowedRetainerIds = [11],
        };
        return (operation, new()
        {
            OperationId = operation.Id, CharacterId = 7, ItemId = 10155, Kind = operation.Kind,
            RequestedQuantity = 1000, RetainerId = 11, BagBeforeQuantity = 600, RetainerBeforeQuantity = 400,
            AttemptedQuantity = 300, ConfirmedMovedBefore = 200,
        });
    }

    [Theory]
    [InlineData(600, 400, 200)]
    [InlineData(300, 700, 500)]
    [InlineData(483, 517, 317)]
    public void DepositRecoversZeroFullOrPartialInterruptedMovement(uint bags, uint retainer, uint cumulative)
    {
        var (operation, checkpoint) = Pending();
        Assert.Equal(cumulative, StorageRecoveryTally.Reconcile(operation, checkpoint, 7, bags, retainer));
    }

    [Theory]
    [InlineData(600, 400, 200)]
    [InlineData(900, 100, 500)]
    [InlineData(717, 283, 317)]
    public void WithdrawalRecoversZeroFullOrPartialInterruptedMovement(uint bags, uint retainer, uint cumulative)
    {
        var (operation, checkpoint) = Pending(false);
        Assert.Equal(cumulative, StorageRecoveryTally.Reconcile(operation, checkpoint, 7, bags, retainer));
    }

    [Fact]
    public void ClosedRetainerUsesBoundedBagTallyWithoutInventingAnotherRetainerObservation()
    {
        var (operation, checkpoint) = Pending();
        Assert.Equal(317U, StorageRecoveryTally.Reconcile(operation, checkpoint, 7, 483, null));
    }

    [Fact]
    public void SettledBoundaryKeepsConfirmedProgressAndRejectsAnyAdditionalMovement()
    {
        var (operation, checkpoint) = Pending();
        checkpoint.AttemptedQuantity = 0;
        checkpoint.ConfirmedMovedBefore = 500;
        checkpoint.BagBeforeQuantity = 300;
        checkpoint.RetainerBeforeQuantity = 700;
        Assert.Equal(500U, StorageRecoveryTally.Reconcile(operation, checkpoint, 7, 300, 700));
        Assert.Throws<InvalidOperationException>(() => StorageRecoveryTally.Reconcile(operation, checkpoint, 7, 299, 701));
    }

    [Theory]
    [InlineData(601)]
    [InlineData(299)]
    public void ReverseOrExcessBagDeltaNeverBecomesAcceptedProgress(uint bags)
    {
        var (operation, checkpoint) = Pending();
        Assert.Throws<InvalidOperationException>(() => StorageRecoveryTally.Reconcile(operation, checkpoint, 7, bags, null));
    }

    [Fact]
    public void OneSidedOrReversedRetainerDeltaIsRejected()
    {
        var (operation, checkpoint) = Pending();
        Assert.Throws<InvalidOperationException>(() => StorageRecoveryTally.Reconcile(operation, checkpoint, 7, 483, 400));
        Assert.Throws<InvalidOperationException>(() => StorageRecoveryTally.Reconcile(operation, checkpoint, 7, 483, 283));
    }

    [Fact]
    public void WrongCharacterQualityItemOrCommandCannotReuseTheCheckpoint()
    {
        var (operation, checkpoint) = Pending();
        Assert.Throws<InvalidOperationException>(() => StorageRecoveryTally.Reconcile(operation, checkpoint, 8, 483, 517));
        checkpoint.HighQuality = true;
        Assert.Throws<InvalidOperationException>(() => StorageRecoveryTally.Reconcile(operation, checkpoint, 7, 483, 517));
        checkpoint.HighQuality = false;
        checkpoint.ItemId++;
        Assert.Throws<InvalidOperationException>(() => StorageRecoveryTally.Reconcile(operation, checkpoint, 7, 483, 517));
        checkpoint.ItemId = operation.ItemId;
        checkpoint.OperationId = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => StorageRecoveryTally.Reconcile(operation, checkpoint, 7, 483, 517));
    }

    [Fact]
    public void DisallowedRetainerAndOutOfBoundsCheckpointRemainUnresolved()
    {
        var (operation, checkpoint) = Pending();
        checkpoint.RetainerId = 12;
        Assert.Throws<InvalidOperationException>(() => StorageRecoveryTally.Reconcile(operation, checkpoint, 7, 483, 517));
        checkpoint.RetainerId = 11;
        checkpoint.AttemptedQuantity = 801;
        Assert.Throws<InvalidOperationException>(() => StorageRecoveryTally.Reconcile(operation, checkpoint, 7, 483, 517));
    }

    [Fact]
    public void CumulativeQuantityCannotWrapPastTheRequestedAmount()
    {
        var (operation, checkpoint) = Pending();
        operation.Quantity = checkpoint.RequestedQuantity = uint.MaxValue;
        checkpoint.ConfirmedMovedBefore = uint.MaxValue;
        checkpoint.AttemptedQuantity = 1;
        Assert.Throws<InvalidOperationException>(() => StorageRecoveryTally.Reconcile(operation, checkpoint, 7, 599, 401));
    }

    [Fact]
    public void LegacyRecordsWithoutAPreActionBaselineAreNotGuessed()
    {
        var (operation, _) = Pending();
        var error = Assert.Throws<InvalidOperationException>(() => StorageRecoveryTally.Reconcile(operation, null, 7, 600, null));
        Assert.Contains("no saved inventory baseline", error.Message);
    }

    [Fact]
    public void InitialSettledCheckpointCanRecoverAnOperationThatNeverReachedARetainer()
    {
        var (operation, checkpoint) = Pending();
        checkpoint.AttemptedQuantity = checkpoint.ConfirmedMovedBefore = 0;
        checkpoint.RetainerId = 0;
        Assert.Equal(0U, StorageRecoveryTally.Reconcile(operation, checkpoint, 7, 600, null));
    }

    [Fact]
    public void StableTallyRequiresSeparateReadsAndRestartsWhenEitherInventoryChanges()
    {
        var stability = new StorageRecoveryStability();
        Assert.False(stability.Observe(1000, 483, 517));
        Assert.False(stability.Observe(1000, 483, 517));
        Assert.False(stability.Observe(1499, 483, 517));
        Assert.True(stability.Observe(1500, 483, 517));
        Assert.False(stability.Observe(1600, 482, 518));
        Assert.False(stability.Observe(2099, 482, 518));
        Assert.True(stability.Observe(2100, 482, 518));
        Assert.False(stability.Observe(2200, 482, 519));
        stability.Reset();
        Assert.False(stability.Observe(3000, 482, 519));
    }
}
