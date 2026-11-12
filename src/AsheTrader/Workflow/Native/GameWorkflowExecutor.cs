using System.Numerics;
using AsheTrader.Core;
using AsheTrader.Integrations;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;

namespace AsheTrader.Workflow.Native;

/// <summary>
/// Executes exactly one coordinator-issued operation. No task is resumed implicitly.
/// Native state is touched exclusively from Update/Execute on the framework thread.
/// Retainer actions require in-game validation against the installed client version.
/// </summary>
public sealed class GameWorkflowExecutor
{
    private enum Stage
    {
        Idle, Begin, WaitLogin, StartWorld, WaitWorld, StartTeleport, WaitTerritory, StartPosition, WaitPosition,
        StartStorage, WaitInn, ApproachBell, WaitBellWalk, WaitList, SelectRetainer, WaitRetainer,
        OpenInventory, WaitInventory, Inventory, WaitNumeric, WaitMove, CloseInventory, QuitRetainer, WaitListAgain, CloseList, WaitClosed,
    }

    private readonly Queue<GameOperationResult> results = new();
    private readonly AutoRetainerClient ar = new(Plugin.PluginInterface, Plugin.Framework, Plugin.Log);
    private Stage stage;
    private GameOperation? operation;
    private TravelLocation? travel;
    private bool storageAfterTravel;
    private bool ownsLifestream;
    private bool ownsNavigation;
    private bool ownsRelog;
    private long? relogIdleSince;
    private long? worldTravelIdleSince;
    private bool restoreArSuppression;
    private long stageStarted;
    private long operationStarted;
    private long actionAfter;
    private IReadOnlyList<RetainerIdentity> roster = [];
    private readonly List<RetainerInventorySnapshot> observed = [];
    private int retainerIndex;
    private bool rosterLoaded;
    private uint moved;
    private uint moveQuantity;
    private NativeBagSlot? pendingMoveSource;
    private bool movementIssued;
    private int inventoryStableFrames;
    private string inventoryFingerprint = string.Empty;
    private RetainerTransferConfirmation? movementConfirmation;
    private int shutdown;
    private long nextDiagnosticAt;
    private string lastDiagnosticState = string.Empty;
    private readonly BellInteractionGate bellInteraction = new();
    private readonly CutsceneWait cutsceneWait = new();
    private readonly InnArrivalGate innArrival = new();
    private string statusBeforeCutscene = string.Empty;
    private bool conditionalInnSkipMode;

    public bool IsBusy => Volatile.Read(ref shutdown) == 0 && stage != Stage.Idle;
    public string Status { get; private set; } = "No workflow operation running.";
    public Guid? ActiveOperationId => operation?.Id;
    public Action<string>? Diagnostic { private get; set; }

    public bool Execute(GameOperation command)
    {
        RequireFramework();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref shutdown) != 0, this);
        if (IsBusy) return false;
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(command.Kind)) throw new ArgumentException("Unknown gameplay operation.");
        if (command.Kind == GameOperationKind.ReadCachedInventory) throw new ArgumentException("Cached inventory queries must be handled without invoking the native gameplay executor.");
        if (command.Id == Guid.Empty || command.CharacterId == 0) throw new ArgumentException("Operation and character IDs are required.");
        if (command.Kind is not GameOperationKind.Login && command.ItemId == 0) throw new ArgumentException("An item is required for this operation.");
        if (command.Kind is GameOperationKind.Withdraw or GameOperationKind.Deposit && command.Quantity == 0) throw new ArgumentException("A positive quantity is required.");
        operation = new GameOperation
        {
            Id = command.Id, Kind = command.Kind, CharacterId = command.CharacterId, CharacterName = command.CharacterName,
            World = command.World, ItemId = command.ItemId, HighQuality = command.HighQuality, Quantity = command.Quantity,
            Destination = Copy(command.Destination), AllowedRetainerIds = command.AllowedRetainerIds?.Distinct().ToArray() ?? [],
        };
        roster = []; observed.Clear(); rosterLoaded = false; moved = 0; retainerIndex = 0; movementIssued = false;
        movementConfirmation = null;
        pendingMoveSource = null;
        ownsLifestream = ownsNavigation = ownsRelog = restoreArSuppression = false;
        relogIdleSince = null;
        worldTravelIdleSince = null;
        inventoryReadFailure = string.Empty;
        bellInteraction.Reset();
        cutsceneWait.Reset();
        innArrival.Reset();
        statusBeforeCutscene = string.Empty;
        conditionalInnSkipMode = false;
        operationStarted = Environment.TickCount64;
        lastDiagnosticState = string.Empty;
        Trace($"operation begin; kind={command.Kind}; character={command.CharacterId}; name={NativeRetainers.DiagnosticText(command.CharacterName)}; world={NativeRetainers.DiagnosticText(command.World)}; item={command.ItemId}; hq={command.HighQuality}; quantity={command.Quantity}");
        Transition(Stage.Begin, $"Starting {command.Kind}…");
        return true;
    }

    public bool TryTakeResult(out GameOperationResult result)
    {
        if (results.Count != 0) { result = results.Dequeue(); return true; }
        result = null!; return false;
    }

    public void Stop(string reason = "Stopped by user.")
    {
        RequireFramework();
        if (!IsBusy) return;
        Finish(false, reason);
    }

    /// <summary>
    /// Unload cleanup. Does not dereference game memory or declare a pending operation complete.
    /// Service stop IPC is marshalled to the framework; AR's suppression setter is a verified
    /// managed boolean assignment and can be restored even if that framework is already stopping.
    /// </summary>
    public void Shutdown()
    {
        if (Interlocked.Exchange(ref shutdown, 1) != 0) return;
        Status = "Workflow unloaded. Reconcile any pending operation before resuming.";
        // Restore immediately so an unload cannot leave the other plugin suppressed forever.
        // Upstream AutoRetainer/Modules/IPC.cs SetSuppressed only assigns IPC.Suppressed.
        RestoreSuppression();
        try
        {
            if (Plugin.Framework.IsInFrameworkUpdateThread && !Plugin.Framework.IsFrameworkUnloading)
                CleanupOwnedServices();
            else if (!Plugin.Framework.IsFrameworkUnloading)
            {
                _ = Plugin.Framework.RunOnFrameworkThread(CleanupOwnedServices).ContinueWith(task =>
                {
                    if (task.Exception != null)
                    {
                        RestoreSuppression();
                        try { Plugin.Log.Warning(task.Exception, "Framework cleanup failed during workflow unload"); } catch { }
                    }
                }, TaskScheduler.Default);
            }
            else
            {
                // During full framework shutdown no further gameplay task can execute.
                // There is no persistent AR setting to clear; the managed flag was restored above.
                stage = Stage.Idle;
                operation = null;
            }
        }
        catch (Exception ex)
        {
            RestoreSuppression();
            try { Plugin.Log.Warning(ex, "Could not schedule owned workflow service cleanup"); } catch { }
        }
    }

    private void CleanupOwnedServices()
    {
        if (!Plugin.Framework.IsFrameworkUnloading && Plugin.Framework.IsInFrameworkUpdateThread)
        {
            StopNavigation();
            StopOwnedRelog();
            if (ownsLifestream)
            {
                try { Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); }
                catch (Exception ex) { try { Plugin.Log.Debug(ex, "Could not stop owned Lifestream operation on unload"); } catch { } }
            }
        }
        ownsLifestream = false;
        RestoreSuppression();
        stage = Stage.Idle;
        operation = null;
    }

    private void StopOwnedRelog()
    {
        if (!ownsRelog) return;
        ownsRelog = false;
        // Cancel AR first: its queued callback can otherwise start another Lifestream switch.
        try { Plugin.PluginInterface.GetIpcSubscriber<object>("AutoRetainer.PluginState.AbortAllTasks").InvokeAction(); }
        catch (Exception ex) { try { Plugin.Log.Warning(ex, "Could not stop the requested AutoRetainer character switch"); } catch { } }
    }

    private void RestoreSuppression()
    {
        if (!restoreArSuppression) return;
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(false);
            restoreArSuppression = false;
        }
        catch (Exception ex) { try { Plugin.Log.Warning(ex, "Could not restore AutoRetainer's acquired suppression flag"); } catch { } }
    }

    public void Update()
    {
        if (Volatile.Read(ref shutdown) != 0) return;
        RequireFramework();
        if (!IsBusy || operation == null) return;
        try
        {
            var now = Environment.TickCount64;
            if (now >= nextDiagnosticAt) CaptureDiagnosticSnapshot("periodic wait", false);
            var wasWatching = cutsceneWait.IsWaiting;
            var pausedFor = cutsceneWait.Observe(now, NativeRetainers.HasActiveCutscene);
            if (!pausedFor.HasValue)
            {
                if (!wasWatching)
                {
                    // Preserve retry accounting, but require fresh stable readiness after the scene.
                    bellInteraction.Observe(now, playerReady: false, interactionBlocked: true, bellOccupied: false);
                    innArrival.Reset();
                    statusBeforeCutscene = Status;
                    Status = "Waiting for the cutscene to finish naturally…";
                    Trace("cutscene wait begin; no gameplay actions or skip inputs will be sent");
                }
                return;
            }
            if (wasWatching)
            {
                operationStarted += pausedFor.Value;
                stageStarted += pausedFor.Value;
                actionAfter += pausedFor.Value;
                movementConfirmation?.Suspend(pausedFor.Value);
                if (relogIdleSince.HasValue) relogIdleSince += pausedFor.Value;
                if (worldTravelIdleSince.HasValue) worldTravelIdleSince += pausedFor.Value;
                Status = statusBeforeCutscene;
                Trace($"cutscene ended; no skip input sent; pausedMs={pausedFor.Value}; resuming observed game state");
            }
            if (now - operationStarted > 1_200_000) throw new TimeoutException("The operation exceeded twenty minutes.");
            var allowance = stage is Stage.WaitLogin or Stage.WaitWorld or Stage.WaitTerritory or Stage.WaitInn ? 300_000 : 90_000;
            if (now - stageStarted > allowance) throw new TimeoutException(TimeoutDescription());
            if (now < actionAfter) return;
            if (stage != Stage.Begin && stage != Stage.WaitLogin)
            {
                if (!Plugin.ClientState.IsLoggedIn || Plugin.PlayerState.ContentId != operation.CharacterId || Plugin.ObjectTable.LocalPlayer == null)
                {
                    if (stage == Stage.WaitWorld) worldTravelIdleSince = null;
                    if (stage == Stage.WaitInn) innArrival.Reset();
                    if (stage is Stage.WaitWorld or Stage.WaitTerritory or Stage.WaitInn) return;
                    throw new InvalidOperationException("The logged-in character changed during the operation.");
                }
            }
            if (NativeRetainers.HasUnrelatedModal && stage is not Stage.WaitLogin and not Stage.WaitWorld and not Stage.WaitInn)
                throw new InvalidOperationException("An unrelated trade or confirmation dialog interrupted the operation.");

            switch (stage)
            {
                case Stage.Begin: Begin(); break;
                case Stage.WaitLogin:
                    if (conditionalInnSkipMode && Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled").InvokeFunc())
                        throw new InvalidOperationException("AutoRetainer multi mode was enabled during login and could skip the inn cutscene. Keep multi mode off or set Skip Inn Login Cutscene to Never before resuming.");
                    var arBusy = ArBusy();
                    var lifestreamBusy = LifestreamBusy();
                    if (PlayerReady && Plugin.PlayerState.ContentId == operation.CharacterId && !arBusy && !lifestreamBusy)
                    {
                        // The command's return value only proves dispatch. Completion is this observed CID.
                        ownsRelog = false;
                        ownsLifestream = false;
                        Finish(true);
                    }
                    else
                    {
                        var atRest = PlayerReady || (!Plugin.ClientState.IsLoggedIn
                            && Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.CanAutoLogin").InvokeFunc());
                        if (!arBusy && !lifestreamBusy && atRest)
                        {
                            relogIdleSince ??= now;
                            if (now - relogIdleSince.Value >= 5000)
                                throw new InvalidOperationException("AutoRetainer stopped or declined the character switch before the requested character was ready. Close occupied menus, keep multi mode off, and check AutoRetainer/Lifestream logs before resuming.");
                        }
                        else relogIdleSince = null;
                    }
                    break;
                case Stage.StartWorld: StartWorld(); break;
                case Stage.WaitWorld:
                    var worldTravelBusy = LifestreamBusy();
                    if (PlayerReady && CurrentWorld == travel!.WorldId && !worldTravelBusy)
                    {
                        worldTravelIdleSince = null;
                        ownsLifestream = false;
                        Transition(Stage.StartTeleport, "Choosing the destination aetheryte…", 500);
                    }
                    else if (PlayerReady && !worldTravelBusy)
                    {
                        // A successful IPC return only acknowledges dispatch. DC-travel
                        // settings or availability can still make Lifestream decline it.
                        worldTravelIdleSince ??= now;
                        if (now - worldTravelIdleSince.Value >= 5000)
                            throw new InvalidOperationException($"Lifestream stopped before reaching meeting/storage world {travel!.WorldId}; the character is still on world {CurrentWorld}. Check Lifestream's data-center travel settings and destination availability, then resume. World travel was not retried automatically.");
                    }
                    else worldTravelIdleSince = null;
                    break;
                case Stage.StartTeleport: StartTeleport(); break;
                case Stage.WaitTerritory:
                    if (PlayerReady && CurrentWorld == travel!.WorldId && Plugin.ClientState.TerritoryType == travel.TerritoryId && !LifestreamBusy())
                    { ownsLifestream = false; Transition(Stage.StartPosition, "Approaching the saved destination…", 1000); }
                    break;
                case Stage.StartPosition: StartPosition(); break;
                case Stage.WaitPosition:
                    if (DistanceTo(travel!.Position) <= PositionArrivalRadius)
                    { StopNavigation(); Arrived(); }
                    else if (!NavigationBusy() && now - stageStarted > 5000)
                        throw new InvalidOperationException("Navigation stopped before reaching the configured point.");
                    break;
                case Stage.StartStorage: StartStorage(); break;
                case Stage.WaitInn:
                    var innDecision = innArrival.Observe(now, Plugin.ClientState.TerritoryType,
                        PlayerReady && CurrentWorld == HomeWorld, LifestreamBusy(), NativeRetainers.FindBell() != null);
                    if (innDecision == InnArrivalDecision.Arrived)
                    {
                        Trace($"inn arrival confirmed; territory={Plugin.ClientState.TerritoryType}; graceElapsedMs={innArrival.GraceElapsedMilliseconds(now)}; bellReady=True");
                        ownsLifestream = false;
                        Transition(Stage.ApproachBell, "Approaching the summoning bell…", 500);
                    }
                    else if (innDecision == InnArrivalDecision.TimedOut)
                        throw new InvalidOperationException($"No accessible inn summoning bell appeared after {InnArrivalGate.GraceMilliseconds / 1000} seconds of idle, ready arrival in territory {Plugin.ClientState.TerritoryType}. Check Lifestream's inn destination and wait for loading to finish before resuming. No retainer movement was issued.");
                    else if (innArrival.IsWaitingForArrival)
                        Status = "Waiting for the inn and summoning bell to finish loading…";
                    break;
                case Stage.ApproachBell: ApproachBell(); break;
                case Stage.WaitBellWalk:
                    var bell = NativeRetainers.FindBell() ?? throw new InvalidOperationException("The summoning bell disappeared.");
                    if (DistanceTo(bell.Position) <= 3.5f) { StopNavigation(); Transition(Stage.ApproachBell, "Opening the summoning bell…", 500); }
                    else if (!NavigationBusy() && now - stageStarted > 5000) throw new InvalidOperationException("Could not reach the summoning bell.");
                    break;
                case Stage.WaitList:
                    if (NativeRetainers.HasRetainerList)
                    {
                        roster = NativeRetainers.ReadRoster(); rosterLoaded = true;
                        // Scan always visits the full roster. Deposit alone honors the retainer allowlist.
                        if (operation.Kind == GameOperationKind.Deposit && operation.AllowedRetainerIds.Length != 0)
                        {
                            if (operation.AllowedRetainerIds.Any(id => roster.All(x => x.Id != id)))
                                throw new InvalidOperationException("A permitted destination retainer is absent from this character's roster.");
                            roster = roster.Where(x => operation.AllowedRetainerIds.Contains(x.Id)).ToArray();
                        }
                        Transition(Stage.SelectRetainer, "Selecting the next retainer…", StorageDelay(500));
                    }
                    else
                    {
                        var decision = ObserveBellInteraction(now);
                        if (decision == BellInteractionDecision.Exhausted)
                            throw new InvalidOperationException("The summoning bell did not open after three attempts. No retainer or item action was issued. Check that the character can interact with the bell, then resume.");
                        if (decision == BellInteractionDecision.Interact)
                        {
                            Trace($"summoning-bell open was not acknowledged; retrying approach; attempts={bellInteraction.Attempts}");
                            Transition(Stage.ApproachBell, "Retrying the unopened summoning bell…", 500);
                        }
                    }
                    break;
                case Stage.SelectRetainer:
                    if (retainerIndex >= roster.Count || (operation.Kind != GameOperationKind.ScanRetainers && moved >= operation.Quantity))
                    { Transition(Stage.CloseList, "Closing the summoning bell…"); break; }
                    if (NativeRetainers.SelectRetainer(roster[retainerIndex], Trace))
                        Transition(Stage.WaitRetainer, $"Waiting for {roster[retainerIndex].Name}…", StorageDelay(700));
                    break;
                case Stage.WaitRetainer:
                    if (SelectedRetainerIsExpected() && Plugin.Condition[ConditionFlag.OccupiedSummoningBell]
                        && (NativeRetainers.HasRetainerMenu || NativeRetainers.HasExpectedRetainerTalk(CurrentRetainer) || NativeRetainers.HasVisibleRetainerInventory))
                        Transition(Stage.OpenInventory, "Opening retainer storage…", StorageDelay(500));
                    break;
                case Stage.OpenInventory:
                    if (AdvanceOwnedRetainerTalk()) break;
                    if (NativeRetainers.HasVisibleRetainerInventory || NativeRetainers.SelectMenu(2378, CurrentRetainer.Id, Trace))
                    {
                        inventoryFingerprint = string.Empty; inventoryStableFrames = 0;
                        Transition(Stage.WaitInventory, "Waiting for all retainer inventory pages…", StorageDelay(1000));
                    }
                    break;
                case Stage.WaitInventory: WaitInventory(); break;
                case Stage.Inventory: ProcessInventory(); break;
                case Stage.WaitNumeric:
                    if (NativeRetainers.SubmitQuantity(CurrentRetainer.Id,
                        pendingMoveSource ?? throw new InvalidOperationException("The pending retainer quantity has no source slot."),
                        moveQuantity, operation.Kind == GameOperationKind.Deposit, Trace))
                        Transition(Stage.WaitMove, "Verifying retainer item movement…", StorageDelay(500));
                    break;
                case Stage.WaitMove: VerifyMovement(); break;
                case Stage.CloseInventory:
                    if (!NativeRetainers.TryCloseTransferProgress(CurrentRetainer.Id, Trace)) { actionAfter = now + StorageDelay(500, 100); break; }
                    NativeRetainers.HideInventory(CurrentRetainer.Id, Trace);
                    Transition(Stage.QuitRetainer, "Dismissing the retainer…", StorageDelay(700));
                    break;
                case Stage.QuitRetainer:
                    if (NativeRetainers.HasRetainerList) { retainerIndex++; Transition(Stage.SelectRetainer, "Selecting the next retainer…", StorageDelay(500)); break; }
                    if (AdvanceOwnedRetainerTalk()) break;
                    if (NativeRetainers.HasVisibleRetainerInventory) break;
                    if (NativeRetainers.SelectMenu(2383, CurrentRetainer.Id, Trace)) Transition(Stage.WaitListAgain, "Waiting for the retainer list…", StorageDelay(700));
                    break;
                case Stage.WaitListAgain:
                    if (NativeRetainers.HasRetainerList) { retainerIndex++; Transition(Stage.SelectRetainer, "Selecting the next retainer…", StorageDelay(500)); }
                    else AdvanceOwnedRetainerTalk();
                    break;
                case Stage.CloseList:
                    NativeRetainers.CloseList(Trace); Transition(Stage.WaitClosed, "Waiting for the bell to close…", StorageDelay(700)); break;
                case Stage.WaitClosed:
                    if (!NativeRetainers.HasRetainerList && !Plugin.Condition[ConditionFlag.OccupiedSummoningBell]) Finish(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace($"operation exception; type={ex.GetType().Name}; message={NativeRetainers.DiagnosticText(ex.Message)}");
            Plugin.Log.Warning(ex, "Workflow operation stopped");
            Finish(false, ex.Message);
        }
    }

    private void Begin()
    {
        if (operation!.Kind == GameOperationKind.Login)
        {
            if (PlayerReady && Plugin.PlayerState.ContentId == operation.CharacterId) { Finish(true); return; }
            if (!ar.TryGetCharacters(out var characters, out var error)) throw new InvalidOperationException(error);
            var character = characters.SingleOrDefault(x => x.ContentId == operation.CharacterId)
                ?? throw new InvalidOperationException("Requested character is not in this PC's AutoRetainer roster.");
            if (NativeRetainers.HasNumeric || NativeRetainers.HasRetainerList || Plugin.Condition[ConditionFlag.OccupiedSummoningBell])
                throw new InvalidOperationException("Close the retainer and item-quantity windows before changing characters.");
            if (LifestreamBusy()) throw new InvalidOperationException("Wait for the current Lifestream operation to finish before changing characters.");
            EnsureInnCutscenesPreserved();
            if (!ar.TryRelog($"{character.Name}@{character.World}", out error)) throw new InvalidOperationException(error);
            ownsRelog = true;
            ownsLifestream = true;
            relogIdleSince = null;
            Transition(Stage.WaitLogin, $"Logging in {character.Name}@{character.World}…", 1000);
            return;
        }
        if (!PlayerReady || Plugin.PlayerState.ContentId != operation.CharacterId) throw new InvalidOperationException("The required character is not logged in and ready.");
        if (NativeRetainers.HasNumeric || NativeRetainers.HasUnrelatedModal || NativeRetainers.HasRetainerList || Plugin.Condition[ConditionFlag.OccupiedSummoningBell])
            throw new InvalidOperationException("Close existing trade, retainer, and confirmation windows before starting this operation.");
        var state = ar.GetStatus();
        if (state.Ready && (state.Busy != false || state.MultiModeEnabled != false)) throw new InvalidOperationException("Pause AutoRetainer and wait for its current tasks to finish.");
        if (operation.Kind == GameOperationKind.Inspect) { Finish(true); return; }
        if (state.Ready && operation.Kind is GameOperationKind.ScanRetainers or GameOperationKind.Withdraw or GameOperationKind.Deposit)
        {
            var suppressed = Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed").InvokeFunc();
            if (!suppressed)
            {
                Plugin.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(true);
                restoreArSuppression = true;
            }
        }
        if (LifestreamBusy()) throw new InvalidOperationException("Lifestream is already busy.");
        if (operation.Kind == GameOperationKind.TravelMeeting)
        {
            travel = ValidateLocation(operation.Destination); storageAfterTravel = false;
            Transition(Stage.StartWorld, "Checking the meeting world…");
        }
        else Transition(Stage.StartStorage, "Preparing home-world retainer access…");
    }

    private void StartStorage()
    {
        if (operation!.Destination != null)
        {
            travel = ValidateLocation(operation.Destination);
            if (travel.WorldId != HomeWorld) throw new InvalidOperationException("The configured storage bell must be on this character's home world.");
            storageAfterTravel = true; Transition(Stage.StartWorld, "Returning to the storage world…");
            return;
        }
        if (CurrentWorld == HomeWorld && NativeRetainers.FindBell() is { } bell && DistanceTo(bell.Position) < 40)
        { Transition(Stage.ApproachBell, "Approaching the nearby home-world bell…"); return; }
        innArrival.Reset();
        Plugin.PluginInterface.GetIpcSubscriber<int?, object>("Lifestream.EnqueueInnShortcut").InvokeAction(null);
        ownsLifestream = true;
        Transition(Stage.WaitInn, "Lifestream: returning home and entering an inn…", 2000);
    }

    private void StartWorld()
    {
        worldTravelIdleSince = null;
        if (CurrentWorld == travel!.WorldId) { Transition(Stage.StartTeleport, "Checking destination territory…"); return; }
        if (!Plugin.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById").InvokeFunc(travel.WorldId))
            throw new InvalidOperationException("Lifestream could not start the world change.");
        ownsLifestream = true; Transition(Stage.WaitWorld, "Lifestream: changing worlds…", 1500);
    }

    private void StartTeleport()
    {
        if (Plugin.ClientState.TerritoryType == travel!.TerritoryId) { Transition(Stage.StartPosition, "Approaching the saved point…"); return; }
        if (travel.AetheryteId == 0) throw new InvalidOperationException("This destination needs an aetheryte ID before it can be reached from another territory.");
        var aetheryte = Plugin.DataManager.GetExcelSheet<Aetheryte>().GetRowOrDefault(travel.AetheryteId);
        if (!aetheryte.HasValue || !aetheryte.Value.IsAetheryte || aetheryte.Value.Territory.RowId != travel.TerritoryId)
            throw new InvalidOperationException("The configured aetheryte does not lead to the destination territory.");
        if (!Plugin.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport").InvokeFunc(travel.AetheryteId, 0))
            throw new InvalidOperationException("Lifestream could not teleport to the configured aetheryte.");
        ownsLifestream = true; Transition(Stage.WaitTerritory, "Teleporting to the destination territory…", 1500);
    }

    private void StartPosition()
    {
        if (!PlayerReady || CurrentWorld != travel!.WorldId || Plugin.ClientState.TerritoryType != travel.TerritoryId) return;
        if (DistanceTo(travel.Position) <= PositionArrivalRadius) { Arrived(); return; }
        StartNavigation(travel.Position);
        Transition(Stage.WaitPosition, "Walking to the configured point…", 500);
    }

    private void Arrived()
    {
        Trace($"destination reached; distance={DistanceTo(travel!.Position):F2}; arrivalRadius={PositionArrivalRadius:F2}; world={CurrentWorld}; territory={Plugin.ClientState.TerritoryType}");
        if (storageAfterTravel) Transition(Stage.ApproachBell, "Finding the configured summoning bell…", 500);
        else Finish(true);
    }

    private float PositionArrivalRadius => operation?.Kind == GameOperationKind.TravelMeeting
        ? AsheTrader.Trading.TradeOpeningGate.MeetingArrivalRadius : 3f;

    private void ApproachBell()
    {
        if (CurrentWorld != HomeWorld) throw new InvalidOperationException("Retainer access requires the character's home world.");
        if (NativeRetainers.HasRetainerList)
        {
            // A delayed response can arrive while a retry is waiting to approach.
            Transition(Stage.WaitList, "Reading the opened summoning-bell list…");
            return;
        }
        var now = Environment.TickCount64;
        if (ObserveBellInteraction(now) != BellInteractionDecision.Interact)
        {
            actionAfter = now + 250;
            return;
        }
        var bell = NativeRetainers.FindBell() ?? throw new InvalidOperationException("No summoning bell was found at the storage destination.");
        if (DistanceTo(bell.Position) > 3.5f)
        {
            StartNavigation(bell.Position);
            Transition(Stage.WaitBellWalk, "Walking to the summoning bell…", 500);
            return;
        }
        bellInteraction.RecordAttempt(now);
        Trace($"summoning-bell open attempt={bellInteraction.Attempts}/{BellInteractionGate.MaximumAttempts}");
        NativeRetainers.InteractBell(bell, Trace);
        Transition(Stage.WaitList, "Waiting for the summoning-bell list…", 1000);
    }

    private BellInteractionDecision ObserveBellInteraction(long now) => bellInteraction.Observe(now,
        PlayerReady && Plugin.ObjectTable.LocalPlayer?.IsTargetable == true,
        NativeRetainers.BellInteractionBlocked,
        Plugin.Condition[ConditionFlag.OccupiedSummoningBell]);

    private bool SelectedRetainerIsExpected()
    {
        try { NativeRetainers.VerifyRetainer(CurrentRetainer.Id); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private bool AdvanceOwnedRetainerTalk()
    {
        if (!SelectedRetainerIsExpected() || !NativeRetainers.AdvanceRetainerTalk(CurrentRetainer, Trace)) return false;
        // A speaker can have multiple lines. Do not send another click every frame,
        // and retain the existing stage deadline if a dialogue does not advance.
        actionAfter = Environment.TickCount64 + StorageDelay(700, 200);
        return true;
    }

    private string TimeoutDescription()
    {
        var expected = retainerIndex < roster.Count ? $" Retainer: {CurrentRetainer.Name}." : string.Empty;
        return $"Timed out while {Status} Stage: {stage}.{expected} {NativeRetainers.DescribeStorageUi()}. "
            + "Open Activity and use Copy diagnostics when reporting this pause. No item movement was retried automatically.";
    }

    private void WaitInventory()
    {
        if (!NativeRetainers.HasVisibleRetainerInventory) return;
        ItemInventory inventory;
        try { inventory = NativeRetainers.ReadRetainer(CurrentRetainer.Id, operation!.ItemId, operation.HighQuality); }
        catch (InvalidOperationException exception)
        {
            // Repeated reads are polled, but the periodic page/identity snapshot explains the wait.
            inventoryReadFailure = NativeRetainers.DiagnosticText(exception.Message);
            return;
        }
        inventoryReadFailure = string.Empty;
        var fingerprint = string.Join("|", inventory.AllItems.OrderBy(x => x.Key.ItemId).ThenBy(x => x.Key.HighQuality).Select(x => $"{x.Key}:{x.Value}"));
        inventoryStableFrames = fingerprint == inventoryFingerprint ? inventoryStableFrames + 1 : 0;
        inventoryFingerprint = fingerprint;
        actionAfter = Environment.TickCount64 + StorageDelay(300, 100);
        if (inventoryStableFrames >= 2) Transition(Stage.Inventory, "Inspecting retainer storage…");
    }

    private void ProcessInventory()
    {
        var op = operation!;
        var bags = NativeRetainers.ReadBags(op.ItemId, op.HighQuality);
        var retainer = NativeRetainers.ReadRetainer(CurrentRetainer.Id, op.ItemId, op.HighQuality);
        RecordRetainer(retainer.Quantity);
        if (op.Kind == GameOperationKind.ScanRetainers || moved >= op.Quantity)
        { Transition(Stage.CloseInventory, "Closing verified retainer inventory…", StorageDelay(300)); return; }
        var deposit = op.Kind == GameOperationKind.Deposit;
        var source = deposit ? bags : retainer;
        var destination = deposit ? retainer : bags;
        var slot = source.Slots.FirstOrDefault(x => x.ItemId == op.ItemId && x.HighQuality == op.HighQuality && x.Quantity > 0 && x.Movable);
        if (slot == null || destination.Capacity == 0)
        { Transition(Stage.CloseInventory, "Continuing to the next retainer…", StorageDelay(300)); return; }
        moveQuantity = Math.Min(op.Quantity - moved, Math.Min(slot.Quantity, destination.Capacity));
        pendingMoveSource = slot;
        movementConfirmation = new(Environment.TickCount64, bags.AllItems, retainer.AllItems,
            new(op.ItemId, op.HighQuality), moveQuantity, deposit);
        // Set before invoking native code: a callback can reach the game before managed
        // execution reports an error. A false positive requires inspection, not a retry.
        movementIssued = true;
        var numeric = NativeRetainers.BeginMove(CurrentRetainer.Id, slot, moveQuantity, deposit, Trace);
        Transition(numeric ? Stage.WaitNumeric : Stage.WaitMove, numeric ? "Setting the exact item quantity…" : "Verifying retainer item movement…", StorageDelay(500));
    }

    private void VerifyMovement()
    {
        var op = operation!;
        if (NativeRetainers.HasNumeric) return;
        var bags = NativeRetainers.ReadBags(op.ItemId, op.HighQuality);
        var retainer = NativeRetainers.ReadRetainer(CurrentRetainer.Id, op.ItemId, op.HighQuality);
        var confirmation = movementConfirmation
            ?? throw new InvalidOperationException("The pending retainer movement has no starting inventory snapshot.");
        var now = Environment.TickCount64;
        // Both directions advance on observed inventory changes with one short command
        // interval. Both exact deltas must agree on consecutive observations.
        // No timeout ever permits redispatching an unconfirmed movement.
        if (!confirmation.Observe(now, bags.AllItems, retainer.AllItems)) return;
        moved = checked(moved + moveQuantity); RecordRetainer(retainer.Quantity);
        var deposit = op.Kind == GameOperationKind.Deposit;
        Trace($"{(deposit ? "deposit" : "withdrawal")} confirmed; quantity={moveQuantity}; totalMoved={moved}; elapsedMs={confirmation.ElapsedMilliseconds(now)}; minimumIntervalMs={RetainerTransferConfirmation.MinimumIntervalMilliseconds}; sourceAndDestinationVerified=True");
        movementConfirmation = null;
        pendingMoveSource = null;
        movementIssued = false;
        Transition(Stage.Inventory, $"Verified {moved:N0} item(s) {(deposit ? "deposited" : "withdrawn")}…");
    }

    // Item movements use readiness-driven pacing in both directions. Scan-only operations
    // keep their existing timing. Repeated UI inputs retain an explicit short throttle.
    private int StorageDelay(int normalMilliseconds, int movementMilliseconds = 0)
        => operation?.Kind is GameOperationKind.Withdraw or GameOperationKind.Deposit ? movementMilliseconds : normalMilliseconds;

    private void RecordRetainer(uint quantity)
    {
        observed.RemoveAll(x => x.RetainerId == CurrentRetainer.Id);
        observed.Add(new(CurrentRetainer.Id, CurrentRetainer.Name, quantity, true));
    }

    private void Finish(bool success, string error = "")
    {
        var op = operation;
        if (op == null) return;
        if (!success) CaptureDiagnosticSnapshot("operation stopping", true);
        if (Volatile.Read(ref shutdown) != 0) { success = false; error = "Workflow unloaded while an operation was pending; reconcile before resuming."; }
        var movementUncertain = !success && (op.Kind is GameOperationKind.Withdraw or GameOperationKind.Deposit)
            && (movementIssued || stage is Stage.WaitNumeric or Stage.WaitMove);
        if (movementUncertain)
            error += " A retainer movement may already have completed. Inspect and reconcile both inventories before resuming; the pending amount must not be retried automatically.";
        StopNavigation();
        if (!success) StopOwnedRelog();
        else ownsRelog = false;
        if (ownsLifestream)
        {
            try { Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); }
            catch (Exception ex) { Plugin.Log.Debug(ex, "Could not stop owned Lifestream travel"); }
        }
        ownsLifestream = false;
        RestoreSuppression();
        var result = new GameOperationResult
        {
            Id = op.Id, Success = success, Error = error, CharacterId = op.CharacterId, MovedQuantity = moved, MovementUncertain = movementUncertain,
            RetainerRosterVerified = success && rosterLoaded && observed.Count == roster.Count && (op.Kind != GameOperationKind.Deposit || op.AllowedRetainerIds.Length == 0),
            Retainers = observed.ToList(), ObservedAt = DateTimeOffset.UtcNow,
        };
        if (Plugin.PlayerState.ContentId == op.CharacterId && Plugin.ClientState.IsLoggedIn)
            result.CurrentWorldId = CurrentWorld;
        if (Plugin.PlayerState.ContentId == op.CharacterId && Plugin.ClientState.IsLoggedIn && op.ItemId != 0)
        {
            try
            {
                var inventory = NativeRetainers.ReadBags(op.ItemId, op.HighQuality);
                result.BagQuantity = inventory.Quantity; result.BagCapacity = inventory.Capacity; result.StackSize = inventory.StackSize;
            }
            catch (Exception ex) { result.Success = false; result.RetainerRosterVerified = false; result.Error = $"Could not verify final carried inventory: {ex.Message}"; }
        }
        Trace($"operation end; success={result.Success}; stage={stage}; elapsedMs={Environment.TickCount64 - operationStarted}; currentWorld={result.CurrentWorldId}; moved={result.MovedQuantity}; uncertain={result.MovementUncertain}; bagQuantity={result.BagQuantity}; bagCapacity={result.BagCapacity}; observedRetainers={observed.Count}/{roster.Count}; error={NativeRetainers.DiagnosticText(result.Error)}");
        stage = Stage.Idle; operation = null;
        Status = result.Success ? $"{op.Kind} completed." : $"{op.Kind} paused: {result.Error}";
        results.Enqueue(result);
    }

    public static bool TryCaptureCurrentLocation(out TravelLocation? location, out string error)
    {
        location = null;
        if (!PlayerReady) { error = "Log in before recording a location."; return false; }
        var player = Plugin.ObjectTable.LocalPlayer!;
        var territory = Plugin.ClientState.TerritoryType;
        var aetheryte = Plugin.DataManager.GetExcelSheet<Aetheryte>().FirstOrDefault(x => x.IsAetheryte && x.Territory.RowId == territory);
        location = new() { WorldId = player.CurrentWorld.RowId, TerritoryId = territory, AetheryteId = aetheryte.RowId, X = player.Position.X, Y = player.Position.Y, Z = player.Position.Z };
        error = string.Empty;
        return true;
    }

    private void StartNavigation(Vector3 position)
    {
        if (!Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc())
            throw new InvalidOperationException("vnavmesh is not ready for this territory. Wait for its mesh to load, then resume.");
        if (NavigationBusy()) throw new InvalidOperationException("Another navigation operation is running.");
        if (!Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo").InvokeFunc(position, false))
            throw new InvalidOperationException("vnavmesh could not start a route to the destination.");
        ownsNavigation = true;
    }

    private static bool NavigationBusy() => Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc()
        || Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc();
    private void StopNavigation()
    {
        if (!ownsNavigation) return;
        try { Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction(); }
        catch (Exception ex) { Plugin.Log.Debug(ex, "Could not stop owned navigation"); }
        ownsNavigation = false;
    }
    private static bool LifestreamBusy() => Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
    private void EnsureInnCutscenesPreserved()
    {
        // AR's independent inn-login hook ignores the bell suppression flag. Its supported IPC
        // exposes a read-only config getter, not a scoped override. Never change its persisted config.
        int? mode = null;
        bool? multiMode = null;
        try
        {
            mode = Plugin.PluginInterface.GetIpcSubscriber<int>("AutoRetainer.GetConfig.CutsceneSkipMode").InvokeFunc();
            multiMode = Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled").InvokeFunc();
        }
        catch (Exception exception) { Plugin.Log.Debug(exception, "Could not verify AutoRetainer inn-cutscene policy"); }
        var reason = InnCutscenePolicy.LoginBlockReason(mode, multiMode);
        if (reason != null) throw new InvalidOperationException(reason);
        conditionalInnSkipMode = mode == 1;
        Trace($"inn cutscene preservation checked; AutoRetainerMode={mode}; multi={multiMode}");
    }
    private bool ArBusy() { var state = ar.GetStatus(); return !state.Ready || state.Busy != false; }
    private static float DistanceTo(Vector3 position) => Plugin.ObjectTable.LocalPlayer is { } player ? Vector3.Distance(player.Position, position) : float.PositiveInfinity;
    private static uint CurrentWorld => Plugin.ObjectTable.LocalPlayer?.CurrentWorld.RowId ?? 0;
    private static uint HomeWorld => Plugin.ObjectTable.LocalPlayer?.HomeWorld.RowId ?? 0;
    private static bool PlayerReady => Plugin.ClientState.IsLoggedIn && Plugin.ObjectTable.LocalPlayer != null
        && !Plugin.Condition[ConditionFlag.BetweenAreas] && !Plugin.Condition[ConditionFlag.BetweenAreas51]
        && !NativeRetainers.HasActiveCutscene;
    private RetainerIdentity CurrentRetainer => roster[retainerIndex];
    private string inventoryReadFailure = string.Empty;

    private void Trace(string message)
    {
        try { Diagnostic?.Invoke($"native op={operation?.Id.ToString() ?? "none"} {message}"); }
        catch { /* Reporting must not interrupt or repeat a native action. */ }
    }

    public void CaptureDiagnosticSnapshot(string reason) => CaptureDiagnosticSnapshot(reason, true);

    private void CaptureDiagnosticSnapshot(string reason, bool force)
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread || Plugin.Framework.IsFrameworkUnloading)
        {
            Trace("snapshot unavailable outside an active framework update");
            return;
        }
        var now = Environment.TickCount64;
        nextDiagnosticAt = now + 5000;
        try
        {
            Trace($"snapshot reason={NativeRetainers.DiagnosticText(reason)}; stage={stage}; stageElapsedMs={(operation == null ? 0 : now - stageStarted)}; operationElapsedMs={(operation == null ? 0 : now - operationStarted)}; loggedIn={Plugin.ClientState.IsLoggedIn}; currentCharacter={Plugin.PlayerState.ContentId}; territory={Plugin.ClientState.TerritoryType}; currentWorld={CurrentWorld}; homeWorld={HomeWorld}; playerReady={PlayerReady}; moved={moved}; movementIssued={movementIssued}; ownsAR={ownsRelog}; suppressesAR={restoreArSuppression}; ownsLS={ownsLifestream}; ownsNav={ownsNavigation}");
            var expected = operation != null && retainerIndex >= 0 && retainerIndex < roster.Count ? CurrentRetainer : null;
            uint? menuRow = stage is Stage.QuitRetainer or Stage.WaitListAgain ? 2383U
                : stage is Stage.WaitRetainer or Stage.OpenInventory or Stage.WaitInventory ? 2378U : null;
            var state = NativeRetainers.CaptureDiagnosticState(expected, menuRow,
                stage is Stage.WaitInventory or Stage.Inventory or Stage.WaitMove or Stage.WaitNumeric);
            var fingerprint = string.Join("\n", state);
            if (force || fingerprint != lastDiagnosticState)
            {
                foreach (var line in state) Trace(line);
                lastDiagnosticState = fingerprint;
            }
            else Trace("native UI state unchanged since previous snapshot");
            if (stage is Stage.WaitLogin or Stage.WaitWorld or Stage.WaitTerritory or Stage.WaitInn)
            {
                var arState = ar.GetStatus();
                Trace($"services ARready={arState.Ready}; ARbusy={arState.Busy?.ToString() ?? "unknown"}; ARmulti={arState.MultiModeEnabled?.ToString() ?? "unknown"}; LifestreamBusy={LifestreamBusy()}");
            }
            if (stage == Stage.WaitInventory)
                Trace($"inventory stability polls={inventoryStableFrames}; lastReadError={inventoryReadFailure}");
            if (stage == Stage.WaitInn)
                Trace($"inn arrival waiting={innArrival.IsWaitingForArrival}; graceElapsedMs={innArrival.GraceElapsedMilliseconds(now)}; graceLimitMs={InnArrivalGate.GraceMilliseconds}; observedTerritory={innArrival.ObservedTerritory?.ToString() ?? "none"}; currentTerritory={Plugin.ClientState.TerritoryType}; readyAtHome={PlayerReady && CurrentWorld == HomeWorld}; bellReady={NativeRetainers.FindBell() != null}");
            if (stage is Stage.ApproachBell or Stage.WaitBellWalk or Stage.WaitList)
                Trace($"bell attempts={bellInteraction.Attempts}; acknowledged={bellInteraction.Acknowledged}; playerTargetable={Plugin.ObjectTable.LocalPlayer?.IsTargetable}; interactionBlocked={NativeRetainers.BellInteractionBlocked}");
        }
        catch (Exception exception) { Trace($"snapshot failed; type={exception.GetType().Name}; message={NativeRetainers.DiagnosticText(exception.Message)}"); }
    }

    private void Transition(Stage next, string description, int delayMs = 0)
    {
        var now = Environment.TickCount64;
        Trace($"stage {stage} -> {next}; previousStageMs={(stage == Stage.Idle ? 0 : now - stageStarted)}; delayMs={delayMs}; status={NativeRetainers.DiagnosticText(description)}");
        stage = next; Status = description; stageStarted = now; actionAfter = now + delayMs;
        // Capture on the next Update after the transition, then at most every five seconds.
        nextDiagnosticAt = now;
    }
    private static TravelLocation? Copy(TravelLocation? value) => value == null ? null : new() { WorldId = value.WorldId, TerritoryId = value.TerritoryId, AetheryteId = value.AetheryteId, X = value.X, Y = value.Y, Z = value.Z };
    private static TravelLocation ValidateLocation(TravelLocation? location)
    {
        if (location == null || location.WorldId == 0 || location.TerritoryId == 0 || !float.IsFinite(location.X) || !float.IsFinite(location.Y) || !float.IsFinite(location.Z) || Math.Abs(location.X) > 10000 || Math.Abs(location.Y) > 10000 || Math.Abs(location.Z) > 10000)
            throw new ArgumentException("The configured destination is invalid.");
        return location;
    }
    private static void RequireFramework()
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread || Plugin.Framework.IsFrameworkUnloading)
            throw new InvalidOperationException("Gameplay operations must run on the active framework thread.");
    }
}
