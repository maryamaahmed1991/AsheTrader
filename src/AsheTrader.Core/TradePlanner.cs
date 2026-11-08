namespace AsheTrader.Core;

public static class TradePlanner
{
    public const int MaxItemSlots = 5;

    /// <summary>
    /// Plans from a fresh inventory snapshot without changing the trade list or inventory.
    /// Shortages describe the entire remaining list, independently of the five-slot batch limit.
    /// The caller decides whether to stop or explicitly permit a partial run when shortages exist.
    /// </summary>
    public static TradeBatch Plan(IEnumerable<TradeLine> remaining, IEnumerable<InventoryStack> inventory)
    {
        var requests = ValidateLines(remaining);
        ArgumentNullException.ThrowIfNull(inventory);
        var stacks = inventory.ToArray();
        if (stacks.Any(stack => stack is null))
            throw new ArgumentException("Inventory must not contain null entries.", nameof(inventory));
        if (stacks.GroupBy(stack => (stack.Container, stack.Slot)).Any(group => group.Count() > 1))
            throw new ArgumentException("Inventory contains the same physical slot more than once.", nameof(inventory));

        var usable = stacks.Where(stack => stack.Tradable && stack.ItemId != 0 && stack.Quantity > 0).ToArray();
        if (usable.Any(stack => stack.Slot < 0 || stack.Container < 0 || stack.StackSize == 0 || stack.Quantity > stack.StackSize))
            throw new ArgumentException("Inventory contains an invalid slot, quantity, or stack size.", nameof(inventory));

        var entries = new List<TradeAllocation>(MaxItemSlots);
        var shortages = new List<TradeShortage>();
        foreach (var (key, requested) in requests)
        {
            var matches = usable.Where(stack => stack.Key == key).ToArray();
            var available = matches.Aggregate(0UL, (total, stack) => total + stack.Quantity);
            if (available < requested)
                shortages.Add(new TradeShortage(key, requested, available));

            var unallocated = requested;
            foreach (var stack in matches)
            {
                if (unallocated == 0 || entries.Count == MaxItemSlots)
                    break;

                var quantity = Math.Min(stack.Quantity, unallocated);
                entries.Add(new TradeAllocation(stack.Container, stack.Slot, stack.ItemId, stack.HighQuality, quantity));
                unallocated -= quantity;
            }
        }

        return new TradeBatch(entries, shortages);
    }

    internal static Dictionary<ItemKey, uint> ValidateLines(IEnumerable<TradeLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new Dictionary<ItemKey, uint>();
        foreach (var line in lines)
        {
            if (line is null || line.ItemId == 0 || line.Quantity == 0)
                throw new ArgumentException("Each trade line must have a nonzero item ID and positive quantity.", nameof(lines));

            var key = new ItemKey(line.ItemId, line.HighQuality);
            if (!result.TryAdd(key, line.Quantity))
                throw new ArgumentException($"Item {key} occurs more than once. Combine duplicate lines first.", nameof(lines));
        }
        return result;
    }

    public static IReadOnlyDictionary<ItemKey, ulong> Count(IEnumerable<InventoryStack> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var result = new Dictionary<ItemKey, ulong>();
        foreach (var stack in inventory)
        {
            if (stack.ItemId == 0 || stack.Quantity == 0)
                continue;
            result[stack.Key] = checked(result.GetValueOrDefault(stack.Key) + stack.Quantity);
        }
        return result;
    }
}
