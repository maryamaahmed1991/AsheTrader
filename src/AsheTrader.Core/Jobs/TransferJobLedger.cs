namespace AsheTrader.Core.Jobs;

/// <summary>
/// Durable, idempotent accounting around non-atomic game operations. The caller persists Journal
/// before issuing an operation, after observations, and after committing. Depositing cargo changes
/// its storage location only: it never decrements this ledger's delivered or relay quantities.
/// </summary>
public sealed class TransferJobLedger
{
    public TransferJobLedger(TransferJobJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ValidateJournal(journal);
        Journal = journal;
    }

    public TransferJobJournal Journal { get; }
    public bool HasPendingOperation => Journal.Operations.Any(operation => IsPending(operation.State));
    public bool IsComplete => Journal.Plan.Allocations.All(allocation =>
        CommittedForLeg(allocation.Id, allocation.Legs.Count - 1) == allocation.Quantity);

    public static TransferJobLedger Create(TransferPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(new TransferJobJournal { Plan = plan });
    }

    public ulong CommittedForLeg(Guid allocationId, int legIndex) => Journal.Operations
        .Where(operation => operation.AllocationId == allocationId && operation.LegIndex == legIndex && operation.State == TransferOperationState.Committed)
        .Aggregate(0UL, (total, operation) => checked(total + operation.Quantity));

    /// <summary>For a relay's second leg this excludes cargo not yet received by that relay.</summary>
    public ulong RemainingForLeg(Guid allocationId, int legIndex)
    {
        var allocation = Allocation(allocationId);
        ValidateLegIndex(allocation, legIndex);
        var committed = CommittedForLeg(allocationId, legIndex);
        var ceiling = legIndex == 0 ? allocation.Quantity : CommittedForLeg(allocationId, legIndex - 1);
        if (committed > ceiling) throw new InvalidDataException("Journal outgoing quantity exceeds verified available cargo.");
        return ceiling - committed;
    }

    public ulong DeliveredTo(ulong characterId) => Journal.Plan.Allocations
        .Where(allocation => allocation.RecipientCharacterId == characterId)
        .Aggregate(0UL, (total, allocation) => checked(total + CommittedForLeg(allocation.Id, allocation.Legs.Count - 1)));

    public ulong SourceConsumed(ulong characterId) => Journal.Plan.Allocations
        .Where(allocation => allocation.SourceCharacterId == characterId)
        .Aggregate(0UL, (total, allocation) => checked(total + CommittedForLeg(allocation.Id, 0)));

    public ulong RelayCargo(ulong characterId)
    {
        var total = 0UL;
        foreach (var allocation in Journal.Plan.Allocations.Where(allocation => allocation.Legs.Count == 2 && allocation.Legs[0].ReceiverId == characterId))
        {
            var received = CommittedForLeg(allocation.Id, 0);
            var delivered = CommittedForLeg(allocation.Id, 1);
            if (delivered > received) throw new InvalidDataException("Relay outgoing cargo exceeds received cargo.");
            total = checked(total + received - delivered);
        }
        return total;
    }

    public TransferOperation PrepareBatch(Guid allocationId, int legIndex, uint quantity)
    {
        if (Journal.State == TransferJobState.NeedsReconciliation)
            throw new InvalidOperationException("Reconcile the unfinished operation before issuing another batch.");
        if (Journal.State == TransferJobState.Completed)
            throw new InvalidOperationException("This job is already complete.");
        if (HasPendingOperation)
            throw new InvalidOperationException("Only one unresolved trade operation may exist at a time.");
        var allocation = Allocation(allocationId);
        var priority = Journal.Plan.Definition.Recipients.FindIndex(rule => rule.CharacterId == allocation.RecipientCharacterId);
        var earlierRecipients = Journal.Plan.Definition.Recipients.Take(priority).Select(rule => rule.CharacterId).ToHashSet();
        if (Journal.Plan.Allocations.Any(earlier => earlierRecipients.Contains(earlier.RecipientCharacterId)
            && CommittedForLeg(earlier.Id, earlier.Legs.Count - 1) != earlier.Quantity))
            throw new InvalidOperationException("Finish the higher-priority recipient's allocated deliveries first.");
        if (quantity == 0 || quantity > RemainingForLeg(allocationId, legIndex))
            throw new ArgumentOutOfRangeException(nameof(quantity), "Batch quantity exceeds the verified remaining assignment.");
        var operation = new TransferOperation { AllocationId = allocationId, LegIndex = legIndex, Quantity = quantity };
        Journal.Operations.Add(operation);
        Journal.State = TransferJobState.Running;
        Journal.PauseReason = null;
        return operation;
    }

    public void MarkIssued(Guid operationId)
    {
        var operation = Operation(operationId);
        if (operation.State is TransferOperationState.Issued or TransferOperationState.Observed or TransferOperationState.Committed) return;
        if (operation.State != TransferOperationState.Prepared)
            throw new InvalidOperationException("This operation cannot be issued or replayed.");
        operation.State = TransferOperationState.Issued;
    }

    /// <summary>Duplicate matching observations are harmless; conflicting or unverified results pause reconciliation.</summary>
    public void RecordObservation(Guid operationId, ObservationSide side, TransferObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        var operation = Operation(operationId);
        if (operation.State is TransferOperationState.Prepared or TransferOperationState.Cancelled)
            throw new InvalidOperationException("An unissued or cancelled operation cannot receive a trade observation.");
        var previous = side == ObservationSide.Sender ? operation.SenderObservation : operation.ReceiverObservation;
        if (previous != null)
        {
            if (SameEvidence(previous, observation)) return;
            RequireReconciliation(operation, "A peer reported conflicting results for the same operation.");
            return;
        }
        if (side == ObservationSide.Sender) operation.SenderObservation = JobModelCopies.Observation(observation);
        else operation.ReceiverObservation = JobModelCopies.Observation(observation);
        if (!Matches(operation, side, observation))
        {
            RequireReconciliation(operation, "The observed trade identity or quantity does not match the issued batch.");
            return;
        }
        if (operation.SenderObservation != null && operation.ReceiverObservation != null
            && Matches(operation, ObservationSide.Sender, operation.SenderObservation)
            && Matches(operation, ObservationSide.Receiver, operation.ReceiverObservation))
            operation.State = TransferOperationState.Observed;
    }

    public bool TryCommit(Guid operationId, out string error)
    {
        var operation = Journal.Operations.FirstOrDefault(candidate => candidate.Id == operationId);
        if (operation == null) { error = "Unknown trade operation."; return false; }
        if (operation.State == TransferOperationState.Committed)
        {
            error = operation.Error ?? string.Empty;
            return operation.Error == null;
        }
        if (operation.State is not (TransferOperationState.Observed or TransferOperationState.NeedsReconciliation)
            || operation.SenderObservation == null || operation.ReceiverObservation == null
            || !Matches(operation, ObservationSide.Sender, operation.SenderObservation)
            || !Matches(operation, ObservationSide.Receiver, operation.ReceiverObservation))
        {
            error = "Both peers must report matching verified changes before this batch can be committed.";
            return false;
        }
        // Conflicting duplicate evidence is never automatically resolved merely because the first pair matched.
        if (operation.Error != null)
        {
            error = operation.Error;
            return false;
        }
        if (operation.Quantity > RemainingForLeg(operation.AllocationId, operation.LegIndex))
        {
            error = "Committing this operation would exceed the allocation or verified relay cargo.";
            RequireReconciliation(operation, error);
            return false;
        }
        operation.State = TransferOperationState.Committed;
        if (IsComplete)
        {
            Journal.State = Journal.Plan.HasShortage ? TransferJobState.Paused : TransferJobState.Completed;
            Journal.PauseReason = Journal.Plan.HasShortage ? "Available source stock was delivered, but one or more requested targets remain short." : null;
        }
        else
        {
            Journal.State = TransferJobState.Running;
            Journal.PauseReason = null;
        }
        error = string.Empty;
        return true;
    }

    public void CancelBeforeIssue(Guid operationId)
    {
        var operation = Operation(operationId);
        if (operation.State == TransferOperationState.Cancelled) return;
        if (operation.State != TransferOperationState.Prepared)
            throw new InvalidOperationException("An issued operation may have completed in-game and cannot be cancelled without reconciliation.");
        operation.State = TransferOperationState.Cancelled;
    }

    /// <summary>Handles a verified empty MoveAll plan as well as the end of a fully executed plan.</summary>
    public bool TryComplete(out string error)
    {
        if (HasPendingOperation || !IsComplete || Journal.State == TransferJobState.NeedsReconciliation)
        { error = "Unfinished or unreconciled operations remain."; return false; }
        if (Journal.Plan.HasShortage)
        {
            error = "Available source stock was delivered, but requested targets remain short.";
            Journal.State = TransferJobState.Paused;
            Journal.PauseReason = error;
            return false;
        }
        Journal.State = TransferJobState.Completed;
        Journal.PauseReason = null;
        error = string.Empty;
        return true;
    }

    /// <summary>Only use after both peers freshly prove no transfer occurred, with exact expected identities.</summary>
    public bool ResolveNoTransfer(Guid operationId, TransferObservation sender, TransferObservation receiver, out string error)
    {
        var operation = Operation(operationId);
        if (operation.State is TransferOperationState.Committed or TransferOperationState.Cancelled)
        { error = "A completed or already-cancelled operation cannot be resolved as no transfer."; return false; }
        var leg = Allocation(operation.AllocationId).Legs[operation.LegIndex];
        if (!sender.Verified || !receiver.Verified || sender.Quantity != 0 || receiver.Quantity != 0
            || sender.PeerId != leg.SenderPeerId || sender.CharacterId != leg.SenderId
            || receiver.PeerId != leg.ReceiverPeerId || receiver.CharacterId != leg.ReceiverId)
        { error = "Both exact peers and characters must verify zero change."; return false; }
        if (operation.SenderObservation is { Quantity: > 0, Verified: true }
            || operation.ReceiverObservation is { Quantity: > 0, Verified: true })
        { error = "Existing positive transfer evidence conflicts with a no-transfer resolution."; return false; }
        operation.SenderObservation = JobModelCopies.Observation(sender);
        operation.ReceiverObservation = JobModelCopies.Observation(receiver);
        operation.State = TransferOperationState.Cancelled;
        operation.Error = null;
        Journal.State = TransferJobState.Paused;
        Journal.PauseReason = "No transfer was verified on either peer. Resume to prepare a new operation.";
        error = string.Empty;
        return true;
    }

    public void Pause(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Journal.State == TransferJobState.Completed) return;
        Journal.State = Journal.Operations.Any(operation => operation.State == TransferOperationState.NeedsReconciliation)
            ? TransferJobState.NeedsReconciliation : TransferJobState.Paused;
        Journal.PauseReason = reason;
    }

    /// <summary>Run after loading a journal. Prepared-but-unissued work is safe to discard; issued work is not replayed.</summary>
    public void Recover()
    {
        foreach (var operation in Journal.Operations)
        {
            if (operation.State == TransferOperationState.Prepared) operation.State = TransferOperationState.Cancelled;
            else if (operation.State is TransferOperationState.Issued or TransferOperationState.Observed)
                operation.State = TransferOperationState.NeedsReconciliation;
        }
        if (HasPendingOperation || Journal.Operations.Any(operation => operation.Error != null))
        {
            Journal.State = TransferJobState.NeedsReconciliation;
            Journal.PauseReason = "Reconcile the issued operation with both PCs before resuming. It will not be replayed.";
        }
        else if (Journal.State != TransferJobState.Completed)
        {
            Journal.State = TransferJobState.Paused;
            Journal.PauseReason = "Saved progress loaded. Verify both clients before resuming.";
        }
    }

    private bool Matches(TransferOperation operation, ObservationSide side, TransferObservation observation)
    {
        var leg = Allocation(operation.AllocationId).Legs[operation.LegIndex];
        return observation.Verified && observation.Quantity == operation.Quantity
            && observation.PeerId == (side == ObservationSide.Sender ? leg.SenderPeerId : leg.ReceiverPeerId)
            && observation.CharacterId == (side == ObservationSide.Sender ? leg.SenderId : leg.ReceiverId);
    }

    private void RequireReconciliation(TransferOperation operation, string reason)
    {
        // Preserve committed counters even if a later contradictory message arrives.
        if (operation.State != TransferOperationState.Committed) operation.State = TransferOperationState.NeedsReconciliation;
        operation.Error = reason;
        Journal.State = TransferJobState.NeedsReconciliation;
        Journal.PauseReason = reason;
    }

    private TransferAllocation Allocation(Guid id) => Journal.Plan.Allocations.FirstOrDefault(allocation => allocation.Id == id)
        ?? throw new ArgumentException("Unknown allocation.", nameof(id));
    private TransferOperation Operation(Guid id) => Journal.Operations.FirstOrDefault(operation => operation.Id == id)
        ?? throw new ArgumentException("Unknown operation.", nameof(id));
    private static bool IsPending(TransferOperationState state) => state is TransferOperationState.Prepared or TransferOperationState.Issued
        or TransferOperationState.Observed or TransferOperationState.NeedsReconciliation;
    private static bool SameEvidence(TransferObservation first, TransferObservation second) => first.PeerId == second.PeerId
        && first.CharacterId == second.CharacterId && first.Quantity == second.Quantity && first.Verified == second.Verified;
    private static void ValidateLegIndex(TransferAllocation allocation, int legIndex)
    {
        if (legIndex < 0 || legIndex >= allocation.Legs.Count) throw new ArgumentOutOfRangeException(nameof(legIndex));
    }

    private static void ValidateJournal(TransferJobJournal journal)
    {
        if (journal.Version != 1 || journal.Plan == null || journal.Operations == null || !Enum.IsDefined(journal.State))
            throw new InvalidDataException("Unsupported or incomplete job journal.");
        JobPlanner.ValidateDefinition(journal.Plan.Definition);
        var allocations = journal.Plan.Allocations;
        if (allocations == null || allocations.Any(allocation => allocation == null || allocation.Id == Guid.Empty || allocation.Quantity == 0
            || allocation.Legs == null || allocation.Legs.Count is < 1 or > 2)
            || allocations.Select(allocation => allocation.Id).Distinct().Count() != allocations.Count)
            throw new InvalidDataException("Invalid job allocations.");
        if (journal.Plan.SourceBudgets is null || journal.Plan.ProtectedStock is null || journal.Plan.UnmetTargets is null
            || journal.Plan.Definition.Sources.Any(source => !journal.Plan.SourceBudgets.ContainsKey(source)))
            throw new InvalidDataException("Job source budgets or protected-stock records are missing.");
        foreach (var source in journal.Plan.Definition.Sources)
        {
            var allocated = allocations.Where(allocation => allocation.SourceCharacterId == source)
                .Aggregate(0UL, (total, allocation) => checked(total + allocation.Quantity));
            if (allocated > journal.Plan.SourceBudgets[source])
                throw new InvalidDataException("Allocations exceed the original source budget.");
        }
        foreach (var recipient in journal.Plan.Definition.Recipients)
        {
            var allocated = allocations.Where(allocation => allocation.RecipientCharacterId == recipient.CharacterId)
                .Aggregate(0UL, (total, allocation) => checked(total + allocation.Quantity));
            if (!recipient.Quantity.HasValue) continue;
            var cap = recipient.Quantity.Value;
            if (journal.Plan.Definition.QuantityMode == JobQuantityMode.RestockToTotal)
            {
                var protectedStock = journal.Plan.ProtectedStock.GetValueOrDefault(recipient.CharacterId);
                if (protectedStock > cap) throw new InvalidDataException("Protected stock exceeds the recipient's total-stock target.");
                cap -= protectedStock;
            }
            if (allocated > cap) throw new InvalidDataException("Allocations exceed a recipient's configured quantity.");
        }
        foreach (var allocation in allocations)
        {
            if (!journal.Plan.Definition.Sources.Contains(allocation.SourceCharacterId)
                || !journal.Plan.Definition.Recipients.Any(rule => rule.CharacterId == allocation.RecipientCharacterId)
                || allocation.Legs[0].SenderId != allocation.SourceCharacterId
                || allocation.Legs[^1].ReceiverId != allocation.RecipientCharacterId)
                throw new InvalidDataException("Allocation does not match its job source and recipient.");
            for (var index = 0; index < allocation.Legs.Count; index++)
            {
                var leg = allocation.Legs[index];
                if (leg.SenderId == 0 || leg.ReceiverId == 0 || leg.SenderId == leg.ReceiverId
                    || leg.SenderPeerId == Guid.Empty || leg.ReceiverPeerId == Guid.Empty || leg.SenderPeerId == leg.ReceiverPeerId)
                    throw new InvalidDataException("Each trade leg requires distinct paired accounts and characters.");
                if (index > 0 && (allocation.Legs[index - 1].ReceiverId != leg.SenderId
                    || allocation.Legs[index - 1].ReceiverPeerId != leg.SenderPeerId))
                    throw new InvalidDataException("Relay legs do not connect.");
            }
            if (allocation.Legs.Count == 2)
            {
                var first = allocation.Legs[0];
                var second = allocation.Legs[1];
                if (first.SenderPeerId != second.ReceiverPeerId
                    || !journal.Plan.Definition.RelayCharacters.TryGetValue(first.ReceiverPeerId, out var configuredRelay)
                    || configuredRelay != first.ReceiverId)
                    throw new InvalidDataException("Relay route does not match the configured intermediary.");
            }
        }
        if (journal.Operations.Any(operation => operation == null || operation.Id == Guid.Empty || operation.Quantity == 0 || !Enum.IsDefined(operation.State))
            || journal.Operations.Select(operation => operation.Id).Distinct().Count() != journal.Operations.Count)
            throw new InvalidDataException("Invalid or duplicate journal operations.");
        if (journal.Operations.Count(operation => IsPending(operation.State)) > 1)
            throw new InvalidDataException("More than one unresolved operation is present.");
        foreach (var operation in journal.Operations)
        {
            var allocation = allocations.FirstOrDefault(candidate => candidate.Id == operation.AllocationId)
                ?? throw new InvalidDataException("Operation references an unknown allocation.");
            if (operation.LegIndex < 0 || operation.LegIndex >= allocation.Legs.Count)
                throw new InvalidDataException("Operation references an unknown leg.");
            if (operation.State == TransferOperationState.Committed)
            {
                var leg = allocation.Legs[operation.LegIndex];
                if (operation.SenderObservation is not { Verified: true } sender
                    || operation.ReceiverObservation is not { Verified: true } receiver
                    || sender.Quantity != operation.Quantity || receiver.Quantity != operation.Quantity
                    || sender.PeerId != leg.SenderPeerId || sender.CharacterId != leg.SenderId
                    || receiver.PeerId != leg.ReceiverPeerId || receiver.CharacterId != leg.ReceiverId)
                    throw new InvalidDataException("Committed operation lacks matching peer observations.");
            }
        }
        foreach (var allocation in allocations)
        {
            var previous = allocation.Quantity;
            for (var index = 0; index < allocation.Legs.Count; index++)
            {
                var total = journal.Operations.Where(operation => operation.AllocationId == allocation.Id
                    && operation.LegIndex == index && operation.State == TransferOperationState.Committed)
                    .Aggregate(0UL, (sum, operation) => checked(sum + operation.Quantity));
                if (total > previous) throw new InvalidDataException("Committed quantity exceeds allocation or received relay cargo.");
                var pending = journal.Operations.Where(operation => operation.AllocationId == allocation.Id
                    && operation.LegIndex == index && IsPending(operation.State))
                    .Aggregate(0UL, (sum, operation) => checked(sum + operation.Quantity));
                if (pending > previous - total)
                    throw new InvalidDataException("Pending quantity exceeds allocation or received relay cargo.");
                previous = total;
            }
        }
        if (journal.State == TransferJobState.Completed && (journal.Plan.HasShortage || allocations.Any(allocation =>
            journal.Operations.Where(operation => operation.AllocationId == allocation.Id
                && operation.LegIndex == allocation.Legs.Count - 1 && operation.State == TransferOperationState.Committed)
                .Aggregate(0UL, (sum, operation) => checked(sum + operation.Quantity)) != allocation.Quantity)))
            throw new InvalidDataException("Journal claims completion despite unresolved requested quantities.");
    }
}
