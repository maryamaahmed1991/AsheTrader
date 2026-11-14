using AsheTrader.Core;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

namespace AsheTrader.Fuel;

public enum FuelQueueStage { SelectCharacter, Login, Travel, Buying, CloseShop, Deposit, RecoverDeposit, Finished, RefreshCredits }

public sealed class FuelQueueRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public List<FuelQueueEntry> Entries { get; set; } = [];
    public int Index { get; set; }
    public bool Paused { get; set; } = true;
    public bool Finished { get; set; }
    public string Status { get; set; } = "Fuel queue ready.";
    public FuelQueueStage Stage { get; set; }
    public GameOperation? PendingOperation { get; set; }
    public StorageMovementCheckpoint? StorageCheckpoint { get; set; }
    public FuelPurchaseCheckpoint? PendingPurchase { get; set; }
    public FuelCreditBudget? CreditBudget { get; set; }
    public bool AwaitingCreditRefresh { get; set; }
    public uint? RemainingTarget { get; set; }
    public bool TargetInitialized { get; set; }
    public bool FinishAfterDeposit { get; set; }
    public bool EndRequested { get; set; }
    public string FinishReason { get; set; } = "";
    public HashSet<string> ExhaustedCompanies { get; set; } = [];
    [JsonIgnore] public ulong TotalPurchased => Entries.Aggregate(0UL, (sum, row) => sum + row.Purchased);
    [JsonIgnore] public ulong TotalDeposited => Entries.Aggregate(0UL, (sum, row) => sum + row.Deposited);
}

/// <summary>One independent, durable fuel queue per PC. Native deposits use the transfer executor.</summary>
public sealed class FuelQueueService
{
    private readonly Plugin plugin;
    private readonly SettingsFileStore file;
    private readonly GameWorkflowExecutor executor = new();
    private readonly FuelDestinationRunner destination = new();
    private readonly StorageRecoveryStability storageStability = new();
    private (ulong Character, ulong Company, uint Bags, uint Price)? purchaseSample;
    private long purchaseStableAt;
    private long stageStarted;
    private (ulong Character, ulong Company)? companySample;
    private long companyStableAt;
    private bool purchaseRunning;
    private bool destinationStarted;
    private readonly TeleportRequestGate homeReturnGate = new();
    private bool ownsHomeTravel;
    private uint homeTravelWorld;
    private long homeTravelStarted;
    private bool journalHealthy = true;
    private string setupStatus = "Refresh characters and saved Lifestream houses to configure fuel runs.";
    public FuelQueueRun? Run { get; private set; }
    public bool Active => Run is { Paused: false, Finished: false };
    public bool HasUnfinishedRun => Run is { Finished: false };
    public bool ExpectsLogout => Active && (Run!.PendingOperation?.Kind == GameOperationKind.Login || destination.Active || ownsHomeTravel);
    public string SetupStatus => setupStatus;
    public string Status => Run?.Status ?? "Fuel queue idle.";
    private FuelQueueEntry Entry => Run!.Entries[Run.Index];

    internal void CaptureDiagnosticSnapshot(string reason)
    {
        executor.CaptureDiagnosticSnapshot(reason);
        destination.CaptureDiagnosticSnapshot(reason);
        plugin.Fuel.CaptureDiagnosticSnapshot(reason);
    }

    public FuelQueueService(Plugin plugin)
    {
        this.plugin = plugin;
        file = new(Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "fuel-queue.json"));
        executor.Diagnostic = message => plugin.Workflow.RecordDiagnostic("fuel " + message);
        destination.Diagnostic = message => plugin.Workflow.RecordDiagnostic("fuel route " + message);
        executor.PersistStorageCheckpoint = checkpoint =>
        {
            if (Run?.PendingOperation?.Id != checkpoint.OperationId) throw new InvalidOperationException("The fuel deposit has no matching durable intent.");
            Run.StorageCheckpoint = checkpoint;
            Save();
        };
        plugin.Fuel.PersistPurchase = (checkpoint, purchased) =>
        {
            if (Run == null || Run.Index >= Run.Entries.Count) throw new InvalidOperationException("The fuel purchase has no active queue entry.");
            if (checkpoint != null) Run.PendingPurchase = checkpoint;
            else
            {
                if (Run.PendingPurchase == null || purchased != Run.PendingPurchase.Quantity)
                    throw new InvalidOperationException("The confirmed fuel purchase does not match its saved intent.");
                AccountPurchase(purchased);
            }
            Save();
        };
        try
        {
            Run = JsonConvert.DeserializeObject<FuelQueueRun>(file.Load() ?? "null");
            if (HasUnfinishedRun)
            {
                Run!.Paused = true;
                Run.Status = "Saved fuel queue paused. Resume to tally any interrupted purchase or deposit.";
                Save();
            }
        }
        catch (Exception ex) { journalHealthy = false; setupStatus = "Fuel progress could not be loaded: " + ex.Message; }
    }

    public void RefreshSetup()
    {
        if (HasUnfinishedRun || plugin.Workflow.Busy || plugin.Fuel.Active || plugin.Trader.IsRunning) return;
        plugin.FuelAccess.RefreshCurrent();
        plugin.Workflow.RefreshLocalRoster();
        var imported = 0;
        if (plugin.Lifestream.TryGetHouses(plugin.Config.Characters, out var houses, out var error))
        {
            foreach (var house in houses)
                if (plugin.Config.Houses.All(x => x.Address != house.Address)) { plugin.Config.Houses.Add(house); imported++; }
            plugin.Save();
        }
        setupStatus = $"{plugin.Workflow.RosterStatus} {plugin.Lifestream.LastHouseImportStatus} "
            + $"{imported} added; {plugin.Config.Houses.Count} saved house(s) available."
            + (string.IsNullOrEmpty(error) ? "" : " " + error);
        plugin.Workflow.RecordDiagnostic("fuel house refresh: " + setupStatus);
    }

    public void RegisterHouse(string name, uint worldId, int city, int ward, int plot)
    {
        if (HasUnfinishedRun) throw new InvalidOperationException("Finish or end the fuel queue before changing destinations.");
        if (Plugin.DataManager.GetExcelSheet<World>().GetRowOrDefault(worldId) is not { IsPublic: true })
            throw new ArgumentException("Select a valid home world.");
        var address = FuelHouseAddress.Format(checked((int)worldId), city, ward, plot);
        var house = plugin.Config.Houses.FirstOrDefault(x => x.Address == address);
        if (house == null) plugin.Config.Houses.Add(new() { Name = string.IsNullOrWhiteSpace(name) ? $"Workshop W{ward}, P{plot}" : name.Trim(), Address = address });
        else if (!string.IsNullOrWhiteSpace(name)) house.Name = name.Trim();
        plugin.Save(); setupStatus = "House saved. It is available only to characters on that home world.";
    }

    public void CaptureHouse()
    {
        if (HasUnfinishedRun) throw new InvalidOperationException("Finish or end the fuel queue before changing destinations.");
        if (!FuelRunner.TryCaptureCurrentHouse(out var house, out var error))
        {
            setupStatus = error;
            plugin.Workflow.RecordDiagnostic("fuel workshop capture failed: " + error + " " + NativeFuelShop.LastHousingDiagnostic);
            return;
        }
        if (plugin.Config.Houses.All(x => x.Address != house.Address)) plugin.Config.Houses.Add(house);
        plugin.Save(); setupStatus = $"Registered {house.Name}.";
        plugin.Workflow.RecordDiagnostic("fuel workshop capture: " + house.Address);
    }

    public IReadOnlyList<FuelQueueEntry> Preview() => FuelQueuePlanner.Build(plugin.Config.Characters,
        plugin.Config.Houses, plugin.Config.FuelSettings, ResolveWorld);

    public FuelQueueEntry PreviewCharacter(CharacterProfile character) => FuelQueuePlanner.Build([character],
        plugin.Config.Houses, plugin.Config.FuelSettings, ResolveWorld, includeUnselected: true)[0];

    internal static uint? ResolveWorld(string name)
    {
        var world = Plugin.DataManager.GetExcelSheet<World>().FirstOrDefault(x => x.IsPublic && string.Equals(x.Name.ToString(), name, StringComparison.OrdinalIgnoreCase));
        return world.RowId == 0 ? null : world.RowId;
    }

    public void Start()
    {
        RequireAvailable();
        if (HasUnfinishedRun) throw new InvalidOperationException("Resume or end the saved fuel queue first.");
        var entries = Preview();
        if (!entries.Any(x => x.Status == FuelQueueEntryStatus.Queued)) throw new InvalidOperationException("Select at least one character with a valid fuel destination.");
        Run = new() { Entries = entries.ToList(), Paused = false, Status = "Starting the selected fuel queue…" };
        stageStarted = Environment.TickCount64;
        Save();
    }

    private void RequireAvailable()
    {
        if (!journalHealthy) throw new InvalidOperationException(setupStatus);
        if (plugin.Workflow.Busy || plugin.Workflow.Run is { Step: not CoordinatorStep.Finished } || plugin.Trader.IsRunning || plugin.Fuel.Active)
            throw new InvalidOperationException("Finish the transfer job or manual operation before starting this PC's fuel queue.");
        var ar = plugin.AutoRetainer.GetStatus();
        if (!ar.Ready || ar.Busy != false || ar.MultiModeEnabled != false)
            throw new InvalidOperationException("AutoRetainer must be ready and idle with multi-mode paused.");
    }

    public void Resume()
    {
        if (!HasUnfinishedRun || Active) return;
        if (Run!.EndRequested && Run.PendingPurchase == null
            && !(Run.PendingOperation?.Kind == GameOperationKind.Deposit && Run.StorageCheckpoint != null))
        { FinishEnd(); return; }
        RequireAvailable();
        StopHomeTravel();
        while (executor.TryTakeResult(out _)) { }
        purchaseRunning = destinationStarted = false; purchaseSample = null; storageStability.Reset();
        var run = Run!;
        if (run.PendingOperation?.Kind == GameOperationKind.Deposit && run.StorageCheckpoint != null)
        {
            if (Plugin.PlayerState.ContentId != run.PendingOperation.CharacterId)
                throw new InvalidOperationException("Return to the interrupted deposit character before resuming its inventory tally.");
            run.Stage = FuelQueueStage.RecoverDeposit;
        }
        else
        {
            var continueDeposit = run.PendingPurchase == null
                && (run.Stage is FuelQueueStage.CloseShop or FuelQueueStage.Deposit || run.FinishAfterDeposit);
            if (continueDeposit && Plugin.PlayerState.ContentId != Entry.CharacterId)
                throw new InvalidOperationException("Return to the fuel character before resuming its saved retainer deposit.");
            run.PendingOperation = null; run.StorageCheckpoint = null;
            run.Stage = continueDeposit ? FuelQueueStage.CloseShop
                : run.Stage == FuelQueueStage.RefreshCredits ? FuelQueueStage.RefreshCredits : FuelQueueStage.SelectCharacter;
        }
        run.Paused = false; stageStarted = Environment.TickCount64;
        SetStatus("Resuming the fuel queue and reconciling saved progress…"); Save();
    }

    public void Pause(string reason = "Fuel queue paused by user.")
    {
        if (!HasUnfinishedRun) return;
        Run!.Paused = true;
        StopHomeTravel();
        try { plugin.Fuel.Stop(); } catch (Exception ex) { Plugin.Log.Warning(ex, "Could not stop fuel purchases"); }
        try { destination.Stop(); } catch (Exception ex) { Plugin.Log.Warning(ex, "Could not stop the fuel destination route"); }
        try { executor.Stop(reason); } catch (Exception ex) { Plugin.Log.Warning(ex, "Could not stop the fuel storage operation"); }
        purchaseRunning = destinationStarted = false;
        Run.Status = reason;
        if (journalHealthy)
            try { Save(); } catch (Exception ex) { Plugin.Log.Error(ex, "Fuel pause could not be saved"); Run.Status = reason + " Progress could not be saved: " + ex.Message; }
    }

    public void End()
    {
        if (!HasUnfinishedRun) return;
        Pause();
        Run!.EndRequested = true;
        if (Run.PendingPurchase != null || Run.PendingOperation?.Kind == GameOperationKind.Deposit && Run.StorageCheckpoint != null)
        {
            while (executor.TryTakeResult(out _)) { }
            Run.Stage = Run.PendingPurchase != null ? FuelQueueStage.Travel : FuelQueueStage.RecoverDeposit;
            Run.Paused = false; stageStarted = Environment.TickCount64;
            purchaseSample = null; storageStability.Reset();
            Run.Status = "Ending the fuel queue: tallying the outstanding movement before stopping. No new purchase will start.";
            Save(); return;
        }
        FinishEnd();
    }

    private void FinishEnd()
    {
        Run!.Finished = true; Run.Paused = true; Run.PendingOperation = null; Run.StorageCheckpoint = null;
        Run.Stage = FuelQueueStage.Finished;
        Run.Status = "Fuel queue ended. Purchased fuel and completed progress are retained.";
        Save();
    }

    public void Update()
    {
        if (!Active) return;
        try
        {
            if (plugin.Workflow.Busy || plugin.Trader.IsRunning) { Pause("Another gameplay operation became active; fuel queue paused."); return; }
            executor.Update(); destination.Update();
            while (executor.TryTakeResult(out var result)) AcceptNativeResult(result);
            if (!Active) return;
            if (NativeRetainers.HasActiveCutscene) { stageStarted = Environment.TickCount64; storageStability.Reset(); SetStatus("Waiting for the cutscene to finish naturally…"); return; }
            if (Run!.Stage == FuelQueueStage.RecoverDeposit && Plugin.ClientState.IsLoggedIn
                && (Plugin.ObjectTable.LocalPlayer == null || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]))
            { stageStarted = Environment.TickCount64; storageStability.Reset(); SetStatus("Waiting for loading to finish before tallying the interrupted deposit…"); return; }
            if (Run!.Stage != FuelQueueStage.SelectCharacter && Plugin.PlayerState.ContentId != Entry.CharacterId
                && Run.Stage != FuelQueueStage.Login && !destination.Active && !ownsHomeTravel)
                throw new InvalidOperationException("The active character changed outside the fuel queue.");
            switch (Run.Stage)
            {
                case FuelQueueStage.SelectCharacter: SelectNext(); break;
                case FuelQueueStage.Login: SetStatus(executor.Status); break;
                case FuelQueueStage.Travel: Travel(); break;
                case FuelQueueStage.Buying: Buy(); break;
                case FuelQueueStage.CloseShop: CloseAndDeposit(); break;
                case FuelQueueStage.Deposit: SetStatus(executor.Status); break;
                case FuelQueueStage.RecoverDeposit: RecoverDeposit(); break;
                case FuelQueueStage.RefreshCredits: RefreshCredits(); break;
            }
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Fuel queue paused"); Pause(ex.Message); }
    }

    private void SelectNext()
    {
        StopHomeTravel();
        while (Run!.Index < Run.Entries.Count)
        {
            if (Entry.Status is FuelQueueEntryStatus.Completed or FuelQueueEntryStatus.Skipped or FuelQueueEntryStatus.StorageFull)
            { Run.Index++; continue; }
            if (Run.ExhaustedCompanies.Contains(CompanyKey(Entry)))
            { Entry.Status = FuelQueueEntryStatus.Skipped; Entry.Note = "This FC's credit budget was exhausted by an earlier selected character."; Run.Index++; continue; }
            Entry.Status = FuelQueueEntryStatus.Running;
            IssueNative(GameOperationKind.Login, 0);
            return;
        }
        Run.Finished = true; Run.Paused = true; Run.Stage = FuelQueueStage.Finished;
        Run.Status = $"Fuel queue complete: {Run.TotalPurchased:N0} tanks purchased; {Run.TotalDeposited:N0} deposited. Review any skipped or full-storage characters below.";
        Save();
    }

    private static string CompanyKey(FuelQueueEntry entry) => FuelQueueDecisions.CompanyBudgetKey(entry);

    private bool VerifyCharacterCompany()
    {
        var company = NativeFuelShop.CurrentCompanyId;
        var sample = (Plugin.PlayerState.ContentId, company);
        if (companySample != sample) { companySample = sample; companyStableAt = Environment.TickCount64; }
        if (Environment.TickCount64 - companyStableAt < 1000) { SetStatus("Waiting for stable FC membership…"); return false; }
        if (company == 0)
        {
            if (Environment.TickCount64 - stageStarted < 15_000) { SetStatus("Waiting for FC membership to load after login…"); return false; }
            if (Run!.PendingPurchase != null) throw new InvalidOperationException("FC identity is unavailable for the interrupted purchase tally.");
            FinishEntry(FuelQueueEntryStatus.Skipped, "FC membership was still unavailable after login; refresh and retry this character."); return false;
        }
        if (Entry.FreeCompanyKnown && company != Entry.FreeCompanyId)
        {
            if (Environment.TickCount64 - stageStarted < 15_000) { SetStatus("Waiting for the expected FC membership…"); return false; }
            if (Run!.PendingPurchase != null) throw new InvalidOperationException("FC membership changed while a fuel purchase was unresolved.");
            FinishEntry(FuelQueueEntryStatus.Skipped, "FC membership changed since setup. Refresh the roster before assigning this character again."); return false;
        }
        Entry.FreeCompanyId = company; Entry.FreeCompanyKnown = true;
        if (Run!.ExhaustedCompanies.Contains(CompanyKey(Entry)))
        { FinishEntry(FuelQueueEntryStatus.Skipped, "This FC's credit budget was already exhausted."); return false; }
        return true;
    }

    private void Travel()
    {
        if (destination.Active) { SetStatus(destination.Status); return; }
        if (destinationStarted)
        {
            destinationStarted = false;
            if (!destination.Success) throw new InvalidOperationException(destination.Error);
            if (!FuelDestinationRunner.IsExpectedLocation(Entry) || !NativeFuelShop.TryRead(out _, out _))
                throw new InvalidOperationException("Fuel travel finished without the configured supplier shop. Resume after checking the destination.");
            Transition(FuelQueueStage.Buying, "Checking live fuel stock and FC credits…"); return;
        }
        if (!EnsureHomeWorld()) return;
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null
            || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]) return;
        if (Run!.PendingPurchase != null && Plugin.PlayerState.ContentId != Run.PendingPurchase.CharacterId)
            throw new InvalidOperationException("Return to the interrupted purchase character before tallying its fuel.");
        if (!VerifyCharacterCompany()) return;
        if (FuelDestinationRunner.IsExpectedLocation(Entry) && NativeFuelShop.TryRead(out _, out _))
        { Transition(FuelQueueStage.Buying, "Checking live fuel stock and FC credits…"); return; }
        var access = plugin.FuelAccess.AssessCurrent(Entry);
        if (access.Status == FuelAccessStatus.Unknown && access.Kind != FuelAccessRouteKind.Unavailable && Environment.TickCount64 - stageStarted < 8000)
        { SetStatus("Waiting for this character's travel access to load…"); return; }
        plugin.Workflow.RecordDiagnostic($"fuel route assessment character={Entry.CharacterId}; status={access.Status}; route={access.Kind}; observed={access.SnapshotObservedAtUtc:O}; reason={access.Reason}");
        if ((access.Status == FuelAccessStatus.Ineligible || access.Kind == FuelAccessRouteKind.Unavailable)
            && !FuelDestinationRunner.CanApproachLocally(Entry))
        {
            var reason = access.Reason + (string.IsNullOrWhiteSpace(access.RecommendedRoute) ? "" : " " + access.RecommendedRoute);
            if (Run.PendingPurchase != null) throw new InvalidOperationException("The interrupted purchase's supplier is not currently reachable: " + reason);
            FinishEntry(FuelQueueEntryStatus.Skipped, reason);
            return;
        }
        if (!destination.Start(Entry, out var error, plugin.Config.Characters, access)) throw new InvalidOperationException(error);
        destinationStarted = true;
        SetStatus(destination.Status);
    }

    private bool EnsureHomeWorld()
    {
        var now = Environment.TickCount64;
        var player = Plugin.ObjectTable.LocalPlayer;
        var ready = Plugin.ClientState.IsLoggedIn && player != null && player.IsTargetable && !player.IsCasting
            && !Plugin.Condition[ConditionFlag.BetweenAreas] && !Plugin.Condition[ConditionFlag.BetweenAreas51]
            && !NativeRetainers.HasActiveCutscene;
        if (Plugin.PlayerState.ContentId != 0 && Plugin.PlayerState.ContentId != Entry.CharacterId)
            throw new InvalidOperationException("The fuel buyer changed during the return to its home world.");
        if (!ownsHomeTravel && ready && player!.CurrentWorld.RowId == player.HomeWorld.RowId)
        {
            homeReturnGate.Reset(); homeTravelStarted = 0;
            return true;
        }
        if (!ownsHomeTravel && !ready) return false;
        if (homeTravelStarted == 0) homeTravelStarted = now;
        if (now - homeTravelStarted > 900_000)
            throw new TimeoutException("Returning to the fuel buyer's home world timed out. Resume after the world transfer settles.");
        var lifestream = plugin.Lifestream.GetStatus();
        if (!lifestream.Ready) throw new InvalidOperationException(lifestream.Error ?? "Lifestream is unavailable during the home-world return.");
        var arrived = ownsHomeTravel && ready && player!.CurrentWorld.RowId == homeTravelWorld && player.HomeWorld.RowId == homeTravelWorld
            && lifestream.Busy == false;
        var decision = homeReturnGate.Observe(now, player?.CurrentWorld.RowId ?? 0, Plugin.ClientState.TerritoryType,
            ready && lifestream.Busy == false && !NativeRetainers.BellInteractionBlocked,
            arrived, lifestream.Busy == true || !ready);
        if (decision == TeleportRequestDecision.Arrived)
        {
            ownsHomeTravel = false; homeReturnGate.Reset(); homeTravelStarted = 0;
            companySample = null; stageStarted = now;
            plugin.FuelAccess.RefreshCurrent();
            SetStatus("Returned home. Checking this character's housing access before choosing its route…");
            return true;
        }
        if (decision is TeleportRequestDecision.ReadinessTimedOut or TeleportRequestDecision.ArrivalTimedOut)
            throw new InvalidOperationException("Lifestream could not finish the home-world return. Resume after checking its travel state.");
        if (decision != TeleportRequestDecision.Request)
        { SetStatus(ownsHomeTravel ? "Returning to the buyer's home world before selecting a fuel route…" : "Waiting for the home-world return to become ready…"); return false; }
        homeTravelWorld = player!.HomeWorld.RowId;
        // Claim ownership before dispatch so an IPC exception after queueing can still be cancelled.
        ownsHomeTravel = true;
        var accepted = Plugin.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById").InvokeFunc(homeTravelWorld);
        homeReturnGate.RecordRequest(now, accepted);
        if (!accepted) ownsHomeTravel = false;
        plugin.Workflow.RecordDiagnostic($"fuel home-world return character={Entry.CharacterId}; targetWorld={homeTravelWorld}; accepted={accepted}; attempts={homeReturnGate.Attempts}");
        SetStatus(accepted ? "Returning to the buyer's home world before selecting a fuel route…" : "Home-world return was not accepted yet; waiting to retry…");
        return false;
    }

    private void StopHomeTravel()
    {
        if (ownsHomeTravel)
        {
            ownsHomeTravel = false;
            try { Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Could not stop the fuel buyer's home-world return"); }
        }
        homeReturnGate.Reset(); homeTravelStarted = 0; homeTravelWorld = 0;
    }

    private void Buy()
    {
        if (purchaseRunning)
        {
            if (plugin.Fuel.Active) { SetStatus(plugin.Fuel.Status); return; }
            purchaseRunning = false;
            if (!plugin.Fuel.CompletedNormally) throw new InvalidOperationException(plugin.Fuel.Status);
        }
        if (!NativeFuelShop.TryRead(out var shop, out var error)) throw new InvalidOperationException(error);
        if (Run!.PendingPurchase is { } pending)
        {
            if (NativeFuelShop.HasConfirmation) throw new InvalidOperationException("Close the outstanding fuel confirmation, then Resume to tally inventory and credits.");
            // FC credits are shared and can change while another member earns or spends
            // them. Only this buyer's identity, item tally and price must settle.
            var sample = (Plugin.PlayerState.ContentId, NativeFuelShop.CurrentCompanyId, shop.InventoryQuantity, shop.UnitPrice);
            if (purchaseSample != sample) { purchaseSample = sample; purchaseStableAt = Environment.TickCount64; return; }
            if (Environment.TickCount64 - purchaseStableAt < 500) return;
            var outcome = FuelPurchaseRecovery.Classify(pending, Plugin.PlayerState.ContentId, NativeFuelShop.CurrentCompanyId, shop.InventoryQuantity, shop.Credits, shop.UnitPrice);
            if (outcome == FuelPurchaseOutcome.Inconsistent)
                throw new InvalidOperationException("The interrupted purchase's fuel tally or buyer identity could not be reconciled. No purchase was repeated.");
            if (outcome == FuelPurchaseOutcome.Completed)
            {
                plugin.Workflow.RecordDiagnostic($"fuel recovery completed from exact item delivery; quantity={pending.Quantity}; bagsBefore={pending.BagBefore}; bags={shop.InventoryQuantity}; expectedCreditCost={(ulong)pending.Quantity * pending.UnitPrice}; observedCreditChange={(long)pending.CreditsBefore - shop.Credits}; shared credits are advisory");
                AccountPurchase(pending.Quantity);
            }
            else Run.PendingPurchase = null;
            purchaseSample = null; Save();
        }
        if (Run.EndRequested) { FinishEnd(); return; }
        if (!VerifyCharacterCompany()) return;
        if (Run.AwaitingCreditRefresh)
        {
            // This flag is saved only after the old shop and its returning menu fully close.
            // Travel has now reopened a verified supplier shop, yielding a fresh credit balance.
            Run.CreditBudget = FuelCreditBudget.Begin(Entry.FreeCompanyId, shop.Credits);
            Run.AwaitingCreditRefresh = false;
            plugin.Workflow.RecordDiagnostic($"fuel credit budget refreshed; FC={Entry.FreeCompanyId}; credits={shop.Credits}; addon={shop.AddonId}");
            Save();
        }
        if (Run.CreditBudget == null || Run.CreditBudget.FreeCompanyId != Entry.FreeCompanyId)
        {
            Run.CreditBudget = FuelCreditBudget.Begin(Entry.FreeCompanyId, shop.Credits);
            Save();
        }
        if (!Run.TargetInitialized)
        {
            Run.TargetInitialized = true;
            Run.RemainingTarget = FuelQueueDecisions.InitialTarget(Entry, shop.InventoryQuantity);
            Save();
        }
        if (Run.RemainingTarget != 0
            && Run.CreditBudget.NeedsRefresh(shop.Credits, Entry.CreditReserve, shop.UnitPrice))
        {
            Transition(FuelQueueStage.RefreshCredits, "Refreshing the supplier's FC credit balance before purchasing more…");
            return;
        }
        var decision = FuelQueueDecisions.PlanPurchase(Entry, Run.RemainingTarget, shop.Capacity,
            Run.CreditBudget.PlanningCredits(shop.Credits, Run.RemainingTarget), shop.UnitPrice);
        if (decision.NeedsDeposit)
        {
            if (decision.CreditExhausted) Run.ExhaustedCompanies.Add(CompanyKey(Entry));
            Run.FinishAfterDeposit = decision.FinishAfterDeposit;
            Run.FinishReason = decision.FinishAfterDeposit ? decision.Reason : "";
            Transition(FuelQueueStage.CloseShop, decision.Reason); return;
        }
        if (!plugin.Fuel.StartQueue(Entry, decision.Quantity, out error)) throw new InvalidOperationException(error);
        purchaseRunning = plugin.Fuel.Active;
        SetStatus(plugin.Fuel.Status);
    }

    private void AccountPurchase(uint quantity)
    {
        var pending = Run!.PendingPurchase ?? throw new InvalidOperationException("The fuel purchase has no durable intent to tally.");
        if (quantity != pending.Quantity || pending.FreeCompanyId != Entry.FreeCompanyId)
            throw new InvalidOperationException("The fuel receipt does not match its saved FC purchase.");
        var purchased = checked(Entry.Purchased + quantity);
        var remaining = FuelQueueDecisions.RemainingAfterPurchase(Run.RemainingTarget, quantity);
        // Legacy saved runs have no budget. Start from the pending request's original balance,
        // not a possibly stale or independently changed balance observed during Resume.
        var budget = Run.CreditBudget?.FreeCompanyId == pending.FreeCompanyId ? Run.CreditBudget
            : FuelCreditBudget.Begin(pending.FreeCompanyId, pending.CreditsBefore);
        budget.Acknowledge(pending);
        Run.CreditBudget = budget;
        Entry.Purchased = purchased;
        Run.RemainingTarget = remaining;
        Run.PendingPurchase = null;
    }

    private void RefreshCredits()
    {
        if (Run!.PendingPurchase != null)
            throw new InvalidOperationException("Tally the pending fuel receipt before refreshing the credit balance.");
        if (Run.EndRequested) { FinishEnd(); return; }
        if (!destination.TryCloseShop(Entry, out var error))
        {
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
            if (Environment.TickCount64 - stageStarted > 30_000)
                throw new TimeoutException("The fuel shop did not close for its credit refresh.");
            return;
        }
        Run.AwaitingCreditRefresh = true;
        destinationStarted = false;
        Transition(FuelQueueStage.Travel, "Reopening the supplier to refresh the FC credit balance…");
    }

    private void CloseAndDeposit()
    {
        if (!destination.TryCloseShop(Entry, out var error))
        {
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
            if (Environment.TickCount64 - stageStarted > 30_000) throw new TimeoutException("The fuel shop did not close.");
            return;
        }
        if (!Entry.DepositToRetainers)
        { FinishEntry(Run!.FinishAfterDeposit ? FuelQueueEntryStatus.Completed : FuelQueueEntryStatus.StorageFull,
            Run.FinishAfterDeposit ? Run.FinishReason : "Inventory full; retainer deposits are disabled. Trying the next selected character."); return; }
        var bags = NativeRetainers.ReadBags(NativeFuelShop.FuelItemId, false);
        var amount = bags.Quantity > Entry.KeepCarried ? bags.Quantity - Entry.KeepCarried : 0;
        if (amount == 0)
        {
            FinishEntry(Run!.FinishAfterDeposit ? FuelQueueEntryStatus.Completed : FuelQueueEntryStatus.StorageFull,
                Run.FinishAfterDeposit ? Run.FinishReason : "No movable fuel above the keep-carried amount; trying the next selected character."); return;
        }
        IssueNative(GameOperationKind.Deposit, amount);
    }

    private void IssueNative(GameOperationKind kind, uint quantity)
    {
        var command = new GameOperation { Kind = kind, CharacterId = Entry.CharacterId, CharacterName = Entry.CharacterName,
            World = Entry.World, ItemId = NativeFuelShop.FuelItemId, Quantity = quantity,
            Destination = plugin.Config.Workflow.StorageRoutes.GetValueOrDefault(Entry.CharacterId),
            AllowedRetainerIds = plugin.Config.Workflow.AllowedRetainers.GetValueOrDefault(Entry.CharacterId) ?? [] };
        Run!.PendingOperation = command; Run.StorageCheckpoint = null;
        Transition(kind == GameOperationKind.Login ? FuelQueueStage.Login : FuelQueueStage.Deposit,
            kind == GameOperationKind.Login ? $"Logging into {Entry.CharacterName}…" : $"Depositing up to {quantity:N0} tanks…");
        if (!executor.Execute(command)) throw new InvalidOperationException("The fuel queue's native operation was not accepted.");
    }

    private void AcceptNativeResult(GameOperationResult result)
    {
        if (Run?.PendingOperation is not { } command || command.Id != result.Id) return;
        if (!result.Success) { Pause(result.Error); return; }
        Run.PendingOperation = null; Run.StorageCheckpoint = null;
        if (command.Kind == GameOperationKind.Login)
        { Transition(FuelQueueStage.Travel, $"Travelling to {Entry.CharacterName}'s fuel supplier…"); return; }
        Entry.Deposited = checked(Entry.Deposited + result.MovedQuantity);
        var afterDeposit = FuelQueueDecisions.AfterDeposit(command.Quantity, result.MovedQuantity, Run.FinishAfterDeposit);
        if (afterDeposit == FuelQueueDepositDecision.TryNextCharacter)
        { FinishEntry(FuelQueueEntryStatus.StorageFull, $"Retainers are full. {result.MovedQuantity:N0} tanks deposited; trying the next selected character."); return; }
        if (afterDeposit == FuelQueueDepositDecision.Complete) FinishEntry(FuelQueueEntryStatus.Completed, Run.FinishReason);
        else Transition(FuelQueueStage.Travel, "Fuel deposited. Returning to buy the next load…");
    }

    private void RecoverDeposit()
    {
        var command = Run!.PendingOperation!; var checkpoint = Run.StorageCheckpoint!;
        if (Environment.TickCount64 - stageStarted > 90_000)
            throw new TimeoutException("Retainer storage did not settle within 90 seconds. Close the remaining retainer windows, then Resume to tally the interrupted deposit. If this repeats, use Copy diagnostics. The saved movement remains available and no deposit was repeated.");
        if (NativeRetainers.HasNumeric || NativeRetainers.HasUnrelatedModal)
            throw new InvalidOperationException("Close the outstanding item quantity or confirmation window, then Resume to tally the deposit.");
        StorageRecoveryTally.Reconcile(command, checkpoint, Plugin.PlayerState.ContentId,
            checkpoint.BagBeforeQuantity, checkpoint.RetainerBeforeQuantity);
        if (GameWorkflowExecutor.HasVisibleStorageProgress())
        {
            storageStability.Reset();
            NativeRetainers.TryCloseTransferProgress(checkpoint.RetainerId, plugin.Workflow.RecordDiagnostic);
            return;
        }
        var bags = NativeRetainers.ReadBags(command.ItemId, false);
        uint? retainerQuantity = NativeRetainers.HasRetainerInventory && checkpoint.RetainerId != 0
            ? NativeRetainers.ReadRetainer(checkpoint.RetainerId, command.ItemId, false).Quantity : null;
        if (!storageStability.Observe(Environment.TickCount64, bags.Quantity, retainerQuantity)) return;
        var moved = StorageRecoveryTally.Reconcile(command, checkpoint, Plugin.PlayerState.ContentId, bags.Quantity, retainerQuantity);
        if (!GameWorkflowExecutor.TryCloseRecoveredStorage(checkpoint, plugin.Workflow.RecordDiagnostic)) return;
        Entry.Deposited = checked(Entry.Deposited + moved);
        Run.PendingOperation = null; Run.StorageCheckpoint = null;
        if (Run.EndRequested) { FinishEnd(); return; }
        Transition(FuelQueueStage.CloseShop, "Interrupted deposit tallied. Continuing from current inventory…");
    }

    private void FinishEntry(FuelQueueEntryStatus status, string reason)
    {
        if (Run!.PendingPurchase != null) throw new InvalidOperationException("Tally the pending purchase before moving to another character.");
        Entry.Status = status; Entry.Note = reason;
        Run!.Index++; Run.PendingOperation = null; Run.StorageCheckpoint = null;
        Run.RemainingTarget = null; Run.TargetInitialized = false; Run.FinishAfterDeposit = false; Run.FinishReason = "";
        Transition(FuelQueueStage.SelectCharacter, reason);
    }

    private void Transition(FuelQueueStage stage, string status)
    { Run!.Stage = stage; stageStarted = Environment.TickCount64; SetStatus(status); Save(); }
    private void SetStatus(string status)
    {
        if (Run == null || Run.Status == status) return;
        Run.Status = status; plugin.Workflow.RecordDiagnostic("fuel queue: " + status);
    }
    private void Save()
    {
        if (!file.TrySave(JsonConvert.SerializeObject(Run, Formatting.Indented), out var error))
        { journalHealthy = false; setupStatus = error; throw new IOException(error); }
    }
    public void Shutdown()
    {
        try
        {
            if (Run != null && !Run.Finished) { Run.Paused = true; if (journalHealthy) Save(); }
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "Fuel shutdown progress could not be saved"); }
        finally { StopHomeTravel(); plugin.Fuel.Stop(); destination.Shutdown(); executor.Shutdown(); }
    }
}
