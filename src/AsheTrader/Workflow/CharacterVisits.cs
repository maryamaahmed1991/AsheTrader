using AsheTrader.Core.Jobs;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private sealed record VisitCandidate(int ItemIndex, TransferAllocation Allocation, int LegIndex,
        TransferLeg Leg, ulong Remaining, uint WorldId, int RouteOrder);

    /// <summary>False means every remaining item has no executable allocation left.</summary>
    private bool AdvanceCharacterVisit()
    {
        var run = Run!;
        if (run.Visit == null)
        {
            if (run.Pending.Count != 0) throw new InvalidDataException("Resolve pending commands before selecting a character visit.");
            run.Visit = SelectCharacterVisit();
            if (run.Visit == null) return false;
            FocusVisitItem(run.Visit.Lines[0].ItemIndex);
            run.EffectiveMeeting = Clone(run.Visit.Meeting);
            run.MeetingPinned = true;
            var world = RoutingWorlds().Single(world => world.WorldId == run.Visit.Meeting.WorldId);
            run.MeetingWorldName = world.Name;
            run.MeetingDataCenterName = world.DataCenterName;
            run.MeetingReason = "One character visit prepares and delivers its assigned items together";
            run.Step = CoordinatorStep.PrepareParticipants;
            SaveRun();
        }
        var visit = run.Visit;
        ValidateCharacterVisit(visit);
        if (visit.Recovering)
        {
            RecoverCharacterVisit(visit);
            return true;
        }
        foreach (var sender in new[] { true, false })
        {
            var lane = sender ? visit.Sender : visit.Receiver;
            var peerId = sender ? visit.SenderPeerId : visit.ReceiverPeerId;
            var characterId = sender ? visit.SenderId : visit.ReceiverId;
            var pending = run.Pending.SingleOrDefault(pending => pending.PeerId == peerId);
            if (pending?.Response == null) continue;
            if (pending.Command.Operation.Kind != VisitOperationKind(lane.Stage)
                || pending.InventoryItemIndex != VisitCommandItem(visit, lane))
                throw new InvalidDataException("The saved operation does not match its character visit lane.");
            if (!ResolvePreparationStorage(pending)) continue;
            var response = pending.Response!;
            if (!response.Success && !response.AutoReconciled)
                throw new InvalidOperationException($"{Character(characterId).Character.Name}: {response.Error}");
            var result = response.Observation ?? throw new InvalidDataException("The worker omitted its visit inventory observation.");
            if (result.CharacterId != characterId || result.Id != pending.Command.Id)
                throw new InvalidDataException("The worker observed a different visit character or operation.");
            if (result.CurrentWorldId != 0) run.ObservedWorlds[characterId] = result.CurrentWorldId;
            if (response.AutoReconciled && !response.Success)
            {
                lane.Stage = VisitPreparationStage.Inspect;
                lane.LineIndex = 0;
                lane.StorageItemIndex = null;
            }
            else ApplyVisitResult(visit, lane, sender, result);
            run.Pending.Remove(pending);
            SaveRun();
        }
        if (visit.ReplanRequested)
        {
            if (run.Pending.Count == 0)
            {
                run.Visit = null;
                run.MeetingPinned = false;
                run.Step = CoordinatorStep.SelectLeg;
                SaveRun();
            }
            return true;
        }
        if (visit.Sender.Stage == VisitPreparationStage.Ready && visit.Receiver.Stage == VisitPreparationStage.Ready)
        {
            if (run.Pending.Count != 0) throw new InvalidDataException("A ready visit still has an outstanding operation.");
            // The receiver may have inspected first while the sender was still collecting
            // later item types. Recheck shared-slot demand with every final stack size known.
            if (NeedsVisitReceivingSlots(visit) && TryPrepareVisitDeposit(visit, visit.Receiver, false)) SaveRun();
            else
            {
                if (!visit.Lines.Any(line => line.SenderBagQuantity > 0 && line.ReceiverBagCapacity > 0))
                    throw new InvalidOperationException("None of this character's assigned items fit the recipient's current inventory. Free space, then resume; no empty trade was issued.");
                RecordDiagnostic($"Character visit ready; visit={visit.Id}; sender={visit.SenderId}; receiver={visit.ReceiverId}; items={visit.Lines.Count}; world={visit.Meeting.WorldId}");
                BeginPreparedVisitTrade(visit);
                return true;
            }
        }
        foreach (var sender in new[] { true, false })
        {
            var lane = sender ? visit.Sender : visit.Receiver;
            var peerId = sender ? visit.SenderPeerId : visit.ReceiverPeerId;
            var characterId = sender ? visit.SenderId : visit.ReceiverId;
            if (lane.Stage == VisitPreparationStage.Ready || run.Pending.Any(pending => pending.PeerId == peerId)) continue;
            var itemIndex = VisitCommandItem(visit, lane);
            var kind = VisitOperationKind(lane.Stage);
            var quantity = kind is GameOperationKind.Withdraw or GameOperationKind.Deposit ? lane.StorageQuantity : 0;
            Issue(peerId, Command(characterId, kind, quantity,
                kind == GameOperationKind.TravelMeeting ? visit.Meeting : null, item: run.VisitItems[itemIndex].Definition),
                inventoryItemIndex: itemIndex);
            if (run.Pending.Any(pending => pending.Response is { Success: false })) break;
        }
        run.Status = $"{Character(visit.SenderId).Character.Name}: {VisitStageLabel(visit.Sender)} · "
            + $"{Character(visit.ReceiverId).Character.Name}: {VisitStageLabel(visit.Receiver)} · {visit.Lines.Count} item(s) in this visit";
        SaveRun();
        return true;
    }

    private PreparedVisit? SelectCharacterVisit()
    {
        var run = Run!;
        var eligible = new List<(int Index, VisitItemState State, TransferJobLedger Ledger, TransferAllocation Allocation)>();
        foreach (var (index, state) in run.VisitItems.OrderBy(pair => pair.Key))
        {
            if (state.Journal == null) continue;
            var ledger = new TransferJobLedger(state.Journal);
            if (ledger.HasPendingOperation) throw new InvalidOperationException("An item still has unresolved trade evidence before visit selection.");
            var recipient = state.Journal.Plan.Definition.Recipients.FirstOrDefault(rule => state.Journal.Plan.Allocations.Any(allocation =>
                allocation.RecipientCharacterId == rule.CharacterId
                && ledger.CommittedForLeg(allocation.Id, allocation.Legs.Count - 1) < allocation.Quantity));
            if (recipient == null) continue;
            foreach (var allocation in state.Journal.Plan.Allocations.Where(allocation => allocation.RecipientCharacterId == recipient.CharacterId
                         && ledger.CommittedForLeg(allocation.Id, allocation.Legs.Count - 1) < allocation.Quantity))
                eligible.Add((index, state, ledger, allocation));
        }
        if (eligible.Count == 0) return null;
        var relays = eligible.Where(entry => entry.Allocation.Legs.Count == 2)
            .Select(entry => entry.Allocation.Legs[0].ReceiverId).Distinct().ToArray();
        var flushing = new HashSet<ulong>();
        foreach (var relay in relays)
        {
            var cargo = run.VisitItems.Values.Where(state => state.Journal != null)
                .Aggregate(0UL, (sum, state) => checked(sum + new TransferJobLedger(state.Journal!).RelayCargo(relay)));
            if (cargo == 0)
            {
                foreach (var state in run.VisitItems.Values.Where(state => state.RelayFlushCharacterId == relay)) state.RelayFlushCharacterId = null;
                continue;
            }
            var incoming = eligible.Where(entry => entry.Allocation.Legs.Count == 2
                && entry.Allocation.Legs[0].ReceiverId == relay && entry.Ledger.RemainingForLeg(entry.Allocation.Id, 0) > 0).ToArray();
            if (run.VisitItems.Values.Any(state => state.RelayFlushCharacterId == relay) || incoming.Length == 0
                || incoming.All(entry => entry.State.RelayCapacity.TryGetValue(relay, out var capacity) && capacity == 0))
            {
                flushing.Add(relay);
                MarkVisitRelayForDelivery(relay);
            }
        }
        var candidates = new List<VisitCandidate>();
        foreach (var entry in eligible)
        {
            var allocation = entry.Allocation;
            var legIndex = allocation.Legs.Count == 2 && flushing.Contains(allocation.Legs[0].ReceiverId) ? 1 : 0;
            var remaining = entry.Ledger.RemainingForLeg(allocation.Id, legIndex);
            if (remaining == 0) continue;
            var leg = allocation.Legs[legIndex];
            var world = VisitWorld(entry.State, allocation, legIndex);
            var order = entry.State.CollectionRoute?.FindIndex(stop => stop.AllocationId == allocation.Id) ?? 0;
            candidates.Add(new(entry.Index, allocation, legIndex, leg, remaining, world, order < 0 ? int.MaxValue : order));
        }
        if (candidates.Count == 0) throw new InvalidDataException("Unfinished item allocations have no funded character visit.");
        var chosen = candidates.GroupBy(candidate => (candidate.Leg.SenderId, candidate.Leg.ReceiverId,
                candidate.Leg.SenderPeerId, candidate.Leg.ReceiverPeerId, candidate.WorldId))
            .OrderBy(group => flushing.Contains(group.Key.SenderId) ? 0 : 1)
            .ThenBy(group => group.Key.SenderId == run.LastVisitSenderId ? 0 : 1)
            .ThenBy(group => group.Key.ReceiverId == run.LastVisitReceiverId ? 0 : 1)
            .ThenBy(group => VisitTravelCost(group.Key.ReceiverId, group.Key.WorldId))
            .ThenByDescending(group => group.Select(candidate => candidate.ItemIndex).Distinct().Count())
            .ThenBy(group => group.Min(candidate => candidate.RouteOrder))
            .ThenBy(group => group.Min(candidate => candidate.ItemIndex))
            .ThenBy(group => group.Key.SenderId).ThenBy(group => group.Key.ReceiverId).First();
        // One operation per item journal can be unresolved. Additional allocations for the
        // same item/pair follow in another batch without logging out this pair.
        var lines = chosen.GroupBy(candidate => candidate.ItemIndex)
            .Select(group => group.OrderBy(candidate => candidate.RouteOrder).ThenBy(candidate => candidate.Allocation.Id).First())
            .OrderBy(candidate => candidate.ItemIndex).Select(candidate => new VisitWorkLine
            {
                ItemIndex = candidate.ItemIndex, AllocationId = candidate.Allocation.Id,
                LegIndex = candidate.LegIndex, MaximumQuantity = candidate.Remaining,
            }).ToList();
        var destination = Clone(run.Job.Meeting);
        destination.WorldId = chosen.Key.WorldId;
        return new()
        {
            SenderId = chosen.Key.SenderId, ReceiverId = chosen.Key.ReceiverId,
            SenderPeerId = chosen.Key.SenderPeerId, ReceiverPeerId = chosen.Key.ReceiverPeerId,
            Meeting = destination, Lines = lines,
        };
    }

    private int VisitTravelCost(ulong receiverId, uint destinationWorld)
    {
        var worlds = RoutingWorlds();
        var observed = Run!.ObservedWorlds.GetValueOrDefault(receiverId);
        if (observed == 0)
        {
            var home = Run.VisitItems.Values.Select(state => state.Snapshots.GetValueOrDefault(receiverId))
                .FirstOrDefault(snapshot => snapshot != null)?.World;
            observed = worlds.FirstOrDefault(world => string.Equals(world.Name, home, StringComparison.OrdinalIgnoreCase))?.WorldId ?? 0;
        }
        if (observed == destinationWorld) return 0;
        var current = worlds.FirstOrDefault(world => world.WorldId == observed);
        var destination = worlds.Single(world => world.WorldId == destinationWorld);
        return current?.DataCenterId == destination.DataCenterId ? 1 : 2;
    }

    private uint VisitWorld(VisitItemState state, TransferAllocation allocation, int legIndex)
    {
        var worldId = legIndex == 0
            ? state.CollectionRoute?.Single(stop => stop.AllocationId == allocation.Id).WorldId
                ?? throw new InvalidDataException("The item has no saved collection itinerary.")
            : RoutingWorlds().Single(world => string.Equals(world.Name, state.Snapshots[allocation.RecipientCharacterId].World,
                StringComparison.OrdinalIgnoreCase)).WorldId;
        var worlds = RoutingWorlds();
        var destination = worlds.Single(world => world.WorldId == worldId);
        var leg = allocation.Legs[legIndex];
        foreach (var id in new[] { leg.SenderId, leg.ReceiverId })
        {
            var snapshot = state.Snapshots[id];
            var home = worlds.Single(world => string.Equals(world.Name, snapshot.World, StringComparison.OrdinalIgnoreCase));
            if (home.DataCenterId != destination.DataCenterId && home.Region != destination.Region && destination.Region != 4)
                throw new InvalidOperationException($"{snapshot.Name} cannot visit {destination.Name} under the supported DC travel rules.");
        }
        return worldId;
    }

    private void ValidateCharacterVisit(PreparedVisit visit)
    {
        if (visit.Id == Guid.Empty || visit.SenderPeerId == visit.ReceiverPeerId || visit.SenderId == visit.ReceiverId
            || visit.Lines.Count == 0 || visit.Lines.Select(line => line.ItemIndex).Distinct().Count() != visit.Lines.Count)
            throw new InvalidDataException("The saved mixed-item character visit is invalid.");
        foreach (var line in visit.Lines)
        {
            var state = Run!.VisitItems[line.ItemIndex];
            var allocation = state.Journal!.Plan.Allocations.Single(allocation => allocation.Id == line.AllocationId);
            var leg = allocation.Legs[line.LegIndex];
            if (leg.SenderId != visit.SenderId || leg.ReceiverId != visit.ReceiverId
                || leg.SenderPeerId != visit.SenderPeerId || leg.ReceiverPeerId != visit.ReceiverPeerId
                || VisitWorld(state, allocation, line.LegIndex) != visit.Meeting.WorldId)
                throw new InvalidDataException("A visit line no longer matches its pinned characters and destination.");
        }
        if (Run!.Pending.Count > 2 || Run.Pending.Select(pending => pending.PeerId).Distinct().Count() != Run.Pending.Count
            || Run.Pending.Any(pending => pending.Command.Kind != WorkerCommandKind.Game
                || pending.InventoryItemIndex is not { } index || !Run.VisitItems.ContainsKey(index)
                || !(pending.PeerId == visit.SenderPeerId && pending.Command.Operation.CharacterId == visit.SenderId
                    || pending.PeerId == visit.ReceiverPeerId && pending.Command.Operation.CharacterId == visit.ReceiverId)))
            throw new InvalidDataException("Each PC must have at most one owned visit preparation command.");
    }

    private void ApplyVisitResult(PreparedVisit visit, VisitParticipantPreparation lane, bool sender, GameOperationResult result)
    {
        var characterId = sender ? visit.SenderId : visit.ReceiverId;
        if (lane.Stage == VisitPreparationStage.Login)
        { lane.Stage = VisitPreparationStage.Inspect; lane.LineIndex = 0; return; }
        if (lane.Stage == VisitPreparationStage.Travel)
        {
            if (result.CurrentWorldId != visit.Meeting.WorldId) throw new InvalidDataException("A character arrived on the wrong visit world.");
            lane.Stage = VisitPreparationStage.FinalInspect; lane.LineIndex = 0; return;
        }
        if (lane.Stage == VisitPreparationStage.Deposit)
        {
            lane.StorageItemIndex = null;
            // An overflow trip can store all final-delivered job items while the same
            // character is at its bell, instead of returning once per item type.
            if (TryPrepareVisitDeposit(visit, lane, sender)) return;
            lane.Stage = VisitPreparationStage.Inspect;
            lane.LineIndex = 0;
            return;
        }
        var line = visit.Lines[lane.LineIndex];
        var state = Run!.VisitItems[line.ItemIndex];
        if (result.StackSize == 0) throw new InvalidDataException("A visit inventory observation omitted the item stack size.");
        if (sender)
        {
            line.SenderBagQuantity = result.BagQuantity;
            line.SenderBagCapacity = result.BagCapacity;
            line.SenderFreeBagSlots = result.FreeBagSlots;
            line.StackSize = result.StackSize;
        }
        else
        {
            line.ReceiverBagCapacity = result.BagCapacity;
            line.ReceiverFreeBagSlots = result.FreeBagSlots;
        }
        if (state.Journal!.Plan.Allocations.Any(allocation => allocation.Legs.Count == 2 && allocation.Legs[0].ReceiverId == characterId))
            state.RelayCapacity[characterId] = result.BagCapacity;
        if (lane.Stage == VisitPreparationStage.Withdraw)
        {
            lane.StorageItemIndex = null;
            if (result.BagQuantity == 0 && result.MovedQuantity == 0)
                throw new InvalidOperationException("The provider no longer has the planned item in bags or accessible retainers. Other confirmed progress is preserved.");
            AdvanceVisitLine(visit, lane, sender);
            return;
        }
        if (sender && lane.Stage == VisitPreparationStage.Inspect && line.LegIndex == 0
            && result.BagQuantity < line.MaximumQuantity && result.BagCapacity > 0)
        {
            // Keep physical slots for other item types in this same visit instead of
            // letting the first large request fill every free slot by itself.
            var pickupCapacity = result.BagCapacity;
            if (result.FreeBagSlots is { } free && lane.LineIndex + 1 < visit.Lines.Count)
            {
                var laterItems = (uint)(visit.Lines.Count - lane.LineIndex - 1);
                var reserve = Math.Min(free, laterItems);
                if (result.BagQuantity == 0 && reserve == free && free > 0) reserve--;
                var reservedCapacity = (ulong)reserve * result.StackSize;
                pickupCapacity = result.BagCapacity > reservedCapacity ? (uint)(result.BagCapacity - reservedCapacity) : 0;
            }
            if (pickupCapacity == 0) { AdvanceVisitLine(visit, lane, sender); return; }
            lane.StorageItemIndex = line.ItemIndex;
            lane.StorageQuantity = (uint)Math.Min(uint.MaxValue,
                Math.Min(line.MaximumQuantity - result.BagQuantity, pickupCapacity));
            lane.Stage = VisitPreparationStage.Withdraw;
            return;
        }
        if (!sender && result.BagCapacity == 0 && TryPrepareVisitDeposit(visit, lane, sender)) return;
        AdvanceVisitLine(visit, lane, sender);
    }

    private void AdvanceVisitLine(PreparedVisit visit, VisitParticipantPreparation lane, bool sender)
    {
        var final = lane.Stage == VisitPreparationStage.FinalInspect;
        lane.LineIndex++;
        if (lane.LineIndex < visit.Lines.Count) { lane.Stage = final ? VisitPreparationStage.FinalInspect : VisitPreparationStage.Inspect; return; }
        if (sender && visit.Lines.All(line => line.SenderBagQuantity == 0))
        {
            if (!final && TryPrepareVisitDeposit(visit, lane, sender)) return;
            throw new InvalidOperationException("The selected provider has no carried planned items and no room to collect them. Free bag space, then resume.");
        }
        if (!sender && visit.Lines.All(line => line.ReceiverBagCapacity == 0))
        {
            var holdsRelayCargo = Run!.VisitItems.Values.Any(state => state.Journal != null
                && new TransferJobLedger(state.Journal).RelayCargo(visit.ReceiverId) > 0);
            if (holdsRelayCargo)
            {
                MarkVisitRelayForDelivery(visit.ReceiverId);
                visit.ReplanRequested = true;
                return;
            }
            throw new InvalidOperationException("The recipient has no room for the assigned items and no received job items available to deposit. Free inventory space, then resume.");
        }
        if (!sender && NeedsVisitReceivingSlots(visit) && TryPrepareVisitDeposit(visit, lane, sender)) return;
        lane.LineIndex = 0;
        lane.Stage = final ? VisitPreparationStage.Ready : VisitPreparationStage.Travel;
    }

    private static bool NeedsVisitReceivingSlots(PreparedVisit visit)
    {
        var free = visit.Lines.Select(line => line.ReceiverFreeBagSlots).Where(value => value.HasValue)
            .Select(value => value!.Value).DefaultIfEmpty(uint.MaxValue).Min();
        if (free == uint.MaxValue) return false; // Older observations cannot prove physical shared-slot requirements.
        ulong slots = 0;
        foreach (var line in visit.Lines.Take(5))
        {
            if (line.StackSize == 0) continue;
            var desired = Math.Min(line.MaximumQuantity, line.StackSize);
            var emptyCapacity = (ulong)free * line.StackSize;
            var merge = line.ReceiverBagCapacity > emptyCapacity ? line.ReceiverBagCapacity - emptyCapacity : 0;
            if (desired > merge) slots++;
        }
        return slots > free;
    }

    /// <summary>Absolute capacity hints derived from this prepared visit, safe to repeat after commit replay.</summary>
    private void OnVisitBatchCommitted(PreparedVisit visit, List<VisitBatchLine> committed)
    {
        var run = Run!;
        var freeValues = visit.Lines.Select(line => line.ReceiverFreeBagSlots).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        uint? freeBefore = freeValues.Length == visit.Lines.Count ? freeValues.Min() : null;
        ulong occupiedSlots = 0;
        if (freeBefore.HasValue)
        {
            foreach (var line in visit.Lines)
            {
                var quantity = committed.Where(batch => batch.ItemIndex == line.ItemIndex)
                    .Aggregate(0UL, (sum, batch) => checked(sum + batch.Quantity));
                var emptyCapacity = (ulong)freeBefore.Value * line.StackSize;
                var merge = line.ReceiverBagCapacity > emptyCapacity ? line.ReceiverBagCapacity - emptyCapacity : 0;
                var needsNew = quantity > merge ? quantity - merge : 0;
                if (line.StackSize == 0) throw new InvalidDataException("The committed item omitted its stack size.");
                occupiedSlots = checked(occupiedSlots + (needsNew + line.StackSize - 1) / line.StackSize);
            }
            if (occupiedSlots > freeBefore.Value) throw new InvalidDataException("The committed mixed trade exceeded the prepared shared inventory slots.");
        }
        // A different item can consume/free the same physical empty slots. Invalidate
        // unrelated historical hints rather than treating each item's capacity as additive.
        foreach (var state in run.VisitItems.Values)
        {
            state.RelayCapacity.Remove(visit.ReceiverId);
            state.RelayCapacity.Remove(visit.SenderId);
        }
        foreach (var line in visit.Lines)
        {
            var state = run.VisitItems[line.ItemIndex];
            var quantity = committed.Where(batch => batch.ItemIndex == line.ItemIndex)
                .Aggregate(0UL, (sum, batch) => checked(sum + batch.Quantity));
            if (state.Journal!.Plan.Allocations.Any(allocation => allocation.Legs.Count == 2 && allocation.Legs[0].ReceiverId == visit.ReceiverId))
            {
                if (freeBefore.HasValue)
                {
                    var emptyCapacity = (ulong)freeBefore.Value * line.StackSize;
                    var mergeBefore = line.ReceiverBagCapacity > emptyCapacity ? line.ReceiverBagCapacity - emptyCapacity : 0;
                    var needsNew = quantity > mergeBefore ? quantity - mergeBefore : 0;
                    var ownSlots = (needsNew + line.StackSize - 1) / line.StackSize;
                    var mergeAfter = quantity <= mergeBefore ? mergeBefore - quantity : ownSlots * line.StackSize - needsNew;
                    state.RelayCapacity[visit.ReceiverId] = (uint)Math.Min(uint.MaxValue,
                        mergeAfter + (freeBefore.Value - occupiedSlots) * line.StackSize);
                }
            }
            if (state.Journal.Plan.Allocations.Any(allocation => allocation.Legs.Count == 2 && allocation.Legs[0].ReceiverId == visit.SenderId))
                state.RelayCapacity[visit.SenderId] = (uint)Math.Min(uint.MaxValue, (ulong)line.SenderBagCapacity + quantity);
        }
    }

    private bool TryPrepareVisitDeposit(PreparedVisit visit, VisitParticipantPreparation lane, bool sender)
    {
        var characterId = sender ? visit.SenderId : visit.ReceiverId;
        foreach (var (index, state) in Run!.VisitItems.OrderBy(pair => pair.Key))
        {
            if (state.Journal == null || lane.AttemptedDeposits.Contains(index)) continue;
            // Only final deliveries belong in retainers. Incoming relay cargo remains in
            // carried bags until its attributed outbound leg is verified.
            var delivered = new TransferJobLedger(state.Journal).DeliveredTo(characterId);
            var stored = state.Deposited.GetValueOrDefault(characterId);
            if (delivered <= stored) continue;
            lane.AttemptedDeposits.Add(index);
            lane.StorageItemIndex = index;
            lane.StorageQuantity = (uint)Math.Min(uint.MaxValue, delivered - stored);
            lane.Stage = VisitPreparationStage.Deposit;
            return true;
        }
        return false;
    }

    private void MarkVisitRelayForDelivery(ulong relayId)
    {
        foreach (var state in Run!.VisitItems.Values.Where(state => state.Journal?.Plan.Allocations.Any(allocation =>
                     allocation.Legs.Count == 2 && allocation.Legs[0].ReceiverId == relayId) == true))
            state.RelayFlushCharacterId = relayId;
    }

    private static GameOperationKind VisitOperationKind(VisitPreparationStage stage) => stage switch
    {
        VisitPreparationStage.Login => GameOperationKind.Login,
        VisitPreparationStage.Inspect or VisitPreparationStage.FinalInspect => GameOperationKind.Inspect,
        VisitPreparationStage.Withdraw => GameOperationKind.Withdraw,
        VisitPreparationStage.Deposit => GameOperationKind.Deposit,
        VisitPreparationStage.Travel => GameOperationKind.TravelMeeting,
        _ => throw new InvalidDataException("No command can be issued for this visit stage."),
    };

    private static int VisitCommandItem(PreparedVisit visit, VisitParticipantPreparation lane) =>
        lane.StorageItemIndex ?? visit.Lines[Math.Min(lane.LineIndex, visit.Lines.Count - 1)].ItemIndex;

    private static string VisitStageLabel(VisitParticipantPreparation lane) => lane.Stage switch
    {
        VisitPreparationStage.Login => "logging in",
        VisitPreparationStage.Withdraw => "collecting assigned items from retainers",
        VisitPreparationStage.Deposit => "storing delivered items",
        VisitPreparationStage.Travel => "travelling to meet the other character",
        VisitPreparationStage.Ready => "ready with assigned items",
        _ => "checking this visit's carried items",
    };

    private void ResumeCharacterVisit()
    {
        var run = Run!;
        var visit = run.Visit ?? throw new InvalidDataException("The interrupted character visit is missing.");
        visit.Recovering = true;
        foreach (var pending in run.Pending)
        {
            RememberAppliedStorageQuantity(pending);
            if (pending.Response is { Success: true } or { AutoReconciled: true } or { NeverStarted: true }
                || StorageReconciliation.IsReviewed(pending.Response)) continue;
            if (StorageReconciliation.IsStorage(pending.Command)) pending.StorageRecoveryRequested = true;
            pending.Response = null;
        }
        preparationRecoveryDeadline = Environment.TickCount64 + 30_000;
        resendAt = 0;
        run.Paused = false;
        run.Status = "Recovering both characters' original visit operations before preparing any more items…";
        SaveRun();
    }

    private void RecoverCharacterVisit(PreparedVisit visit)
    {
        foreach (var pending in Run!.Pending.ToArray())
        {
            if (pending.Response == null || !ResolvePreparationStorage(pending)) continue;
            if (StorageReconciliation.IsStorage(pending.Command) && !pending.Response!.NeverStarted && pending.Response.Observation == null)
                throw new InvalidOperationException("An interrupted item pickup or deposit has no matching inventory observation. Keep the original character logged in and resume tallying.");
            Run.Pending.Remove(pending);
            SaveRun();
        }
        if (Run.Pending.Count != 0)
        {
            if (Environment.TickCount64 >= preparationRecoveryDeadline)
                throw new TimeoutException("Waiting for both PCs' previous item movements to settle. Keep their original characters logged in, then resume.");
            return;
        }
        Run.Visit = null;
        Run.MeetingPinned = false;
        Run.Step = CoordinatorStep.SelectLeg;
        SaveRun();
    }
}
