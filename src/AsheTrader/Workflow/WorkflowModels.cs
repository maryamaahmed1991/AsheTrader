using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

public sealed class WorkflowSettings
{
    public Guid PeerId { get; set; } = Guid.NewGuid();
    public string PeerName { get; set; } = Environment.MachineName;
    public string BindAddress { get; set; } = "";
    public string ConnectAddress { get; set; } = "";
    public int Port { get; set; } = 45874;
    public string PairingKey { get; set; } = "";
    public Guid? ExpectedRemotePeerId { get; set; }
    public bool AllowRemoteControl { get; set; }
    public List<SavedTransferJob> Jobs { get; set; } = [];
    public Dictionary<ulong, TravelLocation> StorageRoutes { get; set; } = [];
    public Dictionary<ulong, ulong[]> AllowedRetainers { get; set; } = [];
}

public sealed class SavedTransferJob
{
    public TransferJobDefinition Definition { get; set; } = new();
    public TravelLocation Meeting { get; set; } = new();
}

public sealed class PeerRoster
{
    public Guid PeerId { get; set; }
    public string Name { get; set; } = "";
    public bool ControlEnabled { get; set; }
    public List<CharacterProfile> Characters { get; set; } = [];
}

public enum WorkerCommandKind { Game, PreviewBatch, ReceiveBatch, SendBatch }

public sealed class WorkerCommand
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public WorkerCommandKind Kind { get; set; }
    public GameOperation Operation { get; set; } = new();
    public CharacterProfile? Partner { get; set; }
}

public sealed class WorkerResponse
{
    public Guid CommandId { get; set; }
    public bool Success { get; set; }
    public bool Ready { get; set; }
    public bool Unchanged { get; set; }
    public bool Ambiguous { get; set; }
    public bool OperatorReviewed { get; set; }
    public bool NeverStarted { get; set; }
    public uint? BeforeQuantity { get; set; }
    public uint? CurrentQuantity { get; set; }
    public string Error { get; set; } = "";
    public uint Quantity { get; set; }
    public GameOperationResult? Observation { get; set; }
}

public sealed class WorkerRecord
{
    public string Fingerprint { get; set; } = "";
    public WorkerCommand Command { get; set; } = new();
    public uint? BeforeQuantity { get; set; }
    public WorkerResponse? Response { get; set; }
    public bool TradeStarted { get; set; }
}

public sealed class PendingCommand
{
    public bool InventoryAdjustmentApplied { get; set; }
    public Guid PeerId { get; set; }
    public WorkerCommand Command { get; set; } = new();
    public WorkerResponse? Response { get; set; }
    public bool Ready { get; set; }
}

public enum CoordinatorStep
{
    DiscoverLogin, DiscoverScan, Plan, SelectLeg, LoginSender, LoginReceiver,
    InspectSender, Withdraw, InspectReceiver, Deposit, TravelSender, TravelReceiver,
    PreviewBatch, ArmReceiver, SendBatch, AwaitTrade, Finished
}

public sealed class CoordinatorRun
{
    public bool DiscoveryOnly { get; set; }
    public SavedTransferJob Job { get; set; } = new();
    public Dictionary<ulong, JobCharacterSnapshot> Snapshots { get; set; } = [];
    public List<ulong> DiscoveryOrder { get; set; } = [];
    public int DiscoveryIndex { get; set; }
    public CoordinatorStep Step { get; set; }
    public TransferJobJournal? Journal { get; set; }
    public Guid AllocationId { get; set; }
    public int LegIndex { get; set; }
    public uint DesiredQuantity { get; set; }
    public uint SenderBagQuantity { get; set; }
    public uint SenderBagCapacity { get; set; }
    public uint SenderStackSize { get; set; }
    public uint ReceiverCapacity { get; set; }
    // Verified pickups are progress information only; only a confirmed trade counts as delivery.
    public Dictionary<ulong, ulong> Withdrawn { get; set; } = [];
    public Dictionary<ulong, ulong> Deposited { get; set; } = [];
    public Guid? TradeOperationId { get; set; }
    public List<PendingCommand> Pending { get; set; } = [];
    public bool Paused { get; set; }
    public string Status { get; set; } = "";
}

public sealed record ManualResolution(Guid CommandId, bool Transferred);
