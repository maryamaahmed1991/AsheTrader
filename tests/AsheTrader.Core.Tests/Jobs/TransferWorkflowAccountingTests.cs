using System.Text.Json;
using AsheTrader.Core.Jobs;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

/// <summary>Exercises a whole job's accounting through small bags, relay cycles, retainer trips, and restarts.</summary>
public sealed class TransferWorkflowAccountingTests
{
    [Fact]
    public void MultiSourcePriorityJobSurvivesRelayOverflowDepositsAndSerializedRestarts()
    {
        var definition = Job([1, 2], (4, 9000), (5, null));
        definition.RelayCharacters[PeerB] = 3;
        var initial = new[]
        {
            Character(1, PeerA, 100, 14900), Character(2, PeerA, 0, 7000),
            Character(3, PeerB, 0, 1234), // Existing relay stock must remain owned by the relay.
            Character(4, PeerA), Character(5, PeerB),
        };
        var plan = JobPlanner.Plan(definition, initial);
        var ledger = TransferJobLedger.Create(plan);
        var bag = initial.ToDictionary(character => character.CharacterId, character => character.BagQuantity);
        var stored = initial.ToDictionary(character => character.CharacterId, character => character.Retainers.Sum(retainer => (long)retainer.Quantity));
        var capacity = new Dictionary<ulong, ulong> { [1] = 800, [2] = 800, [3] = 600, [4] = 450, [5] = 550 };
        var committed = 0;
        var pickups = 0;
        var deposits = 0;

        foreach (var originalAllocation in plan.Allocations)
        {
            while (ledger.CommittedForLeg(originalAllocation.Id, originalAllocation.Legs.Count - 1) < originalAllocation.Quantity)
            {
                var legIndex = originalAllocation.Legs.Count == 2 && ledger.RemainingForLeg(originalAllocation.Id, 1) > 0 ? 1 : 0;
                var leg = originalAllocation.Legs[legIndex];
                if (bag[leg.SenderId] == 0)
                {
                    var pickup = Math.Min(capacity[leg.SenderId], Math.Min((ulong)stored[leg.SenderId], ledger.RemainingForLeg(originalAllocation.Id, legIndex)));
                    Assert.True(pickup > 0);
                    bag[leg.SenderId] += pickup;
                    stored[leg.SenderId] -= (long)pickup;
                    pickups++;
                }
                var receiveSpace = capacity[leg.ReceiverId] - bag[leg.ReceiverId];
                if (receiveSpace == 0)
                {
                    var beforeDelivered = ledger.DeliveredTo(leg.ReceiverId);
                    var beforeRelay = ledger.RelayCargo(leg.ReceiverId);
                    stored[leg.ReceiverId] += (long)bag[leg.ReceiverId];
                    bag[leg.ReceiverId] = 0;
                    receiveSpace = capacity[leg.ReceiverId];
                    deposits++;
                    Assert.Equal(beforeDelivered, ledger.DeliveredTo(leg.ReceiverId));
                    Assert.Equal(beforeRelay, ledger.RelayCargo(leg.ReceiverId));
                }

                var quantity = checked((uint)Math.Min(495UL, Math.Min(receiveSpace,
                    Math.Min(bag[leg.SenderId], ledger.RemainingForLeg(originalAllocation.Id, legIndex)))));
                var operation = ledger.PrepareBatch(originalAllocation.Id, legIndex, quantity);
                ledger.MarkIssued(operation.Id);
                bag[leg.SenderId] -= quantity;
                bag[leg.ReceiverId] += quantity;
                ledger.RecordObservation(operation.Id, ObservationSide.Sender, new()
                { PeerId = leg.SenderPeerId, CharacterId = leg.SenderId, Quantity = quantity, Verified = true });
                ledger.RecordObservation(operation.Id, ObservationSide.Receiver, new()
                { PeerId = leg.ReceiverPeerId, CharacterId = leg.ReceiverId, Quantity = quantity, Verified = true });

                // Simulate a crash after both durable acknowledgments, before journal commit.
                if (committed % 7 == 0)
                {
                    ledger = RoundTrip(ledger);
                    ledger.Recover();
                    Assert.Equal(TransferJobState.NeedsReconciliation, ledger.Journal.State);
                }
                Assert.True(ledger.TryCommit(operation.Id, out var error), error);
                Assert.True(ledger.TryCommit(operation.Id, out error), error); // Retransmitted acknowledgment.
                committed++;

                if (leg.ReceiverId == 5) Assert.Equal(9000UL, ledger.DeliveredTo(4));
                if (committed % 5 == 0)
                {
                    ledger = RoundTrip(ledger);
                    ledger.Recover();
                }
                Assert.True(committed < 1000, "The planner must make monotonic progress, not re-collect delivered stock.");
            }
        }

        Assert.True(ledger.TryComplete(out var failure), failure);
        Assert.Equal(9000UL, ledger.DeliveredTo(4));
        Assert.Equal(13000UL, ledger.DeliveredTo(5));
        Assert.Equal(15000UL, ledger.SourceConsumed(1));
        Assert.Equal(7000UL, ledger.SourceConsumed(2));
        Assert.Equal(0UL, ledger.RelayCargo(3));
        Assert.Equal(1234UL, bag[3] + (ulong)stored[3]);
        Assert.Equal(0UL, bag[1] + (ulong)stored[1]);
        Assert.Equal(0UL, bag[2] + (ulong)stored[2]);
        Assert.Equal(9000UL, bag[4] + (ulong)stored[4]);
        Assert.Equal(13000UL, bag[5] + (ulong)stored[5]);
        Assert.True(deposits > 10);
        Assert.True(pickups > 10);
        Assert.True(committed > 30);
    }

    [Fact]
    public void LargeCharacterIdsAndFullEvidenceSurviveJsonRoundTrip()
    {
        const ulong source = 9_007_199_254_740_993;
        const ulong recipient = 9_007_199_254_741_117;
        var ledger = TransferJobLedger.Create(JobPlanner.Plan(Job([source], (recipient, 10)),
            [Character(source, PeerA, 10), Character(recipient, PeerB)]));
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        var operation = ledger.PrepareBatch(allocation.Id, 0, 10);
        ledger.MarkIssued(operation.Id);
        ledger.RecordObservation(operation.Id, ObservationSide.Sender, new()
        { PeerId = PeerA, CharacterId = source, Quantity = 10, Verified = true, ObservedAt = DateTimeOffset.UtcNow });
        ledger.RecordObservation(operation.Id, ObservationSide.Receiver, new()
        { PeerId = PeerB, CharacterId = recipient, Quantity = 10, Verified = true, ObservedAt = DateTimeOffset.UtcNow });
        Assert.True(ledger.TryCommit(operation.Id, out _));

        var restored = RoundTrip(ledger);
        Assert.Equal(source, Assert.Single(restored.Journal.Plan.Definition.Sources));
        Assert.Equal(recipient, Assert.Single(restored.Journal.Plan.Definition.Recipients).CharacterId);
        Assert.Equal(source, Assert.Single(restored.Journal.Operations).SenderObservation!.CharacterId);
        Assert.Equal(recipient, Assert.Single(restored.Journal.Operations).ReceiverObservation!.CharacterId);
        Assert.Equal(10UL, restored.DeliveredTo(recipient));
        Assert.Equal(TransferJobState.Completed, restored.Journal.State);
    }

    [Fact]
    public void FullDestinationStoragePauseKeepsAllPriorDeliveryAndRelayReservations()
    {
        var definition = Job([1], (2, 100));
        definition.RelayCharacters[PeerB] = 3;
        var ledger = TransferJobLedger.Create(JobPlanner.Plan(definition,
            [Character(1, PeerA, 100), Character(2, PeerA), Character(3, PeerB)]));
        var allocation = Assert.Single(ledger.Journal.Plan.Allocations);
        foreach (var (index, quantity) in new[] { (0, 70U), (1, 40U) })
        {
            var leg = allocation.Legs[index];
            var operation = ledger.PrepareBatch(allocation.Id, index, quantity);
            ledger.MarkIssued(operation.Id);
            ledger.RecordObservation(operation.Id, ObservationSide.Sender, new()
            { PeerId = leg.SenderPeerId, CharacterId = leg.SenderId, Quantity = quantity, Verified = true });
            ledger.RecordObservation(operation.Id, ObservationSide.Receiver, new()
            { PeerId = leg.ReceiverPeerId, CharacterId = leg.ReceiverId, Quantity = quantity, Verified = true });
            Assert.True(ledger.TryCommit(operation.Id, out _));
        }
        ledger.Pause("Every permitted destination retainer is full.");
        var restored = RoundTrip(ledger);
        restored.Recover();
        Assert.Equal(40UL, restored.DeliveredTo(2));
        Assert.Equal(70UL, restored.SourceConsumed(1));
        Assert.Equal(30UL, restored.RelayCargo(3));
        Assert.Equal(30UL, restored.RemainingForLeg(allocation.Id, 0));
        Assert.Equal(30UL, restored.RemainingForLeg(allocation.Id, 1));
        Assert.False(restored.TryComplete(out _));
    }

    private static TransferJobLedger RoundTrip(TransferJobLedger ledger) =>
        new(JsonSerializer.Deserialize<TransferJobJournal>(JsonSerializer.Serialize(ledger.Journal))!);
}
