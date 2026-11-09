namespace AsheTrader.Core.Jobs;

/// <summary>Ranks cached AutoRetainer hints. A chosen character still requires a live capacity check.</summary>
public static class AutomaticRelaySelector
{
    public static IReadOnlyList<CharacterProfile> Rank(Guid relayPeerId,
        IEnumerable<(Guid PeerId, CharacterProfile Character)> roster, IEnumerable<ulong>? excluded = null)
    {
        if (relayPeerId == Guid.Empty) throw new ArgumentException("A relay must belong to the other paired PC.", nameof(relayPeerId));
        ArgumentNullException.ThrowIfNull(roster);
        var rejected = excluded?.ToHashSet() ?? [];
        return roster.Where(entry => entry.PeerId == relayPeerId && entry.Character is not null
                && entry.Character.ContentId != 0 && entry.Character.InventoryFreeSlots.HasValue
                && !rejected.Contains(entry.Character.ContentId))
            .Select(entry => entry.Character)
            .DistinctBy(character => character.ContentId)
            .OrderByDescending(character => character.InventoryFreeSlots!.Value)
            .ThenBy(character => character.ContentId)
            .ToArray();
    }
}
