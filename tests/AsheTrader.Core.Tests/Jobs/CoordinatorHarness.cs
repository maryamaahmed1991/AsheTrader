using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using AsheTrader.Workflow.Native;
using Newtonsoft.Json;

namespace AsheTrader.Workflow;

/// <summary>
/// Test-only other half of the production TransferCoordinator partial. Only transport, persistence,
/// and game commands are faked; the coordinator's actual transitions and command sequencing run unchanged.
/// </summary>
public sealed partial class WorkflowService
{
    private readonly HarnessPlugin plugin = new();
    private readonly HarnessExecutor executor = new();
    private readonly HarnessPeer peer;
    private readonly WorkflowSettings settings;
    private readonly Dictionary<ulong, SimulatedCharacter> characters;
    private readonly Dictionary<Guid, ulong> loggedIn = [];
    private readonly Dictionary<Guid, WorkerResponse> durableResponses = [];
    private readonly Dictionary<Guid, WorkerCommand> receivedCommands = [];
    private readonly Dictionary<Guid, Guid> commandOwners = [];
    private readonly Dictionary<Guid, uint> tradeBeforeQuantities = [];
    private WorkerCommand? armedReceiver;
    private WorkerCommand? deferredSender;
    private uint armedBefore;
    private readonly bool host = true;
    private readonly bool journalHealthy = true;
    private readonly object? activeWorker = null;
    private string error = string.Empty;
    private WorkflowSettings Settings => settings;
    private IReadOnlyList<RouteWorld> RoutingWorlds() => TestWorlds;
    private void RecordDiagnostic(string message) => Diagnostics.Add(message);
    private uint PlanningItemStackSize() => 999;

    public CoordinatorRun? Run { get; private set; }
    public PeerRoster? RemoteRoster { get; private set; }
    public bool Connected { get; set; } = true;
    public string LastError => error;
    public string SavedJson { get; private set; } = "null";
    public List<SimulatedCommandTrace> Trace { get; } = [];
    public List<SimulatedTrade> Trades { get; } = [];
    public List<string> Diagnostics { get; } = [];
    public List<RouteWorld> TestWorlds { get; set; } = [new(1, "Home World", 1, "Home DC", 1)];
    public IReadOnlyDictionary<ulong, SimulatedCharacter> Characters => characters;
    public Func<WorkerCommand, GameOperationResult, GameOperationResult>? TransformResult { get; set; }
    public uint? DepositMovementLimit { get; set; }
    public uint? WithdrawMovementLimit { get; set; }
    public bool RejectNextSenderBeforeStart { get; set; }
    public bool DeferTradeCompletion { get; set; }
    public HashSet<Guid> StoppedTradeCommands { get; } = [];
    public bool StorageMenusClosed { get; set; } = true;
    // The worker still persists the result. A subsequent query can recover a lost response.
    public Predicate<WorkerCommand>? SuppressInitialResponse { get; set; }

    public void SetCachedFreeSlots(ulong characterId, uint? freeSlots)
    {
        var profile = AllCharacters().Single(entry => entry.Character.ContentId == characterId).Character;
        profile.InventoryFreeSlots = freeSlots;
        profile.InventorySlotsRetrievedAt = freeSlots.HasValue ? DateTimeOffset.UtcNow : null;
    }

    public WorkflowService(Guid localPeer, Guid remotePeer, params SimulatedCharacter[] initialCharacters)
    {
        settings = new() { PeerId = localPeer, AllowRemoteControl = true };
        peer = new() { RemotePeerId = remotePeer };
        characters = initialCharacters.ToDictionary(character => character.Id);
        foreach (var character in initialCharacters)
            if (character.CurrentWorldId == 0) character.CurrentWorldId = character.HomeWorldId;
        plugin.Config.Characters = initialCharacters.Where(character => character.PeerId == localPeer).Select(Profile).ToList();
        RemoteRoster = new()
        {
            PeerId = remotePeer, Name = "Worker", ControlEnabled = true,
            Characters = initialCharacters.Where(character => character.PeerId == remotePeer).Select(Profile).ToList(),
        };
    }

    public void Tick()
    {
        if (Run is not { Paused: false, Step: not CoordinatorStep.Finished }) return;
        try { AdvanceCoordinator(); }
        catch (Exception exception) { Pause(exception.Message); }
    }

    public void RunUntil(Func<CoordinatorRun, bool> condition, int maximumTicks = 10000)
    {
        for (var tick = 0; tick < maximumTicks; tick++)
        {
            if (Run != null && condition(Run)) return;
            Tick();
            if (Run is { Paused: true } && !condition(Run))
                throw new InvalidOperationException($"Coordinator paused before checkpoint: {LastError}");
        }
        throw new TimeoutException($"Coordinator did not reach its test checkpoint. Step={Run?.Step}; error={error}");
    }

    public void RunToTerminal() => RunUntil(run => run.Step == CoordinatorStep.Finished || run.Paused);

    public void SimulateCoordinatorRestart(bool interruptArmedReceiver = false)
    {
        Run = Decode<CoordinatorRun>(SavedJson);
        Run.Paused = true;
        error = string.Empty;
        resendAt = 0;
        if (interruptArmedReceiver && armedReceiver != null)
        {
            var character = characters[armedReceiver.Operation.CharacterId];
            var stock = character.Inventory(armedReceiver.Operation.ItemId, armedReceiver.Operation.HighQuality);
            durableResponses[armedReceiver.Id] = new()
            {
                CommandId = armedReceiver.Id, Ambiguous = true,
                Unchanged = stock.Bags == armedBefore, BeforeQuantity = armedBefore,
                CurrentQuantity = stock.Bags, Error = "Receiver interrupted before a verified result.",
            };
            armedReceiver = null;
        }
        SaveRun();
    }

    public void PauseForTest(string reason = "Test pause") => Pause(reason);

    public void FailTradeForTest(WorkerCommandKind kind, string failure)
    {
        var pending = Run!.Pending.Single(command => command.Command.Kind == kind);
        Deliver(pending.PeerId, new() { CommandId = pending.Command.Id, Ambiguous = true, Error = failure });
    }

    private void RequireIdle()
    {
        if (Run is { Paused: false, Step: not CoordinatorStep.Finished }) throw new InvalidOperationException("A job is already running.");
    }

    private IEnumerable<(Guid PeerId, CharacterProfile Character)> AllCharacters() =>
        plugin.Config.Characters.Select(character => (Settings.PeerId, character))
            .Concat(RemoteRoster?.Characters.Select(character => (RemoteRoster.PeerId, character)) ?? []);

    private static T Decode<T>(string json) => JsonConvert.DeserializeObject<T>(json)
        ?? throw new InvalidDataException("Missing test payload.");
    private void SaveRun() => SavedJson = JsonConvert.SerializeObject(Run);
    private void Pause(string reason)
    {
        error = reason;
        if (Run != null) { Run.Paused = true; Run.Status = reason; SaveRun(); }
        foreach (var command in new[] { armedReceiver, deferredSender }.OfType<WorkerCommand>())
        {
            StoppedTradeCommands.Add(command.Id);
            if (!durableResponses.ContainsKey(command.Id))
                durableResponses[command.Id] = new()
                {
                    CommandId = command.Id, Ambiguous = true,
                    Error = "Trade stopped after the coordinator requested pause.",
                };
        }
        armedReceiver = null;
        deferredSender = null;
    }

    private void Send<T>(string kind, T payload)
    {
        switch (kind)
        {
            case "command": HandleWorker((WorkerCommand)(object)payload!, peer.RemotePeerId!.Value); break;
            case "query": HandleQuery((Guid)(object)payload!, peer.RemotePeerId!.Value); break;
            case "resolve": ResolveWorker((ManualResolution)(object)payload!, peer.RemotePeerId!.Value); break;
            default: throw new InvalidOperationException($"Unexpected coordinator message: {kind}");
        }
    }

    private void HandleWorker(WorkerCommand command, Guid owner)
    {
        if (receivedCommands.ContainsKey(command.Id)) { HandleQuery(command.Id, owner); return; }
        receivedCommands[command.Id] = Clone(command);
        commandOwners[command.Id] = owner;
        var character = characters[command.Operation.CharacterId];
        var stock = character.Inventory(command.Operation.ItemId, command.Operation.HighQuality);
        if (character.PeerId != owner) throw new InvalidOperationException("Command went to the wrong peer.");
        Trace.Add(new(command.Kind, command.Operation.Kind, character.Id, command.Operation.Quantity, character.Location,
            command.Operation.ItemId, command.Operation.HighQuality, command.Operation.Destination?.WorldId ?? 0, character.CurrentWorldId));

        if (command.Kind == WorkerCommandKind.Game)
        {
            var operation = command.Operation;
            if (operation.Kind == GameOperationKind.Login) loggedIn[owner] = character.Id;
            else if (operation.Kind != GameOperationKind.ReadCachedInventory && loggedIn.GetValueOrDefault(owner) != character.Id)
                throw new InvalidOperationException("Operation ran on the wrong active character.");
            uint moved = 0;
            switch (operation.Kind)
            {
                case GameOperationKind.ScanRetainers: character.Location = "home"; character.CurrentWorldId = character.HomeWorldId; break;
                case GameOperationKind.Withdraw:
                    character.Location = "home";
                    character.CurrentWorldId = character.HomeWorldId;
                    moved = Math.Min(operation.Quantity, Math.Min(stock.RetainerTotal, character.BagSpace));
                    if (WithdrawMovementLimit is { } pickupLimit) moved = Math.Min(moved, pickupLimit);
                    RemoveStored(stock, moved); stock.Bags += moved;
                    break;
                case GameOperationKind.Deposit:
                    character.Location = "home";
                    character.CurrentWorldId = character.HomeWorldId;
                    moved = Math.Min(operation.Quantity, Math.Min(stock.Bags, character.StorageSpace));
                    if (DepositMovementLimit is { } limit) moved = Math.Min(moved, limit);
                    stock.Bags -= moved;
                    var firstRetainer = stock.Retainers.Keys.FirstOrDefault();
                    if (firstRetainer == 0 && moved != 0) throw new InvalidOperationException("No retainer to deposit into.");
                    if (moved != 0) stock.Retainers[firstRetainer] += moved;
                    break;
                case GameOperationKind.TravelMeeting:
                    if (operation.Destination == null) throw new InvalidOperationException("Travel destination omitted.");
                    character.Location = "meeting";
                    character.CurrentWorldId = operation.Destination.WorldId;
                    break;
            }
            var result = Result(character, operation, moved);
            if (TransformResult != null) result = TransformResult(command, result);
            Deliver(owner, new()
            {
                CommandId = command.Id, Success = result.Success, Observation = result,
                Quantity = moved, Error = result.Error, Ambiguous = result.MovementUncertain,
            });
            return;
        }

        if (loggedIn.GetValueOrDefault(owner) != character.Id) throw new InvalidOperationException("Trade ran on the wrong active character.");
        if (command.Kind == WorkerCommandKind.PreviewBatch)
        {
            var quantity = Math.Min(command.Operation.Quantity, Math.Min(stock.Bags, checked(stock.StackSize * 5)));
            Deliver(owner, new() { CommandId = command.Id, Success = quantity != 0, Quantity = quantity });
            return;
        }
        if (character.Location != "meeting") throw new InvalidOperationException("Trade participant did not return to the meeting point.");
        tradeBeforeQuantities[command.Id] = stock.Bags;
        if (command.Kind == WorkerCommandKind.ReceiveBatch)
        {
            if (armedReceiver != null) throw new InvalidOperationException("A receiver was already armed.");
            armedReceiver = Clone(command); armedBefore = stock.Bags;
            AcceptResponse(owner, new() { CommandId = command.Id, Ready = true });
            return;
        }

        var arm = armedReceiver ?? throw new InvalidOperationException("The sender started without an armed receiver.");
        var receiver = characters[arm.Operation.CharacterId];
        var receiverStock = receiver.Inventory(arm.Operation.ItemId, arm.Operation.HighQuality);
        if (RejectNextSenderBeforeStart)
        {
            RejectNextSenderBeforeStart = false;
            // Starting the receiver was durable, but its interruption still needs operator review.
            Deliver(receiver.PeerId, new()
            {
                CommandId = arm.Id, Ambiguous = true, Unchanged = receiverStock.Bags == armedBefore,
                BeforeQuantity = armedBefore, CurrentQuantity = receiverStock.Bags,
                Error = "Receiver stopped without a verified trade result.",
            });
            Deliver(owner, new()
            {
                CommandId = command.Id, NeverStarted = true,
                Error = "Sender rejected the command before starting the game transaction.",
            });
            armedReceiver = null;
            return;
        }
        var amount = command.Operation.Quantity;
        if (arm.Operation.Quantity != amount || arm.Partner?.ContentId != character.Id
            || arm.Operation.ItemId != command.Operation.ItemId || arm.Operation.HighQuality != command.Operation.HighQuality
            || command.Partner?.ContentId != receiver.Id || loggedIn.GetValueOrDefault(receiver.PeerId) != receiver.Id
            || receiver.Location != "meeting" || receiver.CurrentWorldId != character.CurrentWorldId
            || stock.Bags < amount || receiver.BagSpace < amount)
            throw new InvalidOperationException("The proposed trade cannot fit or does not match the armed partner.");
        if (DeferTradeCompletion) { deferredSender = Clone(command); return; }
        stock.Bags -= amount; receiverStock.Bags += amount;
        Trades.Add(new(character.Id, receiver.Id, amount, command.Id, arm.Id, command.Operation.ItemId, command.Operation.HighQuality, character.CurrentWorldId));
        Deliver(receiver.PeerId, new() { CommandId = arm.Id, Success = true, Quantity = amount });
        Deliver(owner, new() { CommandId = command.Id, Success = true, Quantity = amount });
        armedReceiver = null;
    }

    private void HandleQuery(Guid id, Guid owner)
    {
        if (commandOwners.TryGetValue(id, out var expected) && owner != expected)
            throw new InvalidOperationException("Query sent to the wrong worker.");
        if (durableResponses.TryGetValue(id, out var response))
        {
            var command = receivedCommands[id];
            if (command.Kind is WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch
                && response is not ({ Success: true } or { OperatorReviewed: true } or { NeverStarted: true })
                && tradeBeforeQuantities.TryGetValue(id, out var before)
                && loggedIn.GetValueOrDefault(owner) == command.Operation.CharacterId)
            {
                response = TradeReconciliationEvidence.Capture(command, response, before,
                    characters[command.Operation.CharacterId].Inventory(command.Operation.ItemId, command.Operation.HighQuality).Bags);
                durableResponses[id] = Clone(response);
            }
            AcceptResponse(owner, Clone(response));
        }
        else if (armedReceiver?.Id == id) AcceptResponse(owner, new() { CommandId = id, Ready = true });
        else AcceptResponse(owner, new() { CommandId = id, Ambiguous = true, Error = "Unknown operation." });
    }

    private void ResolveWorker(ManualResolution resolution, Guid owner)
    {
        var command = receivedCommands[resolution.CommandId];
        var previous = durableResponses[resolution.CommandId];
        if (resolution.StorageVerifiedOnly)
        {
            if (resolution.Transferred || !StorageReconciliation.IsStorage(command))
                throw new InvalidOperationException("This command cannot be reviewed as storage.");
            if (StorageReconciliation.IsReviewed(previous)) { Deliver(owner, previous); return; }
            if (loggedIn.GetValueOrDefault(owner) != command.Operation.CharacterId)
                throw new InvalidOperationException("The original storage character must be logged in.");
            if (!StorageMenusClosed) throw new InvalidOperationException("Close storage and quantity menus before review.");
            var storageQuantity = characters[command.Operation.CharacterId].Inventory(command.Operation.ItemId, command.Operation.HighQuality).Bags;
            Deliver(owner, StorageReconciliation.Review(command, previous, storageQuantity));
            return;
        }
        var before = previous.BeforeQuantity ?? throw new InvalidOperationException("No pre-trade evidence.");
        var current = characters[command.Operation.CharacterId].Inventory(command.Operation.ItemId, command.Operation.HighQuality).Bags;
        if (!resolution.Transferred && before != current) throw new InvalidOperationException("The inventory is not unchanged.");
        Deliver(owner, new()
        {
            CommandId = resolution.CommandId, Success = resolution.Transferred, OperatorReviewed = true,
            Unchanged = !resolution.Transferred, Quantity = resolution.Transferred ? command.Operation.Quantity : 0,
            BeforeQuantity = before, CurrentQuantity = current,
        });
    }

    private void Deliver(Guid owner, WorkerResponse response)
    {
        durableResponses[response.CommandId] = Clone(response);
        if (SuppressInitialResponse?.Invoke(receivedCommands[response.CommandId]) == true) return;
        AcceptResponse(owner, response);
    }

    private static GameOperationResult Result(SimulatedCharacter character, GameOperation operation, uint moved)
    {
        var stock = character.Inventory(operation.ItemId, operation.HighQuality);
        if (operation.Kind == GameOperationKind.ReadCachedInventory)
            return new()
            {
                Id = operation.Id, Success = true, CharacterId = character.Id,
                CachedInventoryAvailable = character.CacheAvailable, RetainerRosterKnown = character.CacheAvailable,
                CacheRetrievedAt = character.CacheAvailable ? DateTimeOffset.UtcNow : null,
                BagQuantity = stock.Bags, BagCapacity = character.BagSpace, StackSize = stock.StackSize,
                Retainers = stock.Retainers.Select(pair => new RetainerInventorySnapshot(pair.Key, $"Retainer {pair.Key}", pair.Value, false)).ToList(),
                Error = character.CacheAvailable ? string.Empty : "No complete cached inventory is available.",
            };
        return new()
        {
            Id = operation.Id, Success = true, CharacterId = character.Id, BagQuantity = stock.Bags,
            CurrentWorldId = character.CurrentWorldId,
            BagCapacity = character.BagSpace, StackSize = stock.StackSize, MovedQuantity = moved,
            RetainerRosterVerified = character.ScanVerified,
            Retainers = stock.Retainers.Select(pair => new RetainerInventorySnapshot(pair.Key, $"Retainer {pair.Key}", pair.Value, character.ScanVerified)).ToList(),
            ObservedAt = DateTimeOffset.UtcNow,
        };
    }

    private static void RemoveStored(SimulatedItemInventory character, uint amount)
    {
        foreach (var id in character.Retainers.Keys.ToArray())
        {
            var take = Math.Min(amount, character.Retainers[id]);
            character.Retainers[id] -= take; amount -= take;
            if (amount == 0) return;
        }
        if (amount != 0) throw new InvalidOperationException("Withdrawal overdrew stored inventory.");
    }

    private static CharacterProfile Profile(SimulatedCharacter character) => new()
    {
        ContentId = character.Id, Name = $"Character {character.Id}", World = character.WorldName,
        InventoryFreeSlots = Math.Min(140U, character.BagSpace), InventorySlotsRetrievedAt = DateTimeOffset.UtcNow
    };

    private sealed class HarnessPlugin
    {
        public HarnessConfig Config { get; } = new();
        public HarnessTrader Trader { get; } = new();
        public HarnessFuel Fuel { get; } = new();
    }
    private sealed class HarnessConfig { public List<CharacterProfile> Characters { get; set; } = []; }
    private sealed class HarnessTrader { public bool IsRunning => false; }
    private sealed class HarnessFuel { public bool Active => false; }
    private sealed class HarnessExecutor { public bool IsBusy => false; }
    private sealed class HarnessPeer { public Guid? RemotePeerId { get; init; } }
}

public sealed class SimulatedCharacter : SimulatedItemInventory
{
    public required ulong Id { get; init; }
    public required Guid PeerId { get; init; }
    public uint HomeWorldId { get; init; } = 1;
    public string WorldName { get; init; } = "Home World";
    public uint CurrentWorldId { get; set; }
    public uint BagLimit { get; init; } = 999;
    public uint StorageLimit { get; init; } = 1_000_000;
    public bool ScanVerified { get; set; } = true;
    public bool CacheAvailable { get; set; } = true;
    public string Location { get; set; } = "home";
    public Dictionary<ItemKey, SimulatedItemInventory> OtherItems { get; } = [];
    public SimulatedItemInventory Inventory(uint itemId, bool highQuality = false)
    {
        if (itemId == 10155 && !highQuality) return this;
        var key = new ItemKey(itemId, highQuality);
        if (!OtherItems.TryGetValue(key, out var stock))
        {
            stock = new() { Retainers = Retainers.Keys.ToDictionary(id => id, _ => 0U) };
            OtherItems[key] = stock;
        }
        return stock;
    }
    // A shared quantity-capacity ceiling models competing item occupancy for coordinator tests.
    public uint BagSpace => (uint)Math.Max(0L, (long)BagLimit - Bags - OtherItems.Values.Sum(item => (long)item.Bags));
    public uint StorageSpace => (uint)Math.Max(0L, (long)StorageLimit - RetainerTotal - OtherItems.Values.Sum(item => (long)item.RetainerTotal));
}

public class SimulatedItemInventory
{
    public uint Bags { get; set; }
    public uint StackSize { get; init; } = 99;
    public Dictionary<ulong, uint> Retainers { get; init; } = [];
    public uint RetainerTotal => checked((uint)Retainers.Values.Aggregate(0UL, (sum, value) => sum + value));
    public ulong Total => (ulong)Bags + RetainerTotal;
}

public sealed record SimulatedCommandTrace(WorkerCommandKind Kind, GameOperationKind Operation, ulong CharacterId, uint Quantity, string StartLocation, uint ItemId, bool HighQuality, uint DestinationWorldId = 0, uint StartWorldId = 0);
public sealed record SimulatedTrade(ulong SenderId, ulong ReceiverId, uint Quantity, Guid SenderCommandId, Guid ReceiverCommandId, uint ItemId, bool HighQuality, uint WorldId = 0);
