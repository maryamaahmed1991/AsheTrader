using AsheTrader.Core.Jobs;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    /// <summary>Resolve only funded same-account routes, before creating an immutable journal.</summary>
    private bool PrepareAutomaticRelays()
    {
        var run = Run ?? throw new InvalidOperationException("No transfer job is active.");
        if (run.Journal != null) throw new InvalidOperationException("An issued transfer plan cannot change its relay routes.");
        var needed = JobPlanner.RequiredRelaySourcePeers(run.ActiveDefinition, run.Snapshots.Values);
        if (needed.Count == 0) return true;
        var funding = JobPlanner.PreviewFunding(run.ActiveDefinition, run.Snapshots.Values);
        var preferredWorld = PreferredMeetingWorld(funding);
        var pairedPeers = new[] { Settings.PeerId, RemoteRoster?.PeerId ?? Guid.Empty };
        foreach (var sourcePeer in needed)
        {
            var relayPeer = pairedPeers.SingleOrDefault(peerId => peerId != sourcePeer && peerId != Guid.Empty);
            if (relayPeer == Guid.Empty)
                throw new InvalidOperationException("Connect the other account's PC before choosing an automatic relay.");
            if (run.AutomaticRelays.TryGetValue(relayPeer, out var chosen)
                && Character(chosen).PeerId == relayPeer
                && run.Snapshots.TryGetValue(chosen, out var verified)
                && verified.Verification == InventoryVerification.Verified && verified.BagCapacity > 0)
                continue;

            // Resume may have interrupted the harmless login/inspection. Keep the chosen identity
            // and inspect again; no relay is accepted merely because its cached count was positive.
            if (run.RelayCandidatePeerId != relayPeer || !run.RelayCandidateId.HasValue
                || run.RejectedRelayCharacters.Contains(run.RelayCandidateId.Value))
            {
                var requiredQuantity = funding.Allocations
                    .Where(allocation => run.Snapshots[allocation.SourceCharacterId].PeerId == sourcePeer
                        && run.Snapshots[allocation.RecipientCharacterId].PeerId == sourcePeer)
                    .Aggregate(0UL, (sum, allocation) => checked(sum + allocation.Quantity));
                var candidates = AutomaticRelaySelector.Rank(relayPeer, AllCharacters(), run.RejectedRelayCharacters,
                    preferredWorld, requiredQuantity, PlanningItemStackSize());
                var next = candidates.FirstOrDefault();
                if (next == null)
                {
                    var hadKnownCandidates = AutomaticRelaySelector.Rank(relayPeer, AllCharacters()).Count > 0;
                    // A later explicit resume must be able to retry alts after the user makes space.
                    run.RejectedRelayCharacters.Clear();
                    SaveRun();
                    throw new InvalidOperationException(hadKnownCandidates
                        ? "No automatic relay on the other account has room for this item. Free bag space on an alt, refresh AutoRetainer characters, then resume."
                        : "No automatic relay has an AutoRetainer inventory-slot count on the other account. Log into an eligible alt with AutoRetainer enabled, refresh characters on both PCs, then resume.");
                }
                run.RelayCandidateId = next.ContentId;
                run.RelayCandidatePeerId = relayPeer;
            }
            var candidate = Character(run.RelayCandidateId.Value).Character;
            run.Step = CoordinatorStep.RelayLogin;
            run.Status = $"Preparing selected relay {candidate.Name}@{candidate.World}: ranked by estimated loads and world travel; checking its carried capacity…";
            SaveRun();
            return false;
        }
        return true;
    }

    /// <summary>Checks only the selected funded relay; unexpected fullness pauses instead of touring other alts.</summary>
    private bool AdvanceAutomaticRelayPreparation()
    {
        var run = Run!;
        if (run.Step is not (CoordinatorStep.RelayLogin or CoordinatorStep.RelayInspect)) return false;
        var id = run.RelayCandidateId ?? throw new InvalidDataException("The automatic relay candidate is missing.");
        var relayPeer = run.RelayCandidatePeerId ?? throw new InvalidDataException("The automatic relay account is missing.");
        if (Character(id).PeerId != relayPeer) throw new InvalidDataException("The automatic relay belongs to a different PC.");
        if (run.Step == CoordinatorStep.RelayLogin)
            Perform(id, GameOperationKind.Login, CoordinatorStep.RelayInspect);
        else
            Perform(id, GameOperationKind.Inspect, CoordinatorStep.Plan, result =>
            {
                if (result.StackSize == 0) throw new InvalidDataException("The relay inspection returned an invalid item stack size.");
                if (result.BagCapacity == 0)
                {
                    run.AutomaticRelays.Remove(relayPeer);
                    var identity = Character(id).Character;
                    throw new InvalidOperationException($"The selected automatic relay {identity.Name}@{identity.World} has no room for this item. Free its bag space or refresh AutoRetainer's slot counts, then Resume. Other alts will not be logged in just to test their capacity.");
                }
                else
                {
                    if (!run.Snapshots.TryGetValue(id, out var snapshot))
                    {
                        var identity = Character(id).Character;
                        snapshot = new() { CharacterId = id, PeerId = relayPeer, Name = identity.Name, World = identity.World };
                        run.Snapshots[id] = snapshot;
                    }
                    snapshot.BagQuantity = result.BagQuantity;
                    snapshot.BagCapacity = result.BagCapacity;
                    snapshot.Verification = InventoryVerification.Verified;
                    snapshot.ObservedAt = result.ObservedAt;
                    // Relay-only characters carry cargo; their retainer inventory is neither
                    // claimed as verified nor included in source budgets.
                    run.AutomaticRelays[relayPeer] = id;
                }
                run.RelayCandidateId = null;
                run.RelayCandidatePeerId = null;
            });
        return true;
    }

    private TransferJobDefinition DefinitionWithAutomaticRelays()
    {
        var definition = Clone(Run!.ActiveDefinition);
        definition.RelayCharacters = new(Run.AutomaticRelays);
        return definition;
    }
}
