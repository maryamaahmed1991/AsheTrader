using AsheTrader.Core.Jobs;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    // Relay selection still prioritizes usable capacity. Locality now refers to funded
    // providers, never the world on which the meeting coordinates were captured.
    private string PreferredMeetingWorld(TransferPlan funding) => funding.Allocations
        .Where(allocation => allocation.Quantity > 0)
        .Select(allocation => Run!.Snapshots[allocation.SourceCharacterId])
        .DistinctBy(character => character.CharacterId)
        .GroupBy(character => character.World, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Key).FirstOrDefault() ?? "";

    private void ChooseMeetingWorld(TransferPlan plan)
    {
        var run = Run!;
        if (run.CollectionRoute != null) return;
        run.CollectionRoute = CollectionRoutePlanner.Build(plan, run.Snapshots.Values, RoutingWorlds(), run.ObservedWorlds);
        run.MeetingPinned = false;
        RecordDiagnostic($"Collection itinerary prepared; providerStops={run.CollectionRoute.Count}; grouped by collector, recipient priority, DC and world; captured world is not a destination");
    }

    private void EnsureCollectionRoute()
    {
        // Legacy journals adopt the new itinerary at a scheduling boundary only. Issued
        // travel commands and pending trade outcomes keep their saved destinations.
        if (Run!.CollectionRoute == null)
        {
            if (Run.Pending.Count != 0 || Run.TradeOperationId.HasValue)
                throw new InvalidOperationException("Reconcile pending work before preparing the collection itinerary.");
            ChooseMeetingWorld(Run.Journal!.Plan);
        }
    }

    private IEnumerable<TransferAllocation> OrderCollectionAllocations(IEnumerable<TransferAllocation> allocations)
    {
        var order = Run!.CollectionRoute!.Select((stop, index) => (stop.AllocationId, index))
            .ToDictionary(entry => entry.AllocationId, entry => entry.index);
        return allocations.OrderBy(allocation => order.TryGetValue(allocation.Id, out var index) ? index
            : throw new InvalidDataException("A funded allocation is missing from the saved collection itinerary."));
    }

    private void PinLegMeeting(TransferAllocation allocation, int legIndex)
    {
        var run = Run!;
        if (run.MeetingPinned) return;
        if (run.Pending.Count != 0 || run.TradeOperationId.HasValue)
            throw new InvalidOperationException("The meeting world cannot change while a command or trade is pending.");
        EnsureCollectionRoute();
        var worlds = RoutingWorlds();
        uint worldId;
        if (legIndex == 0)
        {
            // Providers remain on their home worlds so repeated retainer refills never
            // require a DC round trip. The recipient/relay comes to the provider.
            worldId = run.CollectionRoute!.Single(stop => stop.AllocationId == allocation.Id).WorldId;
            run.MeetingReason = "Recipient or relay visits the provider's home world";
        }
        else
        {
            worldId = worlds.Single(world => string.Equals(world.Name,
                run.Snapshots[allocation.RecipientCharacterId].World, StringComparison.OrdinalIgnoreCase)).WorldId;
            run.MeetingReason = "Relay delivers on the recipient's home world so repeated deposits stay local";
        }
        var world = worlds.SingleOrDefault(candidate => candidate.WorldId == worldId)
            ?? throw new InvalidOperationException("The selected collection world is unavailable.");
        // Validate the selected destination before either PC begins this travel leg.
        foreach (var participant in new[] { allocation.Legs[legIndex].SenderId, allocation.Legs[legIndex].ReceiverId })
        {
            var home = worlds.Single(candidate => string.Equals(candidate.Name, run.Snapshots[participant].World,
                StringComparison.OrdinalIgnoreCase));
            if (home.DataCenterId != world.DataCenterId && home.Region != world.Region && world.Region != 4)
                throw new InvalidOperationException($"{run.Snapshots[participant].Name} cannot visit {world.Name} under the supported DC travel rules.");
        }
        run.EffectiveMeeting = Clone(run.Job.Meeting);
        run.EffectiveMeeting.WorldId = world.WorldId;
        run.MeetingWorldName = world.Name;
        run.MeetingDataCenterName = world.DataCenterName;
        run.MeetingPinned = true;
        RecordDiagnostic($"Meeting pinned for allocation={allocation.Id}; leg={legIndex}; world={world.WorldId}/{world.Name}; DC={world.DataCenterName}; reason={run.MeetingReason}");
        SaveRun(); // Pin both PCs to one destination before the first travel command is issued.
    }
}
