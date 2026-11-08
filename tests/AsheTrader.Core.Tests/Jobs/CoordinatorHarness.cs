using AsheTrader.Core;
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
    private WorkerCommand? armedReceiver;
    private uint armedBefore;
    private readonly bool host = true;
    private readonly bool journalHealthy = true;
    private readonly object? activeWorker = null;
    private string error = string.Empty;
    private WorkflowSettings Settings => settings;

    public CoordinatorRun? Run { get; private set; }
    public PeerRoster? RemoteRoster { get; private set; }
    public bool Connected { get; set; } = true;
    public string LastError => error;
    public string SavedJson { get; private set; } = "null";
    public List<SimulatedCommandTrace> Trace { get; } = [];
    public List<SimulatedTrade> Trades { get; } = [];
    public IReadOnlyDictionary<ulong, SimulatedCharacter> Characters => characters;
    public Func<WorkerCommand, GameOperationResult, GameOperationResult>? TransformResult { get; set; }
    public uint? DepositMovementLimit { get; set; }
    public bool RejectNextSenderBeforeStart { get; set; }
    // The worker still persists the result. A subsequent query can recover a lost response.
    public Predicate<WorkerCommand>? SuppressInitialResponse { get; set; }

    public WorkflowService(Guid localPeer, Guid remotePeer, params SimulatedCharacter[] initialCharacters)
    {
        settings = new() { PeerId = localPeer, AllowRemoteControl = true };
        peer = new() { RemotePeerId = remotePeer };
        characters = initialCharacters.ToDictionary(character => character.Id);
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
            durableResponses[armedReceiver.Id] = new()
            {
                CommandId = armedReceiver.Id, Ambiguous = true,
                Unchanged = character.Bags == armedBefore, BeforeQuantity = armedBefore,
                CurrentQuantity = character.Bags, Error = "Receiver interrupted before a verified result.",
            };
            armedReceiver = null;
        }
        SaveRun();
    }

    public void PauseForTest(string reason = "Test pause") => Pause(reason);

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
        if (character.PeerId != owner) throw new InvalidOperationException("Command went to the wrong peer.");
        Trace.Add(new(command.Kind, command.Operation.Kind, character.Id, command.Operation.Quantity, character.Location));

        if (command.Kind == WorkerCommandKind.Game)
        {
            var operation = command.Operation;
            if (operation.Kind == GameOperationKind.Login) loggedIn[owner] = character.Id;
            else if (loggedIn.GetValueOrDefault(owner) != character.Id) throw new InvalidOperationException("Operation ran on the wrong active character.");
            uint moved = 0;
            switch (operation.Kind)
            {
                case GameOperationKind.ScanRetainers: character.Location = "home"; break;
                case GameOperationKind.Withdraw:
                    character.Location = "home";
                    moved = Math.Min(operation.Quantity, Math.Min(character.RetainerTotal, character.BagSpace));
                    RemoveStored(character, moved); character.Bags += moved;
                    break;
                case GameOperationKind.Deposit:
                    character.Location = "home";
                    moved = Math.Min(operation.Quantity, Math.Min(character.Bags, character.StorageSpace));
                    if (DepositMovementLimit is { } limit) moved = Math.Min(moved, limit);
                    character.Bags -= moved;
                    var firstRetainer = character.Retainers.Keys.FirstOrDefault();
                    if (firstRetainer == 0 && moved != 0) throw new InvalidOperationException("No retainer to deposit into.");
                    if (moved != 0) character.Retainers[firstRetainer] += moved;
                    break;
                case GameOperationKind.TravelMeeting:
                    if (operation.Destination == null) throw new InvalidOperationException("Travel destination omitted.");
                    character.Location = "meeting";
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
            var quantity = Math.Min(command.Operation.Quantity, Math.Min(character.Bags, checked(character.StackSize * 5)));
            Deliver(owner, new() { CommandId = command.Id, Success = quantity != 0, Quantity = quantity });
            return;
        }
        if (character.Location != "meeting") throw new InvalidOperationException("Trade participant did not return to the meeting point.");
        if (command.Kind == WorkerCommandKind.ReceiveBatch)
        {
            if (armedReceiver != null) throw new InvalidOperationException("A receiver was already armed.");
            armedReceiver = Clone(command); armedBefore = character.Bags;
            AcceptResponse(owner, new() { CommandId = command.Id, Ready = true });
            return;
        }

        var arm = armedReceiver ?? throw new InvalidOperationException("The sender started without an armed receiver.");
        var receiver = characters[arm.Operation.CharacterId];
        if (RejectNextSenderBeforeStart)
        {
            RejectNextSenderBeforeStart = false;
            // Starting the receiver was durable, but its interruption still needs operator review.
            Deliver(receiver.PeerId, new()
            {
                CommandId = arm.Id, Ambiguous = true, Unchanged = receiver.Bags == armedBefore,
                BeforeQuantity = armedBefore, CurrentQuantity = receiver.Bags,
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
            || command.Partner?.ContentId != receiver.Id || loggedIn.GetValueOrDefault(receiver.PeerId) != receiver.Id
            || receiver.Location != "meeting" || character.Bags < amount || receiver.BagSpace < amount)
            throw new InvalidOperationException("The proposed trade cannot fit or does not match the armed partner.");
        character.Bags -= amount; receiver.Bags += amount;
        Trades.Add(new(character.Id, receiver.Id, amount, command.Id, arm.Id));
        Deliver(receiver.PeerId, new() { CommandId = arm.Id, Success = true, Quantity = amount });
        Deliver(owner, new() { CommandId = command.Id, Success = true, Quantity = amount });
        armedReceiver = null;
    }

    private void HandleQuery(Guid id, Guid owner)
    {
        if (commandOwners.TryGetValue(id, out var expected) && owner != expected)
            throw new InvalidOperationException("Query sent to the wrong worker.");
        if (durableResponses.TryGetValue(id, out var response)) AcceptResponse(owner, Clone(response));
        else if (armedReceiver?.Id == id) AcceptResponse(owner, new() { CommandId = id, Ready = true });
        else AcceptResponse(owner, new() { CommandId = id, Ambiguous = true, Error = "Unknown operation." });
    }

    private void ResolveWorker(ManualResolution resolution, Guid owner)
    {
        var command = receivedCommands[resolution.CommandId];
        var previous = durableResponses[resolution.CommandId];
        var before = previous.BeforeQuantity ?? throw new InvalidOperationException("No pre-trade evidence.");
        var current = characters[command.Operation.CharacterId].Bags;
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

    private static GameOperationResult Result(SimulatedCharacter character, GameOperation operation, uint moved) => new()
    {
        Id = operation.Id, Success = true, CharacterId = character.Id, BagQuantity = character.Bags,
        BagCapacity = character.BagSpace, StackSize = character.StackSize, MovedQuantity = moved,
        RetainerRosterVerified = character.ScanVerified,
        Retainers = character.Retainers.Select(pair => new RetainerInventorySnapshot(pair.Key, $"Retainer {pair.Key}", pair.Value, character.ScanVerified)).ToList(),
        ObservedAt = DateTimeOffset.UtcNow,
    };

    private static void RemoveStored(SimulatedCharacter character, uint amount)
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
    { ContentId = character.Id, Name = $"Character {character.Id}", World = "Home World" };

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

public sealed class SimulatedCharacter
{
    public required ulong Id { get; init; }
    public required Guid PeerId { get; init; }
    public uint Bags { get; set; }
    public uint BagLimit { get; init; } = 999;
    public uint StackSize { get; init; } = 99;
    public uint StorageLimit { get; init; } = 1_000_000;
    public bool ScanVerified { get; set; } = true;
    public Dictionary<ulong, uint> Retainers { get; init; } = [];
    public string Location { get; set; } = "home";
    public uint RetainerTotal => checked((uint)Retainers.Values.Aggregate(0UL, (sum, value) => sum + value));
    public uint BagSpace => BagLimit - Bags;
    public uint StorageSpace => StorageLimit >= RetainerTotal ? StorageLimit - RetainerTotal : 0;
    public ulong Total => (ulong)Bags + RetainerTotal;
}

public sealed record SimulatedCommandTrace(WorkerCommandKind Kind, GameOperationKind Operation, ulong CharacterId, uint Quantity, string StartLocation);
public sealed record SimulatedTrade(ulong SenderId, ulong ReceiverId, uint Quantity, Guid SenderCommandId, Guid ReceiverCommandId);
