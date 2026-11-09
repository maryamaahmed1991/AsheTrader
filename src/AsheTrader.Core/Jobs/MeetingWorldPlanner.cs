namespace AsheTrader.Core.Jobs;

public sealed record MeetingWorldOption(uint WorldId, string Name);
public sealed record MeetingWorldChoice(uint WorldId, string Name, int WorldTravelers, int ConfiguredWorldTravelers, int ActiveCharacters);

/// <summary>Chooses a shared world by distinct funded participants leaving their recorded home worlds.</summary>
public static class MeetingWorldPlanner
{
    public static MeetingWorldChoice Choose(uint configuredWorldId, IEnumerable<MeetingWorldOption> candidates,
        TransferPlan plan, IEnumerable<JobCharacterSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshots);
        var worlds = candidates.Where(world => world.WorldId != 0 && !string.IsNullOrWhiteSpace(world.Name))
            .DistinctBy(world => world.WorldId).ToArray();
        var configured = worlds.SingleOrDefault(world => world.WorldId == configuredWorldId)
            ?? throw new ArgumentException("The configured meeting world must be a candidate.", nameof(candidates));
        var byId = snapshots.ToDictionary(character => character.CharacterId);
        var funded = plan.Allocations.Where(allocation => allocation.Quantity > 0)
            .SelectMany(allocation => new[] { allocation.SourceCharacterId, allocation.RecipientCharacterId }
                .Concat(allocation.Legs.SelectMany(leg => new[] { leg.SenderId, leg.ReceiverId })))
            .Distinct().Select(id => byId.TryGetValue(id, out var character) ? character
                : throw new InvalidOperationException($"Funded character {id} has no world information.")).ToArray();
        int CountTravelers(MeetingWorldOption world) => funded.Count(character =>
            !string.Equals(character.World, world.Name, StringComparison.OrdinalIgnoreCase));
        var chosen = worlds.OrderBy(CountTravelers)
            .ThenBy(world => world.WorldId == configured.WorldId ? 0 : 1)
            .ThenBy(world => world.WorldId).First();
        return new(chosen.WorldId, chosen.Name, CountTravelers(chosen), CountTravelers(configured), funded.Length);
    }
}
