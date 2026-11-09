using AsheTrader.Core.Jobs;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    /// <summary>Fill a relay across providers, then drain its attributed cargo before collecting another load.</summary>
    private bool SelectNextTransferLeg(TransferJobLedger ledger)
    {
        var run = Run!;
        var plan = run.Journal!.Plan;
        // Both inbound and outbound work obey the ledger's recipient-priority contract.
        var recipient = plan.Definition.Recipients.FirstOrDefault(rule => plan.Allocations.Any(allocation =>
            allocation.RecipientCharacterId == rule.CharacterId
            && ledger.CommittedForLeg(allocation.Id, allocation.Legs.Count - 1) < allocation.Quantity));
        if (recipient == null) return false;
        var allocations = plan.Allocations.Where(allocation => allocation.RecipientCharacterId == recipient.CharacterId
            && ledger.CommittedForLeg(allocation.Id, allocation.Legs.Count - 1) < allocation.Quantity).ToArray();
        var relayed = allocations.Where(allocation => allocation.Legs.Count == 2).ToArray();
        if (relayed.Length != 0)
        {
            var relayId = relayed[0].Legs[0].ReceiverId;
            var group = relayed.Where(allocation => allocation.Legs[0].ReceiverId == relayId).ToArray();
            var cargo = group.Aggregate(0UL, (sum, allocation) => checked(sum + ledger.RemainingForLeg(allocation.Id, 1)));
            var incoming = group.Where(allocation => ledger.RemainingForLeg(allocation.Id, 0) > 0).ToArray();
            var capacity = RelayCapacityHint(relayId, ledger);
            if (cargo == 0 && run.RelayFlushCharacterId == relayId) run.RelayFlushCharacterId = null;
            if (cargo > 0 && (run.RelayFlushCharacterId == relayId || capacity == 0 || incoming.Length == 0))
            {
                run.RelayFlushCharacterId = relayId;
                ChooseTransferLeg(group.First(allocation => ledger.RemainingForLeg(allocation.Id, 1) > 0), 1, ledger);
                return true;
            }
            if (incoming.Length != 0)
            {
                // Finish the current provider's funded contribution before switching to another one.
                var next = incoming.FirstOrDefault(allocation => allocation.Id == run.AllocationId && run.LegIndex == 0) ?? incoming[0];
                ChooseTransferLeg(next, 0, ledger);
                if (capacity > 0) run.DesiredQuantity = Math.Min(run.DesiredQuantity, capacity);
                return true;
            }
            throw new InvalidDataException("The relay allocation has neither incoming work nor verified carried cargo.");
        }
        run.RelayFlushCharacterId = null;
        ChooseTransferLeg(allocations[0], 0, ledger);
        return true;
    }

    private void ChooseTransferLeg(TransferAllocation allocation, int legIndex, TransferJobLedger ledger)
    {
        Run!.AllocationId = allocation.Id;
        Run.LegIndex = legIndex;
        Run.DesiredQuantity = (uint)Math.Min(uint.MaxValue, ledger.RemainingForLeg(allocation.Id, legIndex));
        if (Run.DesiredQuantity == 0) throw new InvalidDataException("The next transfer has no funded quantity.");
    }

    private uint RelayCapacityHint(ulong relayId, TransferJobLedger ledger)
    {
        if (Run!.RelayCapacity.TryGetValue(relayId, out var capacity)) return capacity;
        // Old journals lack this execution hint. Subtract durable cargo from the original verified
        // capacity conservatively; actual carried space is checked again before the next trade.
        var original = Run.Snapshots.GetValueOrDefault(relayId)?.BagCapacity ?? 0;
        var cargo = ledger.RelayCargo(relayId);
        return cargo >= original ? 0 : original - (uint)cargo;
    }

    private bool IsRelayCharacter(ulong characterId) => Run!.Journal!.Plan.Allocations.Any(allocation =>
        allocation.Legs.Count == 2 && allocation.Legs[0].ReceiverId == characterId);

    private void ObserveRelayCapacity(ulong characterId, uint capacity)
    {
        if (IsRelayCharacter(characterId)) Run!.RelayCapacity[characterId] = capacity;
    }

    private void ObserveCommittedRelayCapacity(TransferLeg leg, uint quantity)
    {
        // Absolute updates from the pre-trade inspection make replaying a saved committed response
        // idempotent. Never increment/decrement a previously adjusted capacity hint on recovery.
        ObserveRelayCapacity(leg.SenderId, (uint)Math.Min(uint.MaxValue, (ulong)Run!.SenderBagCapacity + quantity));
        ObserveRelayCapacity(leg.ReceiverId, quantity >= Run.ReceiverCapacity ? 0 : Run.ReceiverCapacity - quantity);
    }
}
