using System.Text.Json;
using AsheTrader.Core.Jobs;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class TransferJobLedgerTests
{
    private static TransferJobLedger Direct(ulong amount = 100) => TransferJobLedger.Create(JobPlanner.Plan(
        Job([1], (2, amount)), [Character(1, PeerA, amount), Character(2, PeerB)]));

    private static TransferJobLedger Relay(ulong amount = 100)
    {
        var job = Job([1], (2, amount));
        job.RelayCharacters[PeerB] = 3;
        return TransferJobLedger.Create(JobPlanner.Plan(job, [Character(1, PeerA, amount), Character(2, PeerA), Character(3, PeerB)]));
    }

    private static TransferObservation Evidence(TransferLeg leg, ObservationSide side, uint quantity, bool verified = true) => new()
    {
        PeerId = side == ObservationSide.Sender ? leg.SenderPeerId : leg.ReceiverPeerId,
        CharacterId = side == ObservationSide.Sender ? leg.SenderId : leg.ReceiverId,
        Quantity = quantity, Verified = verified, ObservedAt = DateTimeOffset.UtcNow,
    };

    private static TransferOperation Commit(TransferJobLedger ledger, int legIndex, uint amount)
    {
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        var operation = ledger.PrepareBatch(allocation.Id, legIndex, amount);
        ledger.MarkIssued(operation.Id);
        var leg = allocation.Legs[legIndex];
        ledger.RecordObservation(operation.Id, ObservationSide.Sender, Evidence(leg, ObservationSide.Sender, amount));
        ledger.RecordObservation(operation.Id, ObservationSide.Receiver, Evidence(leg, ObservationSide.Receiver, amount));
        Assert.True(ledger.TryCommit(operation.Id, out var error), error);
        return operation;
    }

    [Fact]
    public void RequiresBothPeersAndCommitsExactlyOnce()
    {
        var ledger = Direct();
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        var operation = ledger.PrepareBatch(allocation.Id, 0, 40);
        ledger.MarkIssued(operation.Id);
        var sender = Evidence(allocation.Legs[0], ObservationSide.Sender, 40);
        ledger.RecordObservation(operation.Id, ObservationSide.Sender, sender);
        Assert.False(ledger.TryCommit(operation.Id, out _));
        Assert.Equal(0UL, ledger.DeliveredTo(2));
        ledger.RecordObservation(operation.Id, ObservationSide.Sender, sender);
        ledger.RecordObservation(operation.Id, ObservationSide.Receiver, Evidence(allocation.Legs[0], ObservationSide.Receiver, 40));
        Assert.True(ledger.TryCommit(operation.Id, out _));
        Assert.True(ledger.TryCommit(operation.Id, out _));
        ledger.MarkIssued(operation.Id); // Duplicate command bookkeeping must not issue new work.
        Assert.Equal(40UL, ledger.DeliveredTo(2));
        Assert.Equal(60UL, ledger.RemainingForLeg(allocation.Id, 0));
    }

    [Fact]
    public void RelayReceiptConsumesSourceButDoesNotCountFinalDelivery()
    {
        var ledger = Relay();
        Commit(ledger, 0, 60);
        Assert.Equal(60UL, ledger.SourceConsumed(1));
        Assert.Equal(0UL, ledger.DeliveredTo(2));
        Assert.Equal(60UL, ledger.RelayCargo(3));
        Commit(ledger, 1, 40);
        Assert.Equal(60UL, ledger.SourceConsumed(1));
        Assert.Equal(40UL, ledger.DeliveredTo(2));
        Assert.Equal(20UL, ledger.RelayCargo(3));
    }

    [Fact]
    public void PartialRelayCyclesRespectActualReceivedCargo()
    {
        var ledger = Relay();
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        Assert.Equal(0UL, ledger.RemainingForLeg(allocation.Id, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.PrepareBatch(allocation.Id, 1, 1));
        Commit(ledger, 0, 40); Commit(ledger, 1, 40);
        Commit(ledger, 0, 60); Commit(ledger, 1, 60);
        Assert.True(ledger.IsComplete);
        Assert.Equal(100UL, ledger.DeliveredTo(2));
        Assert.Equal(0UL, ledger.RelayCargo(3));
        Assert.Equal(TransferJobState.Completed, ledger.Journal.State);
    }

    [Fact]
    public void DepositingDoesNotReduceDeliveryOrReopenAssignment()
    {
        var ledger = Direct();
        Commit(ledger, 0, 40);
        // The retainer executor moves stock, not job allocations. Round-trip the durable state.
        var restored = new TransferJobLedger(JsonSerializer.Deserialize<TransferJobJournal>(JsonSerializer.Serialize(ledger.Journal))!);
        restored.Recover();
        Assert.Equal(40UL, restored.DeliveredTo(2));
        Assert.Equal(60UL, restored.RemainingForLeg(Assert.Single(restored.Journal.Plan.Allocations).Id, 0));
    }

    [Fact]
    public void OnlyOneUnresolvedOperationMayBePrepared()
    {
        var ledger = Direct();
        var id = Assert.Single(ledger.Journal.Plan.Allocations).Id;
        ledger.PrepareBatch(id, 0, 10);
        Assert.Throws<InvalidOperationException>(() => ledger.PrepareBatch(id, 0, 10));
    }

    [Fact]
    public void WrongIdentityOrQuantityCannotCommit()
    {
        var ledger = Direct();
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        var operation = ledger.PrepareBatch(allocation.Id, 0, 20);
        ledger.MarkIssued(operation.Id);
        var bad = Evidence(allocation.Legs[0], ObservationSide.Receiver, 19);
        ledger.RecordObservation(operation.Id, ObservationSide.Receiver, bad);
        ledger.RecordObservation(operation.Id, ObservationSide.Sender, Evidence(allocation.Legs[0], ObservationSide.Sender, 20));
        Assert.False(ledger.TryCommit(operation.Id, out _));
        Assert.Equal(TransferJobState.NeedsReconciliation, ledger.Journal.State);
        Assert.Equal(0UL, ledger.SourceConsumed(1));
    }

    [Fact]
    public void UnverifiedReportDoesNotBecomeProgress()
    {
        var ledger = Direct();
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        var operation = ledger.PrepareBatch(allocation.Id, 0, 20);
        ledger.MarkIssued(operation.Id);
        ledger.RecordObservation(operation.Id, ObservationSide.Sender, Evidence(allocation.Legs[0], ObservationSide.Sender, 20, false));
        Assert.False(ledger.TryCommit(operation.Id, out _));
        Assert.Equal(0UL, ledger.DeliveredTo(2));
    }

    [Fact]
    public void ConflictingDuplicateAfterCommitPreservesCountersButPausesJob()
    {
        var ledger = Direct();
        var operation = Commit(ledger, 0, 20);
        var leg = Assert.Single(ledger.Journal.Plan.Allocations).Legs[0];
        ledger.RecordObservation(operation.Id, ObservationSide.Receiver, Evidence(leg, ObservationSide.Receiver, 19));
        Assert.Equal(20UL, ledger.DeliveredTo(2));
        Assert.Equal(TransferOperationState.Committed, operation.State);
        Assert.Equal(TransferJobState.NeedsReconciliation, ledger.Journal.State);
    }

    [Fact]
    public void RecoveryCancelsUnissuedWorkWithoutConsumingStock()
    {
        var ledger = Direct();
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        var operation = ledger.PrepareBatch(allocation.Id, 0, 20);
        ledger.Recover();
        Assert.Equal(TransferOperationState.Cancelled, operation.State);
        Assert.Equal(100UL, ledger.RemainingForLeg(allocation.Id, 0));
        ledger.PrepareBatch(allocation.Id, 0, 20);
    }

    [Fact]
    public void RecoveryNeverReplaysAnIssuedTrade()
    {
        var ledger = Direct();
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        var operation = ledger.PrepareBatch(allocation.Id, 0, 20);
        ledger.MarkIssued(operation.Id);
        ledger.Recover();
        Assert.Equal(TransferJobState.NeedsReconciliation, ledger.Journal.State);
        Assert.Throws<InvalidOperationException>(() => ledger.MarkIssued(operation.Id));
        Assert.Throws<InvalidOperationException>(() => ledger.PrepareBatch(allocation.Id, 0, 20));
        Assert.Equal(0UL, ledger.DeliveredTo(2));
    }

    [Fact]
    public void RecoveryCanCommitPersistedMatchingEvidenceWithoutRepeatingGameAction()
    {
        var ledger = Direct();
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        var operation = ledger.PrepareBatch(allocation.Id, 0, 20);
        ledger.MarkIssued(operation.Id);
        ledger.RecordObservation(operation.Id, ObservationSide.Sender, Evidence(allocation.Legs[0], ObservationSide.Sender, 20));
        ledger.RecordObservation(operation.Id, ObservationSide.Receiver, Evidence(allocation.Legs[0], ObservationSide.Receiver, 20));
        var restored = new TransferJobLedger(JsonSerializer.Deserialize<TransferJobJournal>(JsonSerializer.Serialize(ledger.Journal))!);
        restored.Recover();
        Assert.True(restored.TryCommit(operation.Id, out var error), error);
        Assert.Equal(20UL, restored.DeliveredTo(2));
    }

    [Fact]
    public void TwoZeroChangeObservationsCanResolveAnUncertainOperation()
    {
        var ledger = Direct();
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        var operation = ledger.PrepareBatch(allocation.Id, 0, 20);
        ledger.MarkIssued(operation.Id); ledger.Recover();
        Assert.True(ledger.ResolveNoTransfer(operation.Id, Evidence(allocation.Legs[0], ObservationSide.Sender, 0), Evidence(allocation.Legs[0], ObservationSide.Receiver, 0), out var error), error);
        Assert.Equal(100UL, ledger.RemainingForLeg(allocation.Id, 0));
        Assert.False(ledger.HasPendingOperation);
    }

    [Fact]
    public void PositiveEvidenceCannotBeErasedByNoTransferResolution()
    {
        var ledger = Direct();
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        var operation = ledger.PrepareBatch(allocation.Id, 0, 20);
        ledger.MarkIssued(operation.Id);
        ledger.RecordObservation(operation.Id, ObservationSide.Sender, Evidence(allocation.Legs[0], ObservationSide.Sender, 20));
        ledger.Recover();
        Assert.False(ledger.ResolveNoTransfer(operation.Id, Evidence(allocation.Legs[0], ObservationSide.Sender, 0), Evidence(allocation.Legs[0], ObservationSide.Receiver, 0), out _));
    }

    [Fact]
    public void CancelIsAllowedOnlyBeforeIssuing()
    {
        var ledger = Direct();
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        var first = ledger.PrepareBatch(allocation.Id, 0, 20);
        ledger.CancelBeforeIssue(first.Id);
        var second = ledger.PrepareBatch(allocation.Id, 0, 20);
        ledger.MarkIssued(second.Id);
        Assert.Throws<InvalidOperationException>(() => ledger.CancelBeforeIssue(second.Id));
    }

    [Fact]
    public void SourceShortageIsNotReportedAsRequestedJobComplete()
    {
        var ledger = TransferJobLedger.Create(JobPlanner.Plan(Job([1], (2, 200)), [Character(1, PeerA, 100), Character(2, PeerB)]));
        Commit(ledger, 0, 100);
        Assert.True(ledger.IsComplete); // All feasible allocations delivered, but not all requested targets.
        Assert.Equal(TransferJobState.Paused, ledger.Journal.State);
        Assert.Contains("short", ledger.Journal.PauseReason);
    }

    [Fact]
    public void InvalidCommittedJournalWithoutBothPeerEvidenceIsRejected()
    {
        var ledger = Direct();
        var operation = Commit(ledger, 0, 20);
        operation.ReceiverObservation = null;
        Assert.Throws<InvalidDataException>(() => new TransferJobLedger(ledger.Journal));
    }

    [Fact]
    public void ForgedDuplicateOperationIdsAreRejectedWhenLoading()
    {
        var ledger = Direct();
        var operation = Commit(ledger, 0, 20);
        ledger.Journal.Operations.Add(operation);
        Assert.Throws<InvalidDataException>(() => new TransferJobLedger(ledger.Journal));
    }

    [Fact]
    public void LowerPriorityRecipientCannotStartBeforeEarlierAllocatedDelivery()
    {
        var plan = JobPlanner.Plan(Job([1], (2, 50), (3, 50)), [Character(1, PeerA, 100), Character(2, PeerB), Character(3, PeerB)]);
        var ledger = TransferJobLedger.Create(plan);
        Assert.Throws<InvalidOperationException>(() => ledger.PrepareBatch(plan.Allocations[1].Id, 0, 10));
    }

    [Fact]
    public void EmptyVerifiedMoveAllPlanCanCompleteWithoutATrade()
    {
        var ledger = TransferJobLedger.Create(JobPlanner.Plan(Job([1], (2, null)), [Character(1, PeerA), Character(2, PeerB)]));
        Assert.True(ledger.TryComplete(out var error), error);
        Assert.Equal(TransferJobState.Completed, ledger.Journal.State);
    }

    [Fact]
    public void CorruptedSourceBudgetCannotAuthorizeOverdraw()
    {
        var ledger = Direct();
        ledger.Journal.Plan.SourceBudgets[1] = 99;
        Assert.Throws<InvalidDataException>(() => new TransferJobLedger(ledger.Journal));
    }

    [Fact]
    public void ConflictingCommittedEvidenceRemainsPausedAcrossRecovery()
    {
        var ledger = Direct();
        var operation = Commit(ledger, 0, 20);
        var leg = Assert.Single(ledger.Journal.Plan.Allocations).Legs[0];
        ledger.RecordObservation(operation.Id, ObservationSide.Receiver, Evidence(leg, ObservationSide.Receiver, 19));
        ledger.Recover();
        Assert.Equal(TransferJobState.NeedsReconciliation, ledger.Journal.State);
        Assert.False(ledger.TryCommit(operation.Id, out _));
        Assert.Equal(20UL, ledger.DeliveredTo(2));
    }
}
