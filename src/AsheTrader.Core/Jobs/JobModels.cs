namespace AsheTrader.Core.Jobs;

public enum JobQuantityMode { MoveAll, DeliverAmount, RestockToTotal }
public enum InventoryVerification { Unknown, Cached, Verified }
public enum TransferJobState { Planned, Running, Paused, NeedsReconciliation, Completed }
public enum TransferOperationState { Prepared, Issued, Observed, Committed, Cancelled, NeedsReconciliation }
public enum ObservationSide { Sender, Receiver }

public sealed class RecipientRule
{
    public ulong CharacterId { get; set; }
    /// <summary>Additional amount, or final total for RestockToTotal. Null is final-remainder only.</summary>
    public ulong? Quantity { get; set; }
}

public sealed class TransferJobDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Transfer job";
    public uint ItemId { get; set; }
    public bool HighQuality { get; set; }
    public JobQuantityMode QuantityMode { get; set; } = JobQuantityMode.MoveAll;
    public List<ulong> Sources { get; set; } = [];
    public List<RecipientRule> Recipients { get; set; } = [];
    /// <summary>Each peer may nominate a character for relay legs through that peer's account.</summary>
    public Dictionary<Guid, ulong> RelayCharacters { get; set; } = [];
}

public sealed class RetainerStock
{
    public ulong RetainerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ulong Quantity { get; set; }
    public uint AdditionalCapacity { get; set; }
    public InventoryVerification Verification { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
}

/// <summary>Counts for this job's exact item and quality; all expected retainers must be represented.</summary>
public sealed class JobCharacterSnapshot
{
    public ulong CharacterId { get; set; }
    public Guid PeerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public ulong BagQuantity { get; set; }
    public uint BagCapacity { get; set; }
    public InventoryVerification Verification { get; set; }
    public bool RetainerRosterVerified { get; set; }
    public List<RetainerStock> Retainers { get; set; } = [];
    public DateTimeOffset? RetrievedAt { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public ulong TotalQuantity => Retainers.Aggregate(BagQuantity, (total, retainer) => checked(total + retainer.Quantity));
    public bool IsFullyVerified => Verification == InventoryVerification.Verified && RetainerRosterVerified
        && Retainers.All(retainer => retainer.Verification == InventoryVerification.Verified);
}

public sealed class TransferLeg
{
    public ulong SenderId { get; set; }
    public Guid SenderPeerId { get; set; }
    public ulong ReceiverId { get; set; }
    public Guid ReceiverPeerId { get; set; }
}

public sealed class TransferAllocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ulong SourceCharacterId { get; set; }
    public ulong RecipientCharacterId { get; set; }
    public ulong Quantity { get; set; }
    public List<TransferLeg> Legs { get; set; } = [];
}

public sealed class UnmetTarget
{
    public ulong CharacterId { get; set; }
    public ulong Requested { get; set; }
    public ulong Allocated { get; set; }
    public ulong Missing => Requested - Allocated;
}

public sealed class TransferPlan
{
    public TransferJobDefinition Definition { get; set; } = new();
    public List<TransferAllocation> Allocations { get; set; } = [];
    public List<UnmetTarget> UnmetTargets { get; set; } = [];
    public Dictionary<ulong, ulong> SourceBudgets { get; set; } = [];
    public Dictionary<ulong, ulong> ProtectedStock { get; set; } = [];
    public bool HasShortage => UnmetTargets.Any(target => target.Missing > 0);
    public ulong AllocatedQuantity => Allocations.Aggregate(0UL, (total, allocation) => checked(total + allocation.Quantity));
}

public sealed class TransferObservation
{
    public Guid PeerId { get; set; }
    public ulong CharacterId { get; set; }
    /// <summary>Exact decrease for sender / increase for receiver, already verified by local executor.</summary>
    public uint Quantity { get; set; }
    public bool Verified { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}

public sealed class TransferOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AllocationId { get; set; }
    public int LegIndex { get; set; }
    public uint Quantity { get; set; }
    public TransferOperationState State { get; set; }
    public TransferObservation? SenderObservation { get; set; }
    public TransferObservation? ReceiverObservation { get; set; }
    public string? Error { get; set; }
}

/// <summary>Serializable durable state. Save after each transition before issuing the next command.</summary>
public sealed class TransferJobJournal
{
    public int Version { get; set; } = 1;
    public TransferPlan Plan { get; set; } = new();
    public TransferJobState State { get; set; } = TransferJobState.Planned;
    public List<TransferOperation> Operations { get; set; } = [];
    public string? PauseReason { get; set; }
}
