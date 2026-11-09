namespace AsheTrader.Core.Jobs;

/// <summary>Ranks cached AutoRetainer hints. A chosen character still requires a live capacity check.</summary>
public static class AutomaticRelaySelector
{
    public static IReadOnlyList<CharacterProfile> Rank(Guid relayPeerId,
        IEnumerable<(Guid PeerId, CharacterProfile Character)> roster, IEnumerable<ulong>? excluded = null,
        string? preferredWorld = null, ulong requiredQuantity = 0, uint stackSize = 1)
    {
        if (relayPeerId == Guid.Empty) throw new ArgumentException("A relay must belong to the other paired PC.", nameof(relayPeerId));
        ArgumentNullException.ThrowIfNull(roster);
        if (stackSize == 0) throw new ArgumentOutOfRangeException(nameof(stackSize));
        var rejected = excluded?.ToHashSet() ?? [];
        return roster.Where(entry => entry.PeerId == relayPeerId && entry.Character is not null
                && entry.Character.ContentId != 0 && entry.Character.InventoryFreeSlots.HasValue
                && !rejected.Contains(entry.Character.ContentId))
            .Select(entry => entry.Character)
            .DistinctBy(character => character.ContentId)
            .OrderBy(character => EstimatedLoads(character.InventoryFreeSlots!.Value, requiredQuantity, stackSize))
            .ThenBy(character => string.IsNullOrWhiteSpace(preferredWorld)
                || string.Equals(character.World, preferredWorld, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(character => character.InventoryFreeSlots!.Value)
            .ThenBy(character => character.ContentId)
            .ToArray();
    }

    /// <summary>Empty slots are a cached lower-bound capacity hint; actual merge capacity is checked on the chosen relay.</summary>
    public static ulong EstimatedLoads(uint freeSlots, ulong requiredQuantity, uint stackSize)
    {
        if (stackSize == 0) throw new ArgumentOutOfRangeException(nameof(stackSize));
        if (requiredQuantity == 0) return 0;
        var capacity = (ulong)freeSlots * stackSize;
        return capacity == 0 ? ulong.MaxValue : requiredQuantity / capacity + (requiredQuantity % capacity == 0 ? 0UL : 1UL);
    }
}
