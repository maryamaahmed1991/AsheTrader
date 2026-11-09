namespace AsheTrader.Core.Jobs;

public static class JobPlanner
{
    /// <summary>Creates a fixed stock budget. Never re-plan from live inventory after a job has issued work.</summary>
    public static TransferPlan Plan(TransferJobDefinition definition, IEnumerable<JobCharacterSnapshot> snapshots)
        => BuildPlan(definition, snapshots, true);

    /// <summary>
    /// Determines which accounts actually need an opposite-account relay after applying stock,
    /// protected recipient totals and priority. It does not reserve stock or create executable routes.
    /// </summary>
    public static IReadOnlyList<Guid> RequiredRelaySourcePeers(TransferJobDefinition definition, IEnumerable<JobCharacterSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(snapshots);
        ValidateDefinition(definition);
        var all = snapshots.ToArray();
        var candidates = JobModelCopies.Definition(definition);
        candidates.RelayCharacters.Clear();
        var allocationPreview = BuildPlan(candidates, all, false);
        var byCharacter = all.ToDictionary(snapshot => snapshot.CharacterId);
        return allocationPreview.Allocations
            .Where(allocation => byCharacter[allocation.SourceCharacterId].PeerId == byCharacter[allocation.RecipientCharacterId].PeerId)
            .Select(allocation => byCharacter[allocation.SourceCharacterId].PeerId).Distinct().Order().ToArray();
    }

    private static TransferPlan BuildPlan(TransferJobDefinition definition, IEnumerable<JobCharacterSnapshot> snapshots, bool createRoutes)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(snapshots);
        ValidateDefinition(definition);
        var all = snapshots.ToArray();
        if (all.Any(snapshot => snapshot is null || snapshot.CharacterId == 0 || snapshot.PeerId == Guid.Empty))
            throw new ArgumentException("Character snapshots require a character ID and paired peer ID.", nameof(snapshots));
        if (all.Select(snapshot => snapshot.CharacterId).Distinct().Count() != all.Length)
            throw new ArgumentException("Character snapshots contain duplicate character IDs.", nameof(snapshots));
        var byCharacter = all.ToDictionary(snapshot => snapshot.CharacterId);
        var stockOwners = definition.Sources.Concat(definition.Recipients.Select(rule => rule.CharacterId)).ToHashSet();
        var required = stockOwners
            .Concat(definition.RelayCharacters.Values).Distinct().ToArray();
        foreach (var characterId in required)
        {
            if (!byCharacter.TryGetValue(characterId, out var snapshot))
                throw new InvalidOperationException($"Character {characterId} has not been discovered.");
            if (snapshot.Retainers is null || snapshot.Retainers.Any(retainer => retainer is null))
                throw new InvalidOperationException($"Character {characterId} has an invalid retainer collection.");
            if (stockOwners.Contains(characterId) && !snapshot.IsFullyVerified)
                throw new InvalidOperationException($"Character {characterId} and all of its retainers must be verified before planning.");
            if (!stockOwners.Contains(characterId) && snapshot.Verification != InventoryVerification.Verified)
                throw new InvalidOperationException($"Relay character {characterId}'s carried inventory must be verified before planning.");
            if (snapshot.Retainers.Any(retainer => retainer.RetainerId == 0)
                || snapshot.Retainers.Select(retainer => retainer.RetainerId).Distinct().Count() != snapshot.Retainers.Count)
                throw new InvalidOperationException($"Character {characterId} has an invalid retainer roster.");
        }
        if (required.Select(id => byCharacter[id].PeerId).Distinct().Count() > 2)
            throw new InvalidOperationException("A transfer job supports exactly two paired account PCs.");
        if (required.SelectMany(id => byCharacter[id].Retainers.Select(retainer => retainer.RetainerId))
            .GroupBy(id => id).Any(group => group.Count() > 1))
            throw new InvalidOperationException("A retainer was assigned to more than one character; verify ownership before planning.");
        foreach (var (peerId, characterId) in definition.RelayCharacters)
            if (byCharacter[characterId].PeerId != peerId)
                throw new InvalidOperationException($"Relay character {characterId} does not belong to peer {peerId}.");

        var plan = new TransferPlan { Definition = JobModelCopies.Definition(definition) };
        var demand = new Dictionary<ulong, ulong?>();
        foreach (var recipient in definition.Recipients)
        {
            var stock = byCharacter[recipient.CharacterId].TotalQuantity;
            if (definition.QuantityMode == JobQuantityMode.RestockToTotal)
            {
                var target = recipient.Quantity!.Value;
                demand[recipient.CharacterId] = stock >= target ? 0 : target - stock;
                plan.ProtectedStock[recipient.CharacterId] = Math.Min(stock, target);
            }
            else
            {
                demand[recipient.CharacterId] = recipient.Quantity;
                // Additional-delivery jobs protect recipients' entire pre-job stock.
                plan.ProtectedStock[recipient.CharacterId] = stock;
            }
        }
        foreach (var sourceId in definition.Sources)
        {
            var stock = byCharacter[sourceId].TotalQuantity;
            plan.SourceBudgets[sourceId] = stock - plan.ProtectedStock.GetValueOrDefault(sourceId);
        }

        var available = new Dictionary<ulong, ulong>(plan.SourceBudgets);
        foreach (var recipient in definition.Recipients)
        {
            var recipientSnapshot = byCharacter[recipient.CharacterId];
            var requested = demand[recipient.CharacterId];
            var left = requested ?? ulong.MaxValue;
            var allocated = 0UL;
            foreach (var sourceId in definition.Sources)
            {
                if (left == 0) break;
                if (sourceId == recipient.CharacterId || available[sourceId] == 0) continue;
                var amount = Math.Min(left, available[sourceId]);
                var source = byCharacter[sourceId];
                var legs = createRoutes ? CreateRoute(source, recipientSnapshot, definition.RelayCharacters, byCharacter) : [];
                plan.Allocations.Add(new TransferAllocation
                {
                    SourceCharacterId = sourceId,
                    RecipientCharacterId = recipient.CharacterId,
                    Quantity = amount,
                    Legs = legs,
                });
                available[sourceId] -= amount;
                left -= amount;
                allocated = checked(allocated + amount);
            }
            if (requested.HasValue && allocated < requested.Value)
                plan.UnmetTargets.Add(new UnmetTarget { CharacterId = recipient.CharacterId, Requested = requested.Value, Allocated = allocated });
        }
        return plan;
    }

    private static List<TransferLeg> CreateRoute(JobCharacterSnapshot source, JobCharacterSnapshot recipient,
        Dictionary<Guid, ulong> relays, IReadOnlyDictionary<ulong, JobCharacterSnapshot> characters)
    {
        if (source.PeerId != recipient.PeerId) return [Leg(source, recipient)];
        var candidates = relays.Where(pair => pair.Key != source.PeerId).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException($"Transfer from {source.CharacterId} to {recipient.CharacterId} needs a verified relay character on the other account.");
        var relay = characters[candidates[0].Value];
        return [Leg(source, relay), Leg(relay, recipient)];
    }

    private static TransferLeg Leg(JobCharacterSnapshot sender, JobCharacterSnapshot receiver) => new()
    {
        SenderId = sender.CharacterId, SenderPeerId = sender.PeerId,
        ReceiverId = receiver.CharacterId, ReceiverPeerId = receiver.PeerId,
    };

    public static void ValidateDefinition(TransferJobDefinition definition)
    {
        if (definition.Id == Guid.Empty || definition.ItemId == 0 || !Enum.IsDefined(definition.QuantityMode))
            throw new ArgumentException("A job requires a stable ID, item ID, and valid quantity mode.", nameof(definition));
        if (definition.Sources is null || definition.Sources.Count == 0 || definition.Sources.Any(id => id == 0)
            || definition.Sources.Distinct().Count() != definition.Sources.Count)
            throw new ArgumentException("Select distinct nonzero source character IDs.", nameof(definition));
        if (definition.Recipients is null || definition.Recipients.Count == 0
            || definition.Recipients.Any(rule => rule is null || rule.CharacterId == 0 || rule.Quantity == 0)
            || definition.Recipients.Select(rule => rule.CharacterId).Distinct().Count() != definition.Recipients.Count)
            throw new ArgumentException("Select distinct recipients with positive quantities or a final remainder.", nameof(definition));
        if (definition.RelayCharacters is null || definition.RelayCharacters.Any(pair => pair.Key == Guid.Empty || pair.Value == 0)
            || definition.RelayCharacters.Values.Distinct().Count() != definition.RelayCharacters.Count)
            throw new ArgumentException("Relay entries require distinct characters and valid peer IDs.", nameof(definition));
        for (var index = 0; index < definition.Recipients.Count; index++)
            if (definition.Recipients[index].Quantity is null &&
                (definition.QuantityMode != JobQuantityMode.MoveAll || index != definition.Recipients.Count - 1))
                throw new ArgumentException("An unlimited remainder is allowed only for the final recipient of a MoveAll job.", nameof(definition));
    }
}

internal static class JobModelCopies
{
    public static TransferJobDefinition Definition(TransferJobDefinition definition) => new()
    {
        Id = definition.Id, Name = definition.Name, ItemId = definition.ItemId, HighQuality = definition.HighQuality,
        QuantityMode = definition.QuantityMode, Sources = [.. definition.Sources],
        Recipients = definition.Recipients.Select(rule => new RecipientRule { CharacterId = rule.CharacterId, Quantity = rule.Quantity }).ToList(),
        RelayCharacters = new(definition.RelayCharacters),
    };

    public static TransferObservation Observation(TransferObservation observation) => new()
    {
        PeerId = observation.PeerId, CharacterId = observation.CharacterId, Quantity = observation.Quantity,
        Verified = observation.Verified, ObservedAt = observation.ObservedAt,
    };
}
