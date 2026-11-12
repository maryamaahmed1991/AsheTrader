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
    // Keep the original first-item field so saved single-item jobs need no destructive migration.
    public List<TransferJobDefinition> AdditionalItems { get; set; } = [];
    [Newtonsoft.Json.JsonIgnore]
    public IEnumerable<TransferJobDefinition> ItemDefinitions => new[] { Definition }.Concat(AdditionalItems);
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
    public bool AutoReconciled { get; set; }
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
    public Guid? TradeTallyRound { get; set; }
    public uint? TradeTallyCurrent { get; set; }
    public StorageMovementCheckpoint? StorageCheckpoint { get; set; }
}

public sealed class PendingCommand
{
    public bool InventoryAdjustmentApplied { get; set; }
    public uint? AppliedStorageQuantity { get; set; }
    public bool StorageRecoveryRequested { get; set; }
    // Null means the active item; a value identifies a completed item whose received stock is being stored.
    public int? InventoryItemIndex { get; set; }
    public Guid PeerId { get; set; }
    public WorkerCommand Command { get; set; } = new();
    public WorkerResponse? Response { get; set; }
    public bool Ready { get; set; }
}

public enum CoordinatorStep
{
    DiscoverLogin, DiscoverScan, Plan, SelectLeg, LoginSender, LoginReceiver,
    InspectSender, Withdraw, InspectReceiver, Deposit, TravelSender, TravelReceiver,
    PreviewBatch, ArmReceiver, SendBatch, AwaitTrade, Finished,
    // Append new states so existing numeric journal states keep their meaning.
    RelayLogin, RelayInspect, DepositPreviousItem, DiscoverCache, PrepareParticipants
}

public enum ParticipantPreparationStage { Login, Inspect, Withdraw, Deposit, Travel, Ready }

/// <summary>One sequential stream of native operations on one PC.</summary>
public sealed class ParticipantPreparation
{
    public ulong CharacterId { get; set; }
    public Guid PeerId { get; set; }
    public ParticipantPreparationStage Stage { get; set; }
    public uint StorageQuantity { get; set; }
    public int? InventoryItemIndex { get; set; }
}

public sealed class TransferPreparation
{
    public ParticipantPreparation Sender { get; set; } = new();
    public ParticipantPreparation Receiver { get; set; } = new();
    public bool Recovering { get; set; }
    public bool ReplanRequested { get; set; }
}

public sealed class CoordinatorRun
{
    public bool DiscoveryOnly { get; set; }
    public SavedTransferJob Job { get; set; } = new();
    public int ActiveItemIndex { get; set; }
    public List<CoordinatorItemResult> CompletedItems { get; set; } = [];
    public Dictionary<Guid, ulong> AutomaticRelays { get; set; } = [];
    public List<ulong> RejectedRelayCharacters { get; set; } = [];
    public ulong? RelayCandidateId { get; set; }
    public Guid? RelayCandidatePeerId { get; set; }
    public int? PreviousItemDepositIndex { get; set; }
    public ulong PreviousItemDepositCharacterId { get; set; }
    [Newtonsoft.Json.JsonIgnore]
    public TransferJobDefinition ActiveDefinition => ActiveItemIndex >= 0 && ActiveItemIndex < TotalItemCount
        ? Job.ItemDefinitions.ElementAt(ActiveItemIndex)
        : throw new InvalidDataException("The saved job's active item index is invalid.");
    [Newtonsoft.Json.JsonIgnore]
    public int TotalItemCount => 1 + Job.AdditionalItems.Count;
    [Newtonsoft.Json.JsonIgnore]
    public bool HasShortages => Journal?.Plan.HasShortage == true || CompletedItems.Any(item => item.Journal?.Plan.HasShortage == true);
    public Dictionary<ulong, JobCharacterSnapshot> Snapshots { get; set; } = [];
    public List<ulong> DiscoveryOrder { get; set; } = [];
    public int DiscoveryIndex { get; set; }
    public Dictionary<ulong, string> DiscoveryNotes { get; set; } = [];
    public CoordinatorStep Step { get; set; }
    public TransferJobJournal? Journal { get; set; }
    public Guid AllocationId { get; set; }
    public int LegIndex { get; set; }
    public uint DesiredQuantity { get; set; }
    public uint SenderBagQuantity { get; set; }
    public uint SenderBagCapacity { get; set; }
    public uint SenderStackSize { get; set; }
    public uint ReceiverCapacity { get; set; }
    // Relay capacity is an execution hint refreshed by live inspections and committed exact-item trades.
    // The ledger, not this hint, remains authoritative for cargo ownership and delivered quantities.
    public Dictionary<ulong, uint> RelayCapacity { get; set; } = [];
    public ulong? RelayFlushCharacterId { get; set; }
    public TravelLocation? EffectiveMeeting { get; set; }
    public List<CollectionRouteStop>? CollectionRoute { get; set; }
    public Dictionary<ulong, uint> ObservedWorlds { get; set; } = [];
    public bool MeetingPinned { get; set; }
    public string MeetingDataCenterName { get; set; } = "";
    public string MeetingReason { get; set; } = "";
    public string MeetingWorldName { get; set; } = "";
    public int EstimatedWorldTravelers { get; set; }
    public int ConfiguredWorldTravelers { get; set; }
    // Verified pickups are progress information only; only a confirmed trade counts as delivery.
    public Dictionary<ulong, ulong> Withdrawn { get; set; } = [];
    public Dictionary<ulong, ulong> Deposited { get; set; } = [];
    public Guid? TradeOperationId { get; set; }
    public TransferPreparation? Preparation { get; set; }
    public TradeRecoveryState? TradeRecovery { get; set; }
    public int ConsecutiveEmptyTrades { get; set; }
    public List<PendingCommand> Pending { get; set; } = [];
    public bool Paused { get; set; }
    public string Status { get; set; } = "";
}

/// <summary>Completed item ledger and rules remain fixed; its storage counter can advance when later items need room.</summary>
public sealed class CoordinatorItemResult
{
    public int ItemIndex { get; set; }
    public TransferJobDefinition Definition { get; set; } = new();
    public bool DiscoveryOnly { get; set; }
    public TransferJobJournal? Journal { get; set; }
    public Dictionary<ulong, JobCharacterSnapshot> Snapshots { get; set; } = [];
    public Dictionary<ulong, ulong> Withdrawn { get; set; } = [];
    public Dictionary<ulong, ulong> Deposited { get; set; } = [];
    public Dictionary<Guid, ulong> AutomaticRelays { get; set; } = [];
}

public sealed record ManualResolution(Guid CommandId, bool Transferred, bool StorageVerifiedOnly = false);

public sealed record TradeTallyRequest(Guid CommandId, Guid RoundId);
public sealed record TradeTallyReply(Guid CommandId, Guid RoundId, bool Ready, bool Retryable, WorkerResponse? Result, string Error = "");
public sealed record AutomaticTradeResolution(Guid CommandId, Guid RoundId, bool Transferred, uint? ExpectedBefore, uint? ExpectedCurrent);
public sealed record TradeResolutionReply(Guid CommandId, Guid RoundId, WorkerResponse? Result, string Error = "");

public sealed class TradeRecoveryState
{
    public Guid RoundId { get; set; } = Guid.NewGuid();
    public bool? Transferred { get; set; }
    public Dictionary<Guid, WorkerResponse> Tallies { get; set; } = [];
    public Dictionary<Guid, WorkerResponse> Acknowledgements { get; set; } = [];
}

/// <summary>Records an operator's bounded storage review without inventing a completed movement.</summary>
internal static class StorageReconciliation
{
    public static bool IsStorage(WorkerCommand command) => command.Kind == WorkerCommandKind.Game
        && command.Operation.Kind is GameOperationKind.Deposit or GameOperationKind.Withdraw;

    public static bool IsReviewed(WorkerResponse? response) => response is
        { Success: false, OperatorReviewed: true, Ambiguous: false, Observation: { Success: false, MovementUncertain: false } };

    public static WorkerResponse Review(WorkerCommand command, WorkerResponse? response, uint currentBagQuantity)
    {
        if (!IsStorage(command) || command.Operation.Quantity == 0 || response == null
            || response.CommandId != command.Id || response.Observation is not { } observation
            || observation.Id != command.Operation.Id || observation.CharacterId != command.Operation.CharacterId
            || observation.MovedQuantity > command.Operation.Quantity || response.Success || response.NeverStarted)
            throw new InvalidOperationException("This storage operation has no recorded observation that can be reviewed safely.");
        if (IsReviewed(response)) return response;
        if (!response.Ambiguous || !observation.MovementUncertain)
            throw new InvalidOperationException("This storage operation is not awaiting an uncertain-movement review.");
        if (currentBagQuantity != observation.BagQuantity)
            throw new InvalidOperationException("The current item/quality bag count differs from the recorded storage-stop count. This review cannot be accepted.");
        var reviewed = Newtonsoft.Json.JsonConvert.DeserializeObject<WorkerResponse>(
            Newtonsoft.Json.JsonConvert.SerializeObject(response))!;
        reviewed.Success = false;
        reviewed.Ambiguous = false;
        reviewed.OperatorReviewed = true;
        reviewed.Quantity = observation.MovedQuantity;
        reviewed.CurrentQuantity = currentBagQuantity;
        reviewed.Observation!.Success = false;
        reviewed.Observation.MovementUncertain = false;
        reviewed.Error = $"Operator confirmed that only the recorded {observation.MovedQuantity:N0} item(s) moved. The job remains paused; use Resume / reconcile to continue from verified progress.";
        reviewed.Observation.Error = reviewed.Error;
        return reviewed;
    }
}
