namespace AsheTrader.Workflow.Native;

public sealed record CargoSlot(uint Container, ushort Slot, uint ItemId, bool HighQuality, uint Quantity, uint StackSize, bool Movable);
public sealed record CargoMove(CargoSlot Source, CargoSlot Destination, uint Quantity);

public static class SaddlebagCargoPolicy
{
    // A native slot move transfers a whole stack, or the part that fits an existing stack.
    // Never swap occupied slots or move more than the job's cargo allowance.
    public static CargoMove? NextMove(IEnumerable<CargoSlot> sources, IEnumerable<CargoSlot> destinations,
        uint itemId, bool highQuality, uint allowance)
    {
        var targets = destinations.OrderBy(slot => slot.ItemId == 0 ? 1 : 0).ToArray();
        foreach (var source in sources.Where(slot => slot.Movable && slot.ItemId == itemId
                     && slot.HighQuality == highQuality && slot.Quantity > 0 && slot.Quantity <= slot.StackSize))
        foreach (var target in targets)
        {
            if (target.ItemId != 0 && (!target.Movable || target.ItemId != itemId || target.HighQuality != highQuality)) continue;
            var space = target.ItemId == 0 ? source.StackSize : target.StackSize - Math.Min(target.StackSize, target.Quantity);
            var amount = Math.Min(source.Quantity, space);
            if (amount > 0 && amount <= allowance) return new(source, target, amount);
        }
        return null;
    }

    public static uint ReconcileCargo(uint cargoBefore, uint saddleBefore, uint saddleAfter)
    {
        var cargo = (long)cargoBefore + saddleAfter - saddleBefore;
        if (cargo < 0 || cargo > uint.MaxValue) throw new InvalidDataException("Saddlebag cargo does not match the saved movement boundary.");
        return (uint)cargo;
    }
}
