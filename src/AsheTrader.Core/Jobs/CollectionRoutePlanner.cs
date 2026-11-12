namespace AsheTrader.Core.Jobs;

public sealed record RouteWorld(uint WorldId, string Name, uint DataCenterId, string DataCenterName, int Region);
public sealed record CollectionRouteStop(Guid AllocationId, uint WorldId, string WorldName, uint DataCenterId, string DataCenterName);

/// <summary>
/// Orders funded pickups on provider home worlds, grouping each collector's work by DC and world.
/// Recipient priority and relay collection phases take precedence over travel estimates.
/// </summary>
public static class CollectionRoutePlanner
{
    public static List<CollectionRouteStop> Build(TransferPlan plan, IEnumerable<JobCharacterSnapshot> snapshots,
        IEnumerable<RouteWorld> worlds, IReadOnlyDictionary<ulong, uint> knownWorlds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(worlds);
        ArgumentNullException.ThrowIfNull(knownWorlds);
        var funded = plan.Allocations.Where(allocation => allocation.Quantity > 0).ToArray();
        if (funded.Length == 0) return [];
        if (funded.Any(allocation => allocation.Id == Guid.Empty) || funded.Select(allocation => allocation.Id).Distinct().Count() != funded.Length)
            throw new InvalidOperationException("Funded collection routes require distinct allocation IDs.");

        var byId = snapshots.ToLookup(character => character.CharacterId);
        var worldOptions = worlds.ToArray();
        var byWorldName = worldOptions.ToLookup(world => world.Name, StringComparer.OrdinalIgnoreCase);
        var byWorldId = worldOptions.ToLookup(world => world.WorldId);
        var homes = new Dictionary<ulong, RouteWorld>();
        RouteWorld Home(ulong characterId)
        {
            if (homes.TryGetValue(characterId, out var known)) return known;
            var characters = byId[characterId].ToArray();
            if (characterId == 0 || characters.Length != 1)
                throw new InvalidOperationException($"Funded character {characterId} has missing or ambiguous world information.");
            var character = characters[0];
            var matches = byWorldName[character.World].ToArray();
            if (string.IsNullOrWhiteSpace(character.World) || matches.Length != 1 || !Valid(matches[0])
                || byWorldId[matches[0].WorldId].Count() != 1)
                throw new InvalidOperationException($"The home world '{character.World}' for {Display(character)} is unavailable or has incomplete data-center information.");
            homes.Add(characterId, matches[0]);
            return matches[0];
        }

        void CheckVisit(ulong visitorId, RouteWorld destination)
        {
            var home = Home(visitorId);
            // Matches Lifestream's visitable-world region boundary. Runtime restrictions and
            // congestion remain authoritative when the worker actually requests travel.
            if (home.DataCenterId == destination.DataCenterId || home.Region == destination.Region || destination.Region == 4) return;
            throw new InvalidOperationException($"Character {visitorId} on {home.Name} cannot collect on {destination.Name}: the worlds are in unsupported regions.");
        }

        var recipients = plan.Definition.Recipients.Select(rule => rule.CharacterId).ToArray();
        if (recipients.Distinct().Count() != recipients.Length || funded.Any(allocation => !recipients.Contains(allocation.RecipientCharacterId)))
            throw new InvalidOperationException("A funded collection route has no unique recipient priority entry.");

        foreach (var allocation in funded)
        {
            var sourceWorld = Home(allocation.SourceCharacterId);
            _ = Home(allocation.RecipientCharacterId);
            if (allocation.Legs.Count is < 1 or > 2 || allocation.Legs[0].SenderId != allocation.SourceCharacterId
                || allocation.Legs[^1].ReceiverId != allocation.RecipientCharacterId
                || allocation.Legs.Count == 2 && allocation.Legs[0].ReceiverId != allocation.Legs[1].SenderId)
                throw new InvalidOperationException("A funded collection route has inconsistent transfer legs.");
            CheckVisit(allocation.Legs[0].ReceiverId, sourceWorld);
            if (allocation.Legs.Count == 2)
            {
                // The recipient stays home for repeated retainer deposits. Once its load
                // is collected, the relay must be able to visit that recipient's home.
                CheckVisit(allocation.Legs[0].ReceiverId, Home(allocation.RecipientCharacterId));
            }
        }

        var result = new List<CollectionRouteStop>(funded.Length);
        foreach (var recipientId in recipients)
        {
            // Stable ordering retains the existing relay-group order, ahead of direct work.
            var collectorGroups = funded.Where(allocation => allocation.RecipientCharacterId == recipientId)
                .OrderBy(allocation => allocation.Legs.Count == 2 ? 0 : 1)
                .GroupBy(allocation => allocation.Legs[0].ReceiverId);
            foreach (var collectorGroup in collectorGroups)
            {
                var startWorld = Home(collectorGroup.Key);
                if (knownWorlds.TryGetValue(collectorGroup.Key, out var currentId)
                    && byWorldId[currentId].Count() == 1 && Valid(byWorldId[currentId].Single()))
                    startWorld = byWorldId[currentId].Single();

                var dataCenters = collectorGroup.GroupBy(allocation => Home(allocation.SourceCharacterId).DataCenterId)
                    .OrderBy(group => group.Key == startWorld.DataCenterId ? 0 : 1)
                    .ThenByDescending(group => group.Count()).ThenBy(group => group.Key);
                foreach (var dataCenter in dataCenters)
                {
                    var worldGroups = dataCenter.GroupBy(allocation => Home(allocation.SourceCharacterId).WorldId)
                        .OrderBy(group => group.Key == startWorld.WorldId ? 0 : 1)
                        .ThenByDescending(group => group.Count()).ThenBy(group => group.Key);
                    foreach (var worldGroup in worldGroups)
                        foreach (var allocation in worldGroup)
                        {
                            var world = Home(allocation.SourceCharacterId);
                            result.Add(new(allocation.Id, world.WorldId, world.Name, world.DataCenterId, world.DataCenterName));
                        }
                }
            }
        }
        return result;
    }

    private static bool Valid(RouteWorld world) => world.WorldId != 0 && world.DataCenterId != 0 && world.Region > 0
        && !string.IsNullOrWhiteSpace(world.Name) && !string.IsNullOrWhiteSpace(world.DataCenterName);
    private static string Display(JobCharacterSnapshot character) => string.IsNullOrWhiteSpace(character.Name)
        ? $"character {character.CharacterId}" : character.Name;
}
