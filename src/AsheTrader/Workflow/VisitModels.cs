using AsheTrader.Core.Jobs;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

/// <summary>Canonical, independently budgeted progress for one item in a character-oriented run.</summary>
public sealed class VisitItemState
{
    public int ItemIndex { get; set; }
    public TransferJobDefinition Definition { get; set; } = new();
    public TransferJobJournal? Journal { get; set; }
    public Dictionary<ulong, JobCharacterSnapshot> Snapshots { get; set; } = [];
    public Dictionary<ulong, ulong> Withdrawn { get; set; } = [];
    public Dictionary<ulong, ulong> Deposited { get; set; } = [];
    public Dictionary<Guid, ulong> AutomaticRelays { get; set; } = [];
    public Dictionary<ulong, uint> RelayCapacity { get; set; } = [];
    public List<CollectionRouteStop>? CollectionRoute { get; set; }
    public Dictionary<ulong, string> SkippedCharacters { get; set; } = [];
    public string ItemSkipReason { get; set; } = "";
    public ulong? RelayFlushCharacterId { get; set; }
}

public enum VisitPreparationStage { Login, Inspect, Withdraw, Deposit, Travel, FinalInspect, Ready }

public sealed class VisitParticipantPreparation
{
    public VisitPreparationStage Stage { get; set; }
    public int LineIndex { get; set; }
    public int? StorageItemIndex { get; set; }
    public uint StorageQuantity { get; set; }
    public HashSet<int> AttemptedDeposits { get; set; } = [];
}

/// <summary>One eligible allocation contribution; quantities remain item-specific.</summary>
public sealed class VisitWorkLine
{
    public int ItemIndex { get; set; }
    public Guid AllocationId { get; set; }
    public int LegIndex { get; set; }
    public ulong MaximumQuantity { get; set; }
    public uint SenderBagQuantity { get; set; }
    public uint SenderBagCapacity { get; set; }
    public uint StackSize { get; set; }
    public uint ReceiverBagCapacity { get; set; }
    public uint? SenderFreeBagSlots { get; set; }
    public uint? ReceiverFreeBagSlots { get; set; }
}

/// <summary>One pinned pair and destination, prepared on both PCs before an agreed mixed-item trade.</summary>
public sealed class PreparedVisit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ulong SenderId { get; set; }
    public ulong ReceiverId { get; set; }
    public Guid SenderPeerId { get; set; }
    public Guid ReceiverPeerId { get; set; }
    public TravelLocation Meeting { get; set; } = new();
    public List<VisitWorkLine> Lines { get; set; } = [];
    public VisitParticipantPreparation Sender { get; set; } = new();
    public VisitParticipantPreparation Receiver { get; set; } = new();
    public bool Recovering { get; set; }
    public bool ReplanRequested { get; set; }
}
