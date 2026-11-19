using AsheTrader.Core.Jobs;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private void ApplyRecoveredCargo(PendingCommand pending)
    {
        var op = pending.Command.Operation;
        var cargo = pending.Response?.Observation ?? throw new InvalidDataException("Missing cargo evidence.");
        if (cargo.Id != op.Id || cargo.CharacterId != op.CharacterId || !cargo.SaddlebagsAvailable
            || (ulong)cargo.BagQuantity + cargo.SaddlebagQuantity != op.CargoExpectedTotal)
            throw new InvalidDataException("Recovered cargo does not match its saved character and inventory total.");
        Run!.VisitItems[pending.InventoryItemIndex!.Value].PackedCargo[op.CharacterId] =
            SaddlebagCargoPolicy.ReconcileCargo(op.PackedCargoBefore, op.SaddlebagBefore, cargo.SaddlebagQuantity);
    }
    private bool CanUseCargo(ulong id) => (Character(id).Character.UseSaddlebags || Run?.VisitItems.Values.Any(item => item.PackedCargo.GetValueOrDefault(id) > 0) == true)
        && (Character(id).PeerId == Settings.PeerId || RemoteRoster?.ProtocolVersion >= 3);

    private static ulong OwnedCargo(VisitItemState item, ulong id)
    {
        if (item.Journal == null) return 0;
        var ledger = new TransferJobLedger(item.Journal);
        var delivered = ledger.DeliveredTo(id);
        var deposited = item.Deposited.GetValueOrDefault(id);
        return checked((delivered > deposited ? delivered - deposited : 0) + ledger.RelayCargo(id));
    }

    private bool TryInspectPacking(PreparedVisit visit, VisitParticipantPreparation lane)
    {
        if (!CanUseCargo(visit.ReceiverId)) return false;
        var next = Run!.VisitItems.Values.OrderBy(item => item.ItemIndex).FirstOrDefault(item =>
            !lane.AttemptedPacking.Contains(item.ItemIndex) && OwnedCargo(item, visit.ReceiverId) > item.PackedCargo.GetValueOrDefault(visit.ReceiverId));
        if (next == null) return false;
        lane.StorageItemIndex = next.ItemIndex;
        lane.Stage = VisitPreparationStage.PackingInspect;
        return true;
    }

    private static bool ArrangeCargo(VisitParticipantPreparation lane, VisitItemState item, ulong id,
        GameOperationResult result, bool packing, uint allowance, CargoPurpose purpose)
    {
        if (!result.SaddlebagsAvailable) return false;
        var quantity = Math.Min(allowance, packing ? Math.Min(result.BagQuantity, result.SaddlebagCapacity)
            : Math.Min(result.SaddlebagQuantity, result.BagCapacity));
        if (quantity == 0) return false;
        lane.StorageItemIndex = item.ItemIndex;
        lane.StorageQuantity = packing ? checked(result.SaddlebagQuantity + quantity) : result.SaddlebagQuantity - quantity;
        lane.SaddlebagBefore = result.SaddlebagQuantity;
        lane.PackedCargoBefore = item.PackedCargo.GetValueOrDefault(id);
        lane.CargoExpectedTotal = checked(result.BagQuantity + result.SaddlebagQuantity);
        lane.CargoPurpose = purpose;
        lane.Stage = VisitPreparationStage.Saddlebag;
        return true;
    }

    private bool ApplyCargoVisitResult(PreparedVisit visit, VisitParticipantPreparation lane, bool sender, GameOperationResult result)
    {
        var id = sender ? visit.SenderId : visit.ReceiverId;
        if (lane.Stage == VisitPreparationStage.Saddlebag)
        {
            if (!result.SaddlebagsAvailable) throw new InvalidOperationException("Saddlebag movement returned no inventory evidence.");
            if ((ulong)result.BagQuantity + result.SaddlebagQuantity != lane.CargoExpectedTotal)
                throw new InvalidDataException("The completed saddlebag move changed the conserved item total.");
            var item = Run!.VisitItems[lane.StorageItemIndex!.Value];
            item.PackedCargo[id] = SaddlebagCargoPolicy.ReconcileCargo(lane.PackedCargoBefore, lane.SaddlebagBefore, result.SaddlebagQuantity);
            var changed = result.SaddlebagQuantity != lane.SaddlebagBefore;
            if (!changed || lane.CargoPurpose == CargoPurpose.Receiving) lane.AttemptedPacking.Add(item.ItemIndex);
            RecordDiagnostic($"Cargo settled; character={id}; item={item.Definition.ItemId}; packed={item.PackedCargo[id]}; purpose={lane.CargoPurpose}; premium={result.PremiumSaddlebagsAvailable}");
            if (lane.CargoPurpose == CargoPurpose.Depositing)
            {
                lane.Stage = VisitPreparationStage.DepositInspect;
                if (!changed)
                {
                    lane.StorageItemIndex = null;
                    if (!TryPrepareVisitDeposit(visit, lane, sender)) { lane.Stage = VisitPreparationStage.Inspect; lane.LineIndex = 0; }
                }
                return true;
            }
            lane.StorageItemIndex = null;
            lane.Stage = VisitPreparationStage.Inspect;
            if (lane.CargoPurpose == CargoPurpose.Receiving) TryInspectPacking(visit, lane);
            return true;
        }
        if (lane.Stage == VisitPreparationStage.PackingInspect)
        {
            var item = Run!.VisitItems[lane.StorageItemIndex!.Value];
            var owned = OwnedCargo(item, id);
            var packed = item.PackedCargo.GetValueOrDefault(id);
            if (result.FreeBagSlots < 5 && owned > packed && ArrangeCargo(lane, item, id, result, true,
                    (uint)Math.Min(uint.MaxValue, owned - packed), CargoPurpose.Receiving)) return true;
            lane.AttemptedPacking.Add(item.ItemIndex);
            lane.StorageItemIndex = null;
            lane.Stage = VisitPreparationStage.Inspect;
            TryInspectPacking(visit, lane);
            return true;
        }
        if (lane.Stage == VisitPreparationStage.DepositInspect)
        {
            var item = Run!.VisitItems[lane.StorageItemIndex!.Value];
            var delivered = new TransferJobLedger(item.Journal!).DeliveredTo(id);
            var deposited = item.Deposited.GetValueOrDefault(id);
            var remaining = delivered > deposited ? delivered - deposited : 0;
            var packed = item.PackedCargo.GetValueOrDefault(id);
            var inBags = remaining > packed ? remaining - packed : 0;
            if (inBags > 0 && result.BagQuantity > 0)
            {
                lane.StorageQuantity = (uint)Math.Min(result.BagQuantity, inBags);
                lane.Stage = VisitPreparationStage.Deposit;
                return true;
            }
            if (packed > 0 && !result.SaddlebagsAvailable)
                throw new InvalidOperationException("Delivered cargo remains in unavailable saddlebags. Restore saddlebag access and resume.");
            if (packed > 0 && ArrangeCargo(lane, item, id, result, false,
                (uint)Math.Min(remaining, packed), CargoPurpose.Depositing)) return true;
            lane.StorageItemIndex = null;
            if (!TryPrepareVisitDeposit(visit, lane, sender)) { lane.Stage = VisitPreparationStage.Inspect; lane.LineIndex = 0; }
            return true;
        }
        return false;
    }
}
