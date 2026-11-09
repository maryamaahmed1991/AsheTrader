using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private long resendAt;

    public void StartJob(SavedTransferJob job) => Start(job, false);
    public void ScanJob(SavedTransferJob job) => Start(job, true);

    private void Start(SavedTransferJob job, bool discoveryOnly)
    {
        RequireIdle();
        RequirePair();
        if (Run is { Step: not CoordinatorStep.Finished }) throw new InvalidOperationException("Resume or end the existing paused job first.");
        ArgumentNullException.ThrowIfNull(job);
        if (job.Definition is null || job.AdditionalItems is null || job.AdditionalItems.Any(item => item is null))
            throw new ArgumentException("Every job item needs a complete transfer definition.", nameof(job));
        var saved = Clone(job);
        var definitions = saved.ItemDefinitions.ToArray();
        foreach (var definition in definitions)
        {
            // Relay preferences from older templates do not opt characters into a new run.
            // Existing in-flight journals keep their already assigned routes until resolved.
            definition.RelayCharacters = [];
            JobPlanner.ValidateDefinition(definition);
            foreach (var id in DiscoveryCharacters(definition)) _ = Character(id);
        }
        if (definitions.Select(item => new ItemKey(item.ItemId, item.HighQuality)).Distinct().Count() != definitions.Length)
            throw new ArgumentException("Add each item and quality only once per job; combine its recipient quantities on that item line.", nameof(job));
        if (definitions.Select(item => item.Id).Distinct().Count() != definitions.Length)
            throw new ArgumentException("Each job item needs its own stable ID. Remove and re-add the duplicate item line.", nameof(job));
        if (!discoveryOnly && (job.Meeting.WorldId == 0 || job.Meeting.TerritoryId == 0 || job.Meeting.AetheryteId == 0))
            throw new InvalidOperationException("Capture an outdoor meeting point and choose its main aetheryte before starting.");
        Run = new() { Job = saved, DiscoveryOnly = discoveryOnly, DiscoveryOrder = DiscoveryCharacters(saved.Definition), Step = CoordinatorStep.DiscoverCache,
            Status = "Reading Allagan Tools inventories from both PCs…" };
        error = "";
        SaveRun();
    }

    private static List<ulong> DiscoveryCharacters(TransferJobDefinition definition) =>
        definition.Sources.Concat(definition.Recipients.Select(recipient => recipient.CharacterId)).Distinct().ToList();

    private void CompleteItem()
    {
        var run = Run!;
        if (run.Pending.Count != 0 || run.TradeOperationId.HasValue)
            throw new InvalidOperationException("Resolve the current item's pending operation before proceeding to another item.");
        if (run.Journal != null)
        {
            var ledger = new TransferJobLedger(run.Journal);
            if (ledger.HasPendingOperation || run.Journal.Plan.Allocations.Any(allocation => allocation.Legs.Count == 2
                && ledger.RelayCargo(allocation.Legs[0].ReceiverId) != 0))
                throw new InvalidOperationException("Resolve the current item's relay cargo and trade evidence before proceeding to another item.");
            run.Journal.State = run.Journal.Plan.HasShortage ? TransferJobState.Paused : TransferJobState.Completed;
        }
        if (run.CompletedItems.Any(item => item.ItemIndex == run.ActiveItemIndex))
            throw new InvalidDataException("This item already has a completed checkpoint; inspect the saved job before continuing.");
        var completed = new List<CoordinatorItemResult>(run.CompletedItems)
        {
            new()
            {
                ItemIndex = run.ActiveItemIndex, Definition = Clone(run.ActiveDefinition), DiscoveryOnly = run.DiscoveryOnly,
                Journal = run.Journal == null ? null : Clone(run.Journal), Snapshots = Clone(run.Snapshots),
                Withdrawn = new(run.Withdrawn), Deposited = new(run.Deposited), AutomaticRelays = new(run.AutomaticRelays)
            }
        };
        var nextIndex = run.ActiveItemIndex + 1;
        if (nextIndex < run.TotalItemCount)
        {
            var order = DiscoveryCharacters(run.Job.ItemDefinitions.ElementAt(nextIndex));
            foreach (var id in order) _ = Character(id);
            // One durable checkpoint switches item identity and clears every item-specific counter
            // before any command for the next item can be issued.
            Run = new()
            {
                Job = run.Job, DiscoveryOnly = run.DiscoveryOnly, ActiveItemIndex = nextIndex,
                CompletedItems = completed, DiscoveryOrder = order, Step = CoordinatorStep.DiscoverCache,
                Status = $"Item {nextIndex + 1}/{run.TotalItemCount}: reading cached inventories before continuing…"
            };
            resendAt = 0;
        }
        else
        {
            run.CompletedItems = completed;
            run.Step = CoordinatorStep.Finished;
            run.Paused = run.HasShortages;
            run.Status = run.DiscoveryOnly
                ? "Discovery complete. All selected items were read from complete caches or live scans; no items were moved."
                : run.HasShortages
                    ? "All job items processed. Some requested quantities remain short; see each item's unmet targets."
                    : "Transfer job complete: all items' planned final deliveries are verified.";
        }
        SaveRun();
    }

    public void Resume()
    {
        RequirePair();
        if (!journalHealthy) throw new InvalidOperationException("Repair the saved journal before resuming.");
        if (activeWorker != null || executor.IsBusy || plugin.Trader.IsRunning || plugin.Fuel.Active)
            throw new InvalidOperationException("Wait for the local operation to stop before resuming.");
        if (Run is not { Paused: true, Step: not CoordinatorStep.Finished }) return;
        error = "";
        if (Run.Journal == null)
        {
            // Pre-plan selections have not carried cargo and may be re-ranked after a pause.
            // A created journal keeps its original routes pinned for durable trade accounting.
            Run.AutomaticRelays.Clear(); Run.RejectedRelayCharacters.Clear();
            Run.RelayCandidateId = null; Run.RelayCandidatePeerId = null;
        }
        if (Run.TradeOperationId.HasValue)
        {
            var ledger = new TransferJobLedger(Run.Journal!);
            var operation = Run.Journal!.Operations.Single(x => x.Id == Run.TradeOperationId);
            if (operation.State == TransferOperationState.Prepared && Run.Pending.Count == 0)
            {
                ledger.CancelBeforeIssue(operation.Id);
                Run.TradeOperationId = null;
                Run.Step = CoordinatorStep.SelectLeg;
            }
            else
            {
                // Query durable outcomes. Never prepare/reissue an ambiguous game transaction.
                foreach (var pending in Run.Pending) { pending.Response = null; pending.Ready = false; }
                Run.Step = CoordinatorStep.AwaitTrade;
            }
        }
        else
        {
            var storage = Run.Pending.SingleOrDefault(x => x.Command.Kind == WorkerCommandKind.Game
                && x.Command.Operation.Kind is GameOperationKind.Deposit or GameOperationKind.Withdraw);
            if (storage != null && storage.Response?.NeverStarted != true
                && (storage.Response?.Observation == null || storage.Response.Ambiguous))
            {
                // A pickup/deposit may have finished before its result reached this PC.
                // Recover the original command ID rather than moving another load blindly.
                storage.Response = null;
                if (storage.InventoryItemIndex.HasValue)
                {
                    Run.PreviousItemDepositIndex = storage.InventoryItemIndex;
                    Run.PreviousItemDepositCharacterId = storage.Command.Operation.CharacterId;
                    Run.Step = CoordinatorStep.DepositPreviousItem;
                }
                else Run.Step = storage.Command.Operation.Kind == GameOperationKind.Deposit
                        ? CoordinatorStep.Deposit : CoordinatorStep.Withdraw;
            }
            else
            {
                if (storage != null) AccountStorageMovement(storage, storage.Command.Operation.CharacterId);
                Run.Pending.Clear();
                Run.Step = Run.Journal == null ? CoordinatorStep.DiscoverCache : CoordinatorStep.SelectLeg;
            }
        }
        Run.Paused = false;
        Run.Status = "Resuming from saved progress…";
        resendAt = 0;
        SaveRun();
    }

    public void EndPausedJob()
    {
        if (Run is not { Paused: true }) return;
        if (activeWorker != null || executor.IsBusy || plugin.Trader.IsRunning) throw new InvalidOperationException("Wait until local work stops.");
        if (Run.Journal != null)
        {
            var ledger = new TransferJobLedger(Run.Journal);
            if (ledger.HasPendingOperation) throw new InvalidOperationException("Reconcile the pending trade before ending the job.");
            if (Run.Journal.Plan.Allocations.Where(x => x.Legs.Count == 2).Any(x => ledger.RelayCargo(x.Legs[0].ReceiverId) > 0))
                throw new InvalidOperationException("A relay still holds undelivered cargo. Resume delivery before ending this job.");
            ledger.Pause("Job ended by user before all requested quantities were delivered.");
        }
        Run.Step = CoordinatorStep.Finished;
        Run.Status = "Job ended by user. Confirmed progress is preserved in the journal.";
        error = "";
        SaveRun();
    }

    private void RequirePair()
    {
        if (!host || !Connected || RemoteRoster == null) throw new InvalidOperationException("Start this job on the coordinator after both PCs are paired and their rosters are loaded.");
        if (!RemoteRoster.ControlEnabled) throw new InvalidOperationException("Enable paired workflow control on the worker PC and refresh its roster.");
        if (plugin.Config.Characters.Select(x => x.ContentId).Intersect(RemoteRoster.Characters.Select(x => x.ContentId)).Any())
            throw new InvalidOperationException("The same character appears on both PCs. Assign each game account's roster to only its own PC.");
    }

    private (Guid PeerId, CharacterProfile Character) Character(ulong id) => AllCharacters().FirstOrDefault(x => x.Character.ContentId == id)
        is var entry && entry.Character != null ? entry : throw new InvalidOperationException($"Character {id} is missing from the paired rosters.");

    private static T Clone<T>(T value) => Decode<T>(Newtonsoft.Json.JsonConvert.SerializeObject(value));

    private WorkerCommand Command(ulong characterId, GameOperationKind kind, uint quantity = 0, TravelLocation? destination = null,
        WorkerCommandKind workerKind = WorkerCommandKind.Game, ulong? partner = null, TransferJobDefinition? item = null)
    {
        var identity = Character(characterId).Character;
        var command = new WorkerCommand
        {
            Id = Guid.NewGuid(),
            JobId = Run!.Job.Definition.Id, Kind = workerKind,
            Operation = new GameOperation
            {
                Kind = kind, CharacterId = characterId, CharacterName = identity.Name, World = identity.World,
                ItemId = (item ?? Run.ActiveDefinition).ItemId, HighQuality = (item ?? Run.ActiveDefinition).HighQuality,
                Quantity = quantity, Destination = destination == null ? null : Clone(destination)
            },
            Partner = partner.HasValue ? Clone(Character(partner.Value).Character) : null
        };
        command.Operation.Id = command.Id;
        return command;
    }

    private void Issue(Guid peerId, WorkerCommand command, bool marksTrade = false, int? inventoryItemIndex = null)
    {
        Run!.Pending.Add(new() { PeerId = peerId, Command = command, InventoryItemIndex = inventoryItemIndex });
        if (marksTrade) new TransferJobLedger(Run.Journal!).MarkIssued(Run.TradeOperationId!.Value);
        SaveRun();
        Dispatch(peerId, command);
        resendAt = Environment.TickCount64 + 5000;
    }

    private void Dispatch(Guid peerId, WorkerCommand command)
    {
        if (peerId == Settings.PeerId) HandleWorker(command, peerId);
        else
        {
            if (!Connected || peerId != peer.RemotePeerId) throw new InvalidOperationException("The assigned worker is disconnected.");
            Send("command", command);
        }
    }

    private void Query(PendingCommand pending)
    {
        if (pending.PeerId == Settings.PeerId) HandleQuery(pending.Command.Id, pending.PeerId);
        else Send("query", pending.Command.Id);
    }

    private void AcceptResponse(Guid sourcePeer, WorkerResponse response)
    {
        var pending = Run?.Pending.FirstOrDefault(x => x.Command.Id == response.CommandId && x.PeerId == sourcePeer);
        if (pending == null) return;
        if (response.Ready) pending.Ready = true;
        else
        {
            if (pending.Response is { Success: true } old && (!response.Success || response.Quantity != old.Quantity))
                throw new InvalidDataException("Conflicting results arrived for the same operation.");
            pending.Response = response;
        }
        SaveRun();
    }

    private bool Perform(ulong characterId, GameOperationKind kind, CoordinatorStep next, Action<GameOperationResult>? apply = null,
        uint quantity = 0, TravelLocation? destination = null, int? inventoryItemIndex = null)
    {
        if (Run!.Pending.Count == 0)
        {
            var definition = inventoryItemIndex.HasValue
                ? Run.CompletedItems.Single(item => item.ItemIndex == inventoryItemIndex.Value).Definition : null;
            Issue(Character(characterId).PeerId, Command(characterId, kind, quantity, destination, item: definition), inventoryItemIndex: inventoryItemIndex);
            return false;
        }
        var pending = Run.Pending.Single();
        if (pending.Response == null) return false;
        if (kind is GameOperationKind.Deposit or GameOperationKind.Withdraw)
        {
            AccountStorageMovement(pending, characterId);
            if (pending.Response.Ambiguous)
            {
                var action = kind == GameOperationKind.Deposit ? "deposit" : "pickup";
                throw new InvalidOperationException($"A retainer {action} has an uncertain outcome. Inspect bags and retainers before ending this job and creating a freshly scanned job for the remaining quantity; the {action} will not be retried.");
            }
        }
        if (!pending.Response.Success) throw new InvalidOperationException(pending.Response.Error);
        var result = pending.Response.Observation ?? throw new InvalidDataException("Worker omitted its inventory observation.");
        if (result.CharacterId != characterId) throw new InvalidDataException("Worker observed a different character.");
        apply?.Invoke(result);
        Run.Pending.Clear();
        SetStep(next);
        return true;
    }

    private void AccountStorageMovement(PendingCommand pending, ulong characterId)
    {
        if (!pending.InventoryAdjustmentApplied && pending.Response?.Observation is { } result)
        {
            var pickup = pending.Command.Operation.Kind == GameOperationKind.Withdraw;
            if (result.CharacterId != characterId || result.MovedQuantity > pending.Command.Operation.Quantity)
                throw new InvalidDataException(pickup ? "Invalid provider pickup observation." : "Invalid deposit observation.");
            Dictionary<ulong, ulong> quantities;
            if (pending.InventoryItemIndex is { } itemIndex)
            {
                var previous = Run!.CompletedItems.Single(item => item.ItemIndex == itemIndex);
                if (pickup || pending.Command.Operation.ItemId != previous.Definition.ItemId
                    || pending.Command.Operation.HighQuality != previous.Definition.HighQuality || previous.Journal == null)
                    throw new InvalidDataException("The stored item does not match its completed job-item ledger.");
                if (result.MovedQuantity > DepositAllowance(previous.Journal, previous.Deposited, characterId))
                    throw new InvalidDataException("A previous-item deposit exceeded its remaining received-item allowance.");
                quantities = previous.Deposited;
            }
            else quantities = pickup ? Run!.Withdrawn : Run!.Deposited;
            quantities[characterId] = checked(quantities.GetValueOrDefault(characterId) + result.MovedQuantity);
            pending.InventoryAdjustmentApplied = true;
            SaveRun();
        }
    }

    private static ulong DepositAllowance(TransferJobJournal journal, Dictionary<ulong, ulong> deposited, ulong characterId)
    {
        var received = journal.Operations.Where(operation => operation.State == TransferOperationState.Committed)
            .Where(operation => journal.Plan.Allocations.Single(allocation => allocation.Id == operation.AllocationId).Legs[operation.LegIndex].ReceiverId == characterId)
            .Aggregate(0UL, (sum, operation) => checked(sum + operation.Quantity));
        var forwarded = journal.Operations.Where(operation => operation.State == TransferOperationState.Committed && operation.LegIndex > 0)
            .Where(operation => journal.Plan.Allocations.Single(allocation => allocation.Id == operation.AllocationId).Legs[operation.LegIndex].SenderId == characterId)
            .Aggregate(0UL, (sum, operation) => checked(sum + operation.Quantity));
        var stored = deposited.GetValueOrDefault(characterId);
        return forwarded <= received && stored <= received - forwarded ? received - forwarded - stored : 0;
    }

    private bool TryPreparePreviousItemDeposit(ulong characterId)
    {
        var run = Run!;
        foreach (var item in run.CompletedItems)
        {
            if (item.Journal == null) continue;
            var allowance = DepositAllowance(item.Journal, item.Deposited, characterId);
            if (allowance == 0) continue;
            run.PreviousItemDepositIndex = item.ItemIndex;
            run.PreviousItemDepositCharacterId = characterId;
            run.DesiredQuantity = (uint)Math.Min(uint.MaxValue, allowance);
            return true;
        }
        return false;
    }

    private void SetStep(CoordinatorStep step)
    {
        Run!.Step = step;
        Run.Status = step switch
        {
            CoordinatorStep.DiscoverCache => $"Discovery {Run.DiscoveryIndex + 1}/{Run.DiscoveryOrder.Count}: reading Allagan Tools cached bags and retainers…",
            CoordinatorStep.DiscoverLogin => $"Discovery {Run.DiscoveryIndex + 1}/{Run.DiscoveryOrder.Count}: switching character for a live inventory check…",
            CoordinatorStep.DiscoverScan => "Inspecting carried inventory and every retainer…",
            CoordinatorStep.Plan => "Allocating source stock in recipient priority order…",
            CoordinatorStep.Withdraw => $"Collecting up to {Math.Min(Run.DesiredQuantity, Run.SenderBagCapacity):N0} items from provider retainers; trading resumes after this load…",
            CoordinatorStep.Deposit => "Receiver is returning home and depositing job items into retainers…",
            CoordinatorStep.DepositPreviousItem => "Storing received items from an earlier job line to make room for the next item…",
            CoordinatorStep.RelayLogin => "Logging into the automatic relay candidate…",
            CoordinatorStep.RelayInspect => "Checking the automatic relay's live inventory capacity…",
            CoordinatorStep.TravelSender or CoordinatorStep.TravelReceiver => "Bringing both characters to the meeting point…",
            CoordinatorStep.AwaitTrade => "Waiting for both clients to verify the agreed trade…",
            _ => $"Transfer job: {step}"
        };
        if (step is CoordinatorStep.DiscoverLogin or CoordinatorStep.DiscoverScan
            && Run.DiscoveryIndex < Run.DiscoveryOrder.Count
            && Run.DiscoveryNotes.TryGetValue(Run.DiscoveryOrder[Run.DiscoveryIndex], out var discoveryNote))
            Run.Status = $"{discoveryNote} {Run.Status}";
        if (Run.TotalItemCount > 1) Run.Status = $"Item {Run.ActiveItemIndex + 1}/{Run.TotalItemCount}: {Run.Status}";
        SaveRun();
    }

    private void AdvanceCoordinator()
    {
        if (!host || !Connected || RemoteRoster == null) { Pause("Both paired clients must be connected."); return; }
        var run = Run!;
        if (run.Pending.Count > 0 && Environment.TickCount64 >= resendAt)
        {
            resendAt = Environment.TickCount64 + 5000;
            foreach (var pending in run.Pending.Where(x => x.Response == null).ToArray())
            {
                // A trade is queried after interruption; ordinary commands are idempotent by command ID.
                if (run.Step == CoordinatorStep.AwaitTrade) Query(pending);
                else Dispatch(pending.PeerId, pending.Command);
            }
        }
        if (AdvanceAutomaticRelayPreparation()) return;
        if (run.Step is CoordinatorStep.DiscoverCache or CoordinatorStep.DiscoverLogin or CoordinatorStep.DiscoverScan)
        {
            if (run.DiscoveryIndex >= run.DiscoveryOrder.Count) { SetStep(CoordinatorStep.Plan); return; }
            var id = run.DiscoveryOrder[run.DiscoveryIndex];
            if (run.Step == CoordinatorStep.DiscoverCache)
            {
                if (!run.DiscoveryOnly && run.ActiveDefinition.QuantityMode == JobQuantityMode.RestockToTotal
                    && run.ActiveDefinition.Recipients.Any(recipient => recipient.CharacterId == id))
                {
                    run.DiscoveryNotes[id] = "Checking the exact restock target: this recipient needs a current bag and retainer total.";
                    SetStep(CoordinatorStep.DiscoverLogin);
                    return;
                }
                string? fallback = null;
                if (Perform(id, GameOperationKind.ReadCachedInventory, CoordinatorStep.DiscoverCache, result =>
                {
                    if (!result.CachedInventoryAvailable || !result.RetainerRosterKnown || result.CacheRetrievedAt == null
                        || result.StackSize == 0 || result.Retainers.Any(retainer => retainer.RetainerId == 0)
                        || result.Retainers.Select(retainer => retainer.RetainerId).Distinct().Count() != result.Retainers.Count)
                    {
                        fallback = string.IsNullOrWhiteSpace(result.Error) ? "The bag or retainer cache is incomplete." : result.Error;
                        return;
                    }
                    var entry = Character(id);
                    run.Snapshots[id] = new()
                    {
                        CharacterId = id, PeerId = entry.PeerId, Name = entry.Character.Name, World = entry.Character.World,
                        BagQuantity = result.BagQuantity, BagCapacity = result.BagCapacity,
                        Verification = InventoryVerification.Cached, HasCompleteCachedInventory = true,
                        RetrievedAt = result.CacheRetrievedAt,
                        Retainers = result.Retainers.Select(retainer => new RetainerStock
                        {
                            RetainerId = retainer.RetainerId, Name = retainer.Name, Quantity = retainer.Quantity,
                            Verification = InventoryVerification.Cached,
                        }).ToList(),
                    };
                    run.DiscoveryNotes[id] = "Allagan Tools cache: complete recorded bags and retainers; quantities will be checked during pickup and trading.";
                    run.DiscoveryIndex++;
                }) && fallback != null)
                {
                    run.DiscoveryNotes[id] = fallback;
                    SetStep(CoordinatorStep.DiscoverLogin);
                }
            }
            else if (run.Step == CoordinatorStep.DiscoverLogin) Perform(id, GameOperationKind.Login, CoordinatorStep.DiscoverScan);
            else Perform(id, GameOperationKind.ScanRetainers, CoordinatorStep.DiscoverCache, result =>
            {
                if (!result.RetainerRosterVerified || result.Retainers.Any(x => !x.Verified))
                    throw new InvalidOperationException("This character's retainer discovery is incomplete.");
                var entry = Character(id);
                run.Snapshots[id] = new()
                {
                    CharacterId = id, PeerId = entry.PeerId, Name = entry.Character.Name, World = entry.Character.World,
                    BagQuantity = result.BagQuantity, BagCapacity = result.BagCapacity,
                    Verification = InventoryVerification.Verified, RetainerRosterVerified = true, ObservedAt = result.ObservedAt,
                    Retainers = result.Retainers.Select(x => new RetainerStock { RetainerId = x.RetainerId, Name = x.Name,
                        Quantity = x.Quantity, Verification = InventoryVerification.Verified, ObservedAt = result.ObservedAt }).ToList()
                };
                run.DiscoveryIndex++;
            });
            return;
        }
        if (run.Step == CoordinatorStep.Plan)
        {
            if (run.DiscoveryOnly)
            {
                CompleteItem(); return;
            }
            if (!PrepareAutomaticRelays()) return;
            run.Journal = TransferJobLedger.Create(JobPlanner.Plan(DefinitionWithAutomaticRelays(), run.Snapshots.Values)).Journal;
            SetStep(CoordinatorStep.SelectLeg);
            return;
        }
        var ledger = new TransferJobLedger(run.Journal ?? throw new InvalidDataException("Job plan is missing."));
        if (run.Step == CoordinatorStep.SelectLeg)
        {
            foreach (var allocation in run.Journal.Plan.Allocations)
            {
                var index = allocation.Legs.Count == 2 && ledger.RemainingForLeg(allocation.Id, 1) > 0 ? 1 : 0;
                if (ledger.RemainingForLeg(allocation.Id, index) == 0) continue;
                run.AllocationId = allocation.Id; run.LegIndex = index;
                run.DesiredQuantity = (uint)Math.Min(uint.MaxValue, ledger.RemainingForLeg(allocation.Id, index));
                SetStep(CoordinatorStep.LoginSender); return;
            }
            CompleteItem(); return;
        }
        var assignment = run.Journal.Plan.Allocations.Single(x => x.Id == run.AllocationId);
        var leg = assignment.Legs[run.LegIndex];
        switch (run.Step)
        {
            case CoordinatorStep.LoginSender: Perform(leg.SenderId, GameOperationKind.Login, CoordinatorStep.LoginReceiver); break;
            case CoordinatorStep.LoginReceiver: Perform(leg.ReceiverId, GameOperationKind.Login, CoordinatorStep.InspectSender); break;
            case CoordinatorStep.InspectSender:
                Perform(leg.SenderId, GameOperationKind.Inspect, CoordinatorStep.InspectReceiver, result =>
                {
                    run.PreviousItemDepositIndex = null;
                    run.PreviousItemDepositCharacterId = 0;
                    run.SenderBagQuantity = result.BagQuantity;
                    run.SenderBagCapacity = result.BagCapacity;
                    run.SenderStackSize = result.StackSize;
                    if (result.StackSize == 0) throw new InvalidDataException("Invalid item stack size.");
                });
                if (run.Step == CoordinatorStep.InspectReceiver && run.SenderBagQuantity == 0)
                {
                    // Relay cargo must already be carried. Its unrelated retainer stock is not a source.
                    if (run.LegIndex != 0)
                        throw new InvalidOperationException("The relay is missing its carried job items. Restore that cargo to its bags before resuming; unrelated retainer stock will not be collected.");
                    if (run.SenderBagCapacity == 0)
                    {
                        if (TryPreparePreviousItemDeposit(leg.SenderId)) { SetStep(CoordinatorStep.DepositPreviousItem); break; }
                        throw new InvalidOperationException("The provider has no room to collect its next load. Free bag space, then resume; confirmed deliveries are saved.");
                    }
                    SetStep(CoordinatorStep.Withdraw);
                }
                break;
            case CoordinatorStep.Withdraw:
                // Load a bagful, then trade it across as many batches as necessary before returning home.
                // Do not withdraw more than the selected recipient still needs from this provider.
                var pickupQuantity = (uint)Math.Min(ledger.RemainingForLeg(run.AllocationId, run.LegIndex),
                    Math.Min(run.DesiredQuantity, run.SenderBagCapacity));
                if (run.Pending.Count == 0 && (run.LegIndex != 0 || pickupQuantity == 0))
                    throw new InvalidOperationException("No verified provider pickup allowance remains. Resume to inspect bags and the remaining assignment again.");
                Perform(leg.SenderId, GameOperationKind.Withdraw, CoordinatorStep.InspectReceiver, result =>
                {
                    if (result.BagQuantity == 0 || result.MovedQuantity == 0)
                        throw new InvalidOperationException("No provider items could be collected. The planned stock may be stale or unavailable. Check its retainer stock and bag space before resuming; confirmed deliveries are preserved.");
                    if (result.StackSize == 0) throw new InvalidDataException("Invalid item stack size.");
                    run.SenderBagQuantity = result.BagQuantity; run.SenderBagCapacity = result.BagCapacity;
                    run.SenderStackSize = result.StackSize;
                }, pickupQuantity);
                break;
            case CoordinatorStep.InspectReceiver:
                Perform(leg.ReceiverId, GameOperationKind.Inspect, CoordinatorStep.TravelSender, result =>
                {
                    run.PreviousItemDepositIndex = null;
                    run.PreviousItemDepositCharacterId = 0;
                    run.ReceiverCapacity = result.BagCapacity;
                    if (result.BagCapacity == 0)
                    {
                        var eligible = DepositAllowance(run.Journal, run.Deposited, leg.ReceiverId);
                        if (result.BagQuantity == 0 || eligible == 0)
                        {
                            if (TryPreparePreviousItemDeposit(leg.ReceiverId)) return;
                            throw new InvalidOperationException("Receiver is full and has no received job items available to deposit. Free some space before resuming.");
                        }
                        run.SenderBagQuantity = Math.Min(run.SenderBagQuantity, run.DesiredQuantity);
                        // Store the exact-item deposit allowance in the next command, then restore transfer quantity after reinspection.
                        run.DesiredQuantity = (uint)Math.Min(result.BagQuantity, eligible);
                    }
                    else run.DesiredQuantity = Math.Min(run.DesiredQuantity, Math.Min(run.SenderBagQuantity,
                        Math.Min(result.BagCapacity, checked(run.SenderStackSize * 5))));
                });
                if (run.Step == CoordinatorStep.TravelSender)
                {
                    // Capacity is returned in the just-applied result; use a separate marker set by the result handler below.
                    if (run.ReceiverCapacity == 0) SetStep(run.PreviousItemDepositIndex.HasValue ? CoordinatorStep.DepositPreviousItem : CoordinatorStep.Deposit);
                }
                break;
            case CoordinatorStep.Deposit:
                Perform(leg.ReceiverId, GameOperationKind.Deposit, CoordinatorStep.InspectSender, result =>
                {
                    if (result.BagCapacity == 0 || result.MovedQuantity == 0)
                        throw new InvalidOperationException("All permitted retainers are full or inaccessible. The job remains paused.");
                    run.DesiredQuantity = (uint)Math.Min(uint.MaxValue, ledger.RemainingForLeg(run.AllocationId, run.LegIndex));
                }, run.DesiredQuantity);
                break;
            case CoordinatorStep.DepositPreviousItem:
                var previousIndex = run.PreviousItemDepositIndex ?? throw new InvalidDataException("The previous item selected for storage is missing.");
                Perform(run.PreviousItemDepositCharacterId, GameOperationKind.Deposit, CoordinatorStep.InspectSender, result =>
                {
                    if (result.MovedQuantity == 0)
                        throw new InvalidOperationException("The earlier job item could not be stored. Check that its received stock is still carried and its permitted retainers have room, then resume.");
                    run.DesiredQuantity = (uint)Math.Min(uint.MaxValue, ledger.RemainingForLeg(run.AllocationId, run.LegIndex));
                    run.PreviousItemDepositIndex = null;
                    run.PreviousItemDepositCharacterId = 0;
                }, run.DesiredQuantity, inventoryItemIndex: previousIndex);
                break;
            case CoordinatorStep.TravelSender: Perform(leg.SenderId, GameOperationKind.TravelMeeting, CoordinatorStep.TravelReceiver, destination: run.Job.Meeting); break;
            case CoordinatorStep.TravelReceiver: Perform(leg.ReceiverId, GameOperationKind.TravelMeeting, CoordinatorStep.PreviewBatch, destination: run.Job.Meeting); break;
            case CoordinatorStep.PreviewBatch:
                if (run.Pending.Count == 0) Issue(leg.SenderPeerId, Command(leg.SenderId, GameOperationKind.Inspect, run.DesiredQuantity, workerKind: WorkerCommandKind.PreviewBatch));
                else if (run.Pending[0].Response is { } preview)
                {
                    if (!preview.Success || preview.Quantity == 0 || preview.Quantity > run.DesiredQuantity) throw new InvalidOperationException(preview.Error.Length > 0 ? preview.Error : "Invalid batch preview.");
                    run.DesiredQuantity = preview.Quantity; run.Pending.Clear();
                    var operation = ledger.PrepareBatch(run.AllocationId, run.LegIndex, preview.Quantity);
                    run.TradeOperationId = operation.Id;
                    SetStep(CoordinatorStep.ArmReceiver);
                }
                break;
            case CoordinatorStep.ArmReceiver:
                if (run.Pending.Count == 0)
                {
                    Issue(leg.ReceiverPeerId, Command(leg.ReceiverId, GameOperationKind.Inspect, run.DesiredQuantity,
                        workerKind: WorkerCommandKind.ReceiveBatch, partner: leg.SenderId));
                }
                else if (run.Pending[0].Ready) SetStep(CoordinatorStep.SendBatch);
                else if (run.Pending[0].Response is { Success: false } rejected) throw new InvalidOperationException(rejected.Error);
                break;
            case CoordinatorStep.SendBatch:
                if (run.Pending.Count == 1) Issue(leg.SenderPeerId, Command(leg.SenderId, GameOperationKind.Inspect, run.DesiredQuantity,
                    workerKind: WorkerCommandKind.SendBatch, partner: leg.ReceiverId), marksTrade: true);
                SetStep(CoordinatorStep.AwaitTrade);
                break;
            case CoordinatorStep.AwaitTrade:
                ReconcileTrade(ledger, leg);
                break;
        }
    }


    private void ReconcileTrade(TransferJobLedger ledger, TransferLeg leg)
    {
        var run = Run!;
        if (run.Pending.Count != 2)
        {
            // The coordinator may have stopped after arming but before issuing the sender.
            if (run.Pending.Count == 1 && (run.Pending[0].Response is { Unchanged: true, OperatorReviewed: true } or { NeverStarted: true }))
            {
                var op = run.Journal!.Operations.Single(x => x.Id == run.TradeOperationId);
                if (op.State != TransferOperationState.Prepared) throw new InvalidOperationException("Issued trade has an incomplete command journal.");
                ledger.CancelBeforeIssue(op.Id);
                run.Pending.Clear(); run.TradeOperationId = null; SetStep(CoordinatorStep.SelectLeg);
            }
            else if (run.Pending.Count == 0 || run.Pending.Any(x => x.Response != null))
                throw new InvalidOperationException("Unissued/partially armed batch needs manual reconciliation.");
            return;
        }
        var sender = run.Pending.Single(x => x.Command.Kind == WorkerCommandKind.SendBatch).Response;
        var receiver = run.Pending.Single(x => x.Command.Kind == WorkerCommandKind.ReceiveBatch).Response;
        if (sender == null || receiver == null) return;
        if (IsVerifiedNoTransfer(sender) && IsVerifiedNoTransfer(receiver))
        {
            if (!ledger.ResolveNoTransfer(run.TradeOperationId!.Value, Observe(leg.SenderPeerId, leg.SenderId, 0),
                Observe(leg.ReceiverPeerId, leg.ReceiverId, 0), out var failure)) throw new InvalidOperationException(failure);
        }
        else
        {
            if (!sender.Success || !receiver.Success || sender.Quantity != run.DesiredQuantity || receiver.Quantity != run.DesiredQuantity)
                throw new InvalidOperationException($"Trade outcome needs reconciliation. Sender: {sender.Error}; receiver: {receiver.Error}");
            ledger.RecordObservation(run.TradeOperationId!.Value, ObservationSide.Sender, Observe(leg.SenderPeerId, leg.SenderId, sender.Quantity));
            ledger.RecordObservation(run.TradeOperationId.Value, ObservationSide.Receiver, Observe(leg.ReceiverPeerId, leg.ReceiverId, receiver.Quantity));
            SaveRun();
            if (!ledger.TryCommit(run.TradeOperationId.Value, out var failure)) throw new InvalidOperationException(failure);
            SaveRun();
        }
        run.Pending.Clear(); run.TradeOperationId = null;
        SetStep(CoordinatorStep.SelectLeg);
    }

    private static TransferObservation Observe(Guid peerId, ulong characterId, uint quantity) => new()
    { PeerId = peerId, CharacterId = characterId, Quantity = quantity, Verified = true, ObservedAt = DateTimeOffset.UtcNow };

    public void ResolvePendingTrade(bool transferred)
    {
        RequirePair();
        if (Run is not { Paused: true, TradeOperationId: not null }) throw new InvalidOperationException("There is no paused trade to reconcile.");
        if (Run.Pending.Count == 0 || Run.Pending.Any(x => x.Command.Kind is not (WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch)))
            throw new InvalidOperationException("Resume to recover the saved operation checkpoint first.");
        if (transferred && Run.Pending.Count != 2) throw new InvalidOperationException("A sender command was never issued for this batch.");
        foreach (var pending in Run.Pending)
        {
            if (pending.Response is { Success: true }) continue;
            if (pending.Response is { NeverStarted: true })
            {
                if (transferred) throw new InvalidOperationException("One side never started the trade; it cannot be marked transferred.");
                continue;
            }
            if (pending.Response?.BeforeQuantity == null || pending.Response.CurrentQuantity == null)
                throw new InvalidOperationException("Resume once with both original trade characters logged in to collect fresh reconciliation evidence.");
        }
        foreach (var pending in Run.Pending.ToArray())
        {
            if (pending.Response is { Success: true } or { NeverStarted: true }) continue;
            pending.Response = null;
            var resolution = new ManualResolution(pending.Command.Id, transferred);
            if (pending.PeerId == Settings.PeerId) ResolveWorker(resolution, pending.PeerId);
            else Send("resolve", resolution);
        }
        Run.Paused = false; Run.Step = CoordinatorStep.AwaitTrade; error = ""; SaveRun();
    }

    private static bool IsVerifiedNoTransfer(WorkerResponse response) => response.NeverStarted || (response.Unchanged && response.OperatorReviewed && !response.Ambiguous);
}
