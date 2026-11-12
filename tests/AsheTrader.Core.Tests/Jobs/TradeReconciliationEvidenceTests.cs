using AsheTrader.Workflow;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class TradeReconciliationEvidenceTests
{
    [Theory]
    [InlineData(WorkerCommandKind.SendBatch, 500U, 500U, true, 0U)]
    [InlineData(WorkerCommandKind.SendBatch, 500U, 400U, false, 100U)]
    [InlineData(WorkerCommandKind.ReceiveBatch, 20U, 120U, false, 100U)]
    [InlineData(WorkerCommandKind.ReceiveBatch, 20U, 70U, false, 0U)]
    public void RefreshPreservesNativeFailureButNeverInfersSuccess(WorkerCommandKind kind, uint before, uint current, bool unchanged, uint quantity)
    {
        var command = new WorkerCommand { Id = Guid.NewGuid(), Kind = kind, Operation = new() { Quantity = 100 } };
        var original = new WorkerResponse { CommandId = command.Id, Ambiguous = true, Error = "RequestTrade timed out while waiting for the trade window." };

        var first = TradeReconciliationEvidence.Capture(command, original, before, current);
        var repeated = TradeReconciliationEvidence.Capture(command, first, before, current);

        Assert.Equal(original.Error, repeated.Error);
        Assert.Equal(before, repeated.BeforeQuantity);
        Assert.Equal(current, repeated.CurrentQuantity);
        Assert.Equal(unchanged, repeated.Unchanged);
        Assert.Equal(quantity, repeated.Quantity);
        Assert.True(repeated.Ambiguous);
        Assert.False(repeated.Success);
        Assert.False(repeated.OperatorReviewed);
        Assert.False(repeated.NeverStarted);
        Assert.Null(original.BeforeQuantity);
        Assert.Null(original.CurrentQuantity);
    }

    [Fact]
    public void MissingDurableResultUsesInterruptedExplanationWithoutClaimingNeverStarted()
    {
        var command = new WorkerCommand { Id = Guid.NewGuid(), Kind = WorkerCommandKind.SendBatch, Operation = new() { Quantity = 100 } };
        var result = TradeReconciliationEvidence.Capture(command, null, 500, 500);

        Assert.Contains("interrupted before a durable success", result.Error);
        Assert.True(result.Unchanged);
        Assert.True(result.Ambiguous);
        Assert.False(result.NeverStarted);
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void SettledWorkerResultCannotBeReclassified(bool success, bool reviewed, bool neverStarted)
    {
        var command = new WorkerCommand { Id = Guid.NewGuid(), Kind = WorkerCommandKind.ReceiveBatch };
        var result = new WorkerResponse { CommandId = command.Id, Success = success, OperatorReviewed = reviewed, NeverStarted = neverStarted };
        Assert.Throws<InvalidOperationException>(() => TradeReconciliationEvidence.Capture(command, result, 100, 100));
    }
}
