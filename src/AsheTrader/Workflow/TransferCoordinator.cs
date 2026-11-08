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
        JobPlanner.ValidateDefinition(job.Definition);
        if (!discoveryOnly && (job.Meeting.WorldId == 0 || job.Meeting.TerritoryId == 0 || job.Meeting.AetheryteId == 0))
            throw new InvalidOperationException("Capture an outdoor meeting point and choose its main aetheryte before starting.");
        var order = job.Definition.Sources.Concat(job.Definition.Recipients.Select(x => x.CharacterId))
            .Concat(job.Definition.RelayCharacters.Values).Distinct().ToList();
        foreach (var id in order) _ = Character(id);
        Run = new() { Job = Clone(job), DiscoveryOnly = discoveryOnly, DiscoveryOrder = order, Step = CoordinatorStep.DiscoverLogin,
            Status = "Discovering source and destination inventories, including retainers…" };
        error = "";
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
            var deposit = Run.Pending.SingleOrDefault(x => x.Command.Kind == WorkerCommandKind.Game
                && x.Command.Operation.Kind == GameOperationKind.Deposit);
            if (deposit != null && deposit.Response?.NeverStarted != true
                && (deposit.Response?.Observation == null || deposit.Response.Ambiguous))
            {
                // The worker may have stored items before the coordinator saved the result.
                // Recover that exact command; losing its result could spend the same deposit allowance twice.
                deposit.Response = null;
                Run.Step = CoordinatorStep.Deposit;
            }
            else
            {
                if (deposit != null) AccountDeposit(deposit, deposit.Command.Operation.CharacterId);
                Run.Pending.Clear();
                Run.Step = Run.Journal == null ? CoordinatorStep.DiscoverLogin : CoordinatorStep.SelectLeg;
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
        WorkerCommandKind workerKind = WorkerCommandKind.Game, ulong? partner = null)
    {
        var identity = Character(characterId).Character;
        var command = new WorkerCommand
        {
            Id = Guid.NewGuid(),
            JobId = Run!.Job.Definition.Id, Kind = workerKind,
            Operation = new GameOperation
            {
                Kind = kind, CharacterId = characterId, CharacterName = identity.Name, World = identity.World,
                ItemId = Run.Job.Definition.ItemId, HighQuality = Run.Job.Definition.HighQuality,
                Quantity = quantity, Destination = destination == null ? null : Clone(destination)
            },
            Partner = partner.HasValue ? Clone(Character(partner.Value).Character) : null
        };
        command.Operation.Id = command.Id;
        return command;
    }

    private void Issue(Guid peerId, WorkerCommand command, bool marksTrade = false)
    {
        Run!.Pending.Add(new() { PeerId = peerId, Command = command });
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
        uint quantity = 0, TravelLocation? destination = null)
    {
        if (Run!.Pending.Count == 0)
        {
            Issue(Character(characterId).PeerId, Command(characterId, kind, quantity, destination));
            return false;
        }
        var pending = Run.Pending.Single();
        if (pending.Response == null) return false;
        if (kind == GameOperationKind.Deposit) AccountDeposit(pending, characterId);
        if (kind == GameOperationKind.Deposit && pending.Response.Ambiguous)
            throw new InvalidOperationException("A retainer deposit has an uncertain outcome. Inspect storage before ending this job and creating a freshly scanned job for the remaining quantity; the deposit will not be retried.");
        if (!pending.Response.Success) throw new InvalidOperationException(pending.Response.Error);
        var result = pending.Response.Observation ?? throw new InvalidDataException("Worker omitted its inventory observation.");
        if (result.CharacterId != characterId) throw new InvalidDataException("Worker observed a different character.");
        apply?.Invoke(result);
        Run.Pending.Clear();
        SetStep(next);
        return true;
    }

    private void AccountDeposit(PendingCommand pending, ulong characterId)
    {
        if (!pending.InventoryAdjustmentApplied && pending.Response?.Observation is { } depositResult)
        {
            if (depositResult.CharacterId != characterId || depositResult.MovedQuantity > pending.Command.Operation.Quantity)
                throw new InvalidDataException("Invalid deposit observation.");
            Run!.Deposited[characterId] = checked(Run.Deposited.GetValueOrDefault(characterId) + depositResult.MovedQuantity);
            pending.InventoryAdjustmentApplied = true;
            SaveRun();
        }
    }

    private void SetStep(CoordinatorStep step)
    {
        Run!.Step = step;
        Run.Status = step switch
        {
            CoordinatorStep.DiscoverLogin => $"Discovery {Run.DiscoveryIndex + 1}/{Run.DiscoveryOrder.Count}: switching character…",
            CoordinatorStep.DiscoverScan => "Inspecting carried inventory and every retainer…",
            CoordinatorStep.Plan => "Allocating source stock in recipient priority order…",
            CoordinatorStep.Withdraw => "Collecting the next load from source retainers…",
            CoordinatorStep.Deposit => "Receiver is returning home and depositing job items into retainers…",
            CoordinatorStep.TravelSender or CoordinatorStep.TravelReceiver => "Bringing both characters to the meeting point…",
            CoordinatorStep.AwaitTrade => "Waiting for both clients to verify the agreed trade…",
            _ => $"Transfer job: {step}"
        };
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
        if (run.Step is CoordinatorStep.DiscoverLogin or CoordinatorStep.DiscoverScan)
        {
            if (run.DiscoveryIndex >= run.DiscoveryOrder.Count) { SetStep(CoordinatorStep.Plan); return; }
            var id = run.DiscoveryOrder[run.DiscoveryIndex];
            if (run.Step == CoordinatorStep.DiscoverLogin) Perform(id, GameOperationKind.Login, CoordinatorStep.DiscoverScan);
            else Perform(id, GameOperationKind.ScanRetainers, CoordinatorStep.DiscoverLogin, result =>
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
                run.Step = CoordinatorStep.Finished;
                run.Status = "Discovery complete. All selected bags and retainers were inspected; no trades were performed.";
                SaveRun(); return;
            }
            run.Journal = TransferJobLedger.Create(JobPlanner.Plan(run.Job.Definition, run.Snapshots.Values)).Journal;
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
            run.Step = CoordinatorStep.Finished;
            run.Paused = run.Journal.Plan.HasShortage;
            run.Status = run.Journal.Plan.HasShortage
                ? "Available stock delivered. Some requested quantities remain short; see the unmet-target list."
                : "Transfer job complete: all planned final deliveries are verified.";
            run.Journal.State = run.Journal.Plan.HasShortage ? TransferJobState.Paused : TransferJobState.Completed;
            SaveRun(); return;
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
                    run.SenderBagQuantity = result.BagQuantity;
                    run.SenderStackSize = result.StackSize;
                    if (result.StackSize == 0) throw new InvalidDataException("Invalid item stack size.");
                });
                if (run.Step == CoordinatorStep.InspectReceiver && run.SenderBagQuantity == 0) SetStep(CoordinatorStep.Withdraw);
                break;
            case CoordinatorStep.Withdraw:
                Perform(leg.SenderId, GameOperationKind.Withdraw, CoordinatorStep.InspectReceiver, result =>
                {
                    if (result.BagQuantity == 0 || result.MovedQuantity == 0)
                        throw new InvalidOperationException("No source items could be withdrawn. Source stock changed or the character has no room.");
                    run.SenderBagQuantity = result.BagQuantity; run.SenderStackSize = result.StackSize;
                }, run.DesiredQuantity);
                break;
            case CoordinatorStep.InspectReceiver:
                Perform(leg.ReceiverId, GameOperationKind.Inspect, CoordinatorStep.TravelSender, result =>
                {
                    run.ReceiverCapacity = result.BagCapacity;
                    if (result.BagCapacity == 0)
                    {
                        var received = run.Journal.Operations.Where(x => x.State == TransferOperationState.Committed)
                            .Where(x => run.Journal.Plan.Allocations.Single(a => a.Id == x.AllocationId).Legs[x.LegIndex].ReceiverId == leg.ReceiverId)
                            .Aggregate(0UL, (sum, x) => checked(sum + x.Quantity));
                        var relayed = run.Journal.Operations.Where(x => x.State == TransferOperationState.Committed && x.LegIndex > 0)
                            .Where(x => run.Journal.Plan.Allocations.Single(a => a.Id == x.AllocationId).Legs[x.LegIndex].SenderId == leg.ReceiverId)
                            .Aggregate(0UL, (sum, x) => checked(sum + x.Quantity));
                        var stored = run.Deposited.GetValueOrDefault(leg.ReceiverId);
                        var eligible = received > relayed + stored ? received - relayed - stored : 0;
                        if (result.BagQuantity == 0 || eligible == 0) throw new InvalidOperationException("Receiver is full and has no received job items available to deposit. Free some space before resuming.");
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
                    if (run.ReceiverCapacity == 0) SetStep(CoordinatorStep.Deposit);
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
