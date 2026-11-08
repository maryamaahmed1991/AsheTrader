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
    private uint moveSourceQuantity;
    private bool movementIssued;
    private ItemInventory? beforeBags;
    private ItemInventory? beforeRetainer;
    private int inventoryStableFrames;
    private string inventoryFingerprint = string.Empty;
    private int moveStableFrames;
    private int shutdown;

    public bool IsBusy => Volatile.Read(ref shutdown) == 0 && stage != Stage.Idle;
    public string Status { get; private set; } = "No workflow operation running.";
    public Guid? ActiveOperationId => operation?.Id;

    public bool Execute(GameOperation command)
    {
        RequireFramework();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref shutdown) != 0, this);
        if (IsBusy) return false;
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(command.Kind)) throw new ArgumentException("Unknown gameplay operation.");
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
        beforeBags = beforeRetainer = null;
        ownsLifestream = ownsNavigation = restoreArSuppression = false;
        operationStarted = Environment.TickCount64;
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
            if (now - operationStarted > 1_200_000) throw new TimeoutException("The operation exceeded twenty minutes.");
            var allowance = stage is Stage.WaitLogin or Stage.WaitWorld or Stage.WaitTerritory or Stage.WaitInn ? 300_000 : 90_000;
            if (now - stageStarted > allowance) throw new TimeoutException($"Timed out while {Status}");
            if (now < actionAfter) return;
            if (stage != Stage.Begin && stage != Stage.WaitLogin)
            {
                if (!Plugin.ClientState.IsLoggedIn || Plugin.PlayerState.ContentId != operation.CharacterId || Plugin.ObjectTable.LocalPlayer == null)
                {
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
                    if (PlayerReady && Plugin.PlayerState.ContentId == operation.CharacterId && !ArBusy()) Finish(true);
                    break;
                case Stage.StartWorld: StartWorld(); break;
                case Stage.WaitWorld:
                    if (PlayerReady && CurrentWorld == travel!.WorldId && !LifestreamBusy())
                    { ownsLifestream = false; Transition(Stage.StartTeleport, "Choosing the destination aetheryte…", 500); }
                    break;
                case Stage.StartTeleport: StartTeleport(); break;
                case Stage.WaitTerritory:
                    if (PlayerReady && CurrentWorld == travel!.WorldId && Plugin.ClientState.TerritoryType == travel.TerritoryId && !LifestreamBusy())
                    { ownsLifestream = false; Transition(Stage.StartPosition, "Approaching the saved destination…", 1000); }
                    break;
                case Stage.StartPosition: StartPosition(); break;
                case Stage.WaitPosition:
                    if (DistanceTo(travel!.Position) <= 3)
                    { StopNavigation(); Arrived(); }
                    else if (!NavigationBusy() && now - stageStarted > 5000)
                        throw new InvalidOperationException("Navigation stopped before reaching the configured point.");
                    break;
                case Stage.StartStorage: StartStorage(); break;
                case Stage.WaitInn:
                    if (PlayerReady && CurrentWorld == HomeWorld && !LifestreamBusy())
                    {
                        if (NativeRetainers.FindBell() != null)
                        { ownsLifestream = false; Transition(Stage.ApproachBell, "Approaching the summoning bell…", 500); }
                        else if (now - stageStarted > 10_000) throw new InvalidOperationException("Lifestream finished without an accessible inn summoning bell.");
                    }
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
                        Transition(Stage.SelectRetainer, "Selecting the next retainer…", 500);
                    }
                    break;
                case Stage.SelectRetainer:
                    if (retainerIndex >= roster.Count || (operation.Kind != GameOperationKind.ScanRetainers && moved >= operation.Quantity))
                    { Transition(Stage.CloseList, "Closing the summoning bell…"); break; }
                    if (NativeRetainers.SelectRetainer(roster[retainerIndex]))
                        Transition(Stage.WaitRetainer, $"Waiting for {roster[retainerIndex].Name}…", 700);
                    break;
                case Stage.WaitRetainer:
                    if (SelectedRetainerIsExpected()) Transition(Stage.OpenInventory, "Opening retainer storage…", 500);
                    break;
                case Stage.OpenInventory:
                    if (NativeRetainers.SelectMenu(2378, CurrentRetainer.Id))
                    {
                        inventoryFingerprint = string.Empty; inventoryStableFrames = 0;
                        Transition(Stage.WaitInventory, "Waiting for all retainer inventory pages…", 1000);
                    }
                    break;
                case Stage.WaitInventory: WaitInventory(); break;
                case Stage.Inventory: ProcessInventory(); break;
                case Stage.WaitNumeric:
                    if (NativeRetainers.SubmitQuantity(CurrentRetainer.Id, moveQuantity, moveSourceQuantity))
                        Transition(Stage.WaitMove, "Verifying retainer item movement…", 500);
                    break;
                case Stage.WaitMove: VerifyMovement(); break;
                case Stage.CloseInventory:
                    if (!NativeRetainers.TryCloseTransferProgress(CurrentRetainer.Id)) { actionAfter = now + 500; break; }
                    NativeRetainers.HideInventory(CurrentRetainer.Id);
                    Transition(Stage.QuitRetainer, "Dismissing the retainer…", 700);
                    break;
                case Stage.QuitRetainer:
                    if (NativeRetainers.HasRetainerList) { retainerIndex++; Transition(Stage.SelectRetainer, "Selecting the next retainer…", 500); break; }
                    if (NativeRetainers.SelectMenu(2383, CurrentRetainer.Id)) Transition(Stage.WaitListAgain, "Waiting for the retainer list…", 700);
                    break;
                case Stage.WaitListAgain:
                    if (NativeRetainers.HasRetainerList) { retainerIndex++; Transition(Stage.SelectRetainer, "Selecting the next retainer…", 500); }
                    break;
                case Stage.CloseList:
                    NativeRetainers.CloseList(); Transition(Stage.WaitClosed, "Waiting for the bell to close…", 700); break;
                case Stage.WaitClosed:
                    if (!NativeRetainers.HasRetainerList && !Plugin.Condition[ConditionFlag.OccupiedSummoningBell]) Finish(true);
                    break;
            }
        }
        catch (Exception ex)
        {
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
            if (!ar.TryRelog($"{character.Name}@{character.World}", out error)) throw new InvalidOperationException(error);
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
        Plugin.PluginInterface.GetIpcSubscriber<int?, object>("Lifestream.EnqueueInnShortcut").InvokeAction(null);
        ownsLifestream = true;
        Transition(Stage.WaitInn, "Lifestream: returning home and entering an inn…", 2000);
    }

    private void StartWorld()
    {
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
        if (DistanceTo(travel.Position) <= 3) { Arrived(); return; }
        StartNavigation(travel.Position);
        Transition(Stage.WaitPosition, "Walking to the configured point…", 500);
    }

    private void Arrived()
    {
        if (storageAfterTravel) Transition(Stage.ApproachBell, "Finding the configured summoning bell…", 500);
        else Finish(true);
    }

    private void ApproachBell()
    {
        if (CurrentWorld != HomeWorld) throw new InvalidOperationException("Retainer access requires the character's home world.");
        var bell = NativeRetainers.FindBell() ?? throw new InvalidOperationException("No summoning bell was found at the storage destination.");
        if (DistanceTo(bell.Position) > 3.5f)
        {
            StartNavigation(bell.Position);
            Transition(Stage.WaitBellWalk, "Walking to the summoning bell…", 500);
            return;
        }
        NativeRetainers.InteractBell(bell);
        Transition(Stage.WaitList, "Waiting for the summoning-bell list…", 1000);
    }

    private bool SelectedRetainerIsExpected()
    {
        try { NativeRetainers.VerifyRetainer(CurrentRetainer.Id); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private void WaitInventory()
    {
        if (!NativeRetainers.HasRetainerInventory) return;
        ItemInventory inventory;
        try { inventory = NativeRetainers.ReadRetainer(CurrentRetainer.Id, operation!.ItemId, operation.HighQuality); }
        catch (InvalidOperationException) { return; }
        var fingerprint = string.Join("|", inventory.AllItems.OrderBy(x => x.Key.ItemId).ThenBy(x => x.Key.HighQuality).Select(x => $"{x.Key}:{x.Value}"));
        inventoryStableFrames = fingerprint == inventoryFingerprint ? inventoryStableFrames + 1 : 0;
        inventoryFingerprint = fingerprint;
        actionAfter = Environment.TickCount64 + 300;
        if (inventoryStableFrames >= 2) Transition(Stage.Inventory, "Inspecting retainer storage…");
    }

    private void ProcessInventory()
    {
        var op = operation!;
        var bags = NativeRetainers.ReadBags(op.ItemId, op.HighQuality);
        var retainer = NativeRetainers.ReadRetainer(CurrentRetainer.Id, op.ItemId, op.HighQuality);
        RecordRetainer(retainer.Quantity);
        if (op.Kind == GameOperationKind.ScanRetainers || moved >= op.Quantity)
        { Transition(Stage.CloseInventory, "Closing verified retainer inventory…", 300); return; }
        var deposit = op.Kind == GameOperationKind.Deposit;
        var source = deposit ? bags : retainer;
        var destination = deposit ? retainer : bags;
        var slot = source.Slots.FirstOrDefault(x => x.ItemId == op.ItemId && x.HighQuality == op.HighQuality && x.Quantity > 0 && x.Movable);
        if (slot == null || destination.Capacity == 0)
        { Transition(Stage.CloseInventory, "Continuing to the next retainer…", 300); return; }
        beforeBags = bags; beforeRetainer = retainer;
        moveQuantity = Math.Min(op.Quantity - moved, Math.Min(slot.Quantity, destination.Capacity));
        moveSourceQuantity = slot.Quantity;
        moveStableFrames = 0;
        // Set before invoking native code: a callback can reach the game before managed
        // execution reports an error. A false positive requires inspection, not a retry.
        movementIssued = true;
        var numeric = NativeRetainers.BeginMove(CurrentRetainer.Id, slot, moveQuantity, deposit);
        Transition(numeric ? Stage.WaitNumeric : Stage.WaitMove, numeric ? "Setting the exact item quantity…" : "Verifying retainer item movement…", 500);
    }

    private void VerifyMovement()
    {
        var op = operation!;
        if (NativeRetainers.HasNumeric) return;
        var bags = NativeRetainers.ReadBags(op.ItemId, op.HighQuality);
        var retainer = NativeRetainers.ReadRetainer(CurrentRetainer.Id, op.ItemId, op.HighQuality);
        var key = new ItemKey(op.ItemId, op.HighQuality);
        var deposit = op.Kind == GameOperationKind.Deposit;
        var bagsOkay = MatchesDelta(beforeBags!.AllItems, bags.AllItems, key, moveQuantity, !deposit);
        var retainerOkay = MatchesDelta(beforeRetainer!.AllItems, retainer.AllItems, key, moveQuantity, deposit);
        if (bagsOkay && retainerOkay)
        {
            if (++moveStableFrames < 2) { actionAfter = Environment.TickCount64 + 300; return; }
            moved = checked(moved + moveQuantity); RecordRetainer(retainer.Quantity);
            movementIssued = false;
            Transition(Stage.Inventory, $"Verified {moved:N0} item(s) moved…", 300);
            return;
        }
        moveStableFrames = 0;
        if (Environment.TickCount64 - stageStarted > 15_000)
            throw new InvalidOperationException("Retainer movement could not be reconciled exactly. It was not retried; inspect the inventories before resuming.");
    }

    private static bool MatchesDelta(IReadOnlyDictionary<ItemKey, ulong> before, IReadOnlyDictionary<ItemKey, ulong> after, ItemKey expected, uint quantity, bool increase)
    {
        foreach (var key in before.Keys.Union(after.Keys).Append(expected).Distinct())
        {
            var oldValue = before.GetValueOrDefault(key); var newValue = after.GetValueOrDefault(key);
            if (key != expected) { if (oldValue != newValue) return false; continue; }
            if (increase ? newValue < oldValue || newValue - oldValue != quantity : oldValue < newValue || oldValue - newValue != quantity) return false;
        }
        return true;
    }

    private void RecordRetainer(uint quantity)
    {
        observed.RemoveAll(x => x.RetainerId == CurrentRetainer.Id);
        observed.Add(new(CurrentRetainer.Id, CurrentRetainer.Name, quantity, true));
    }

    private void Finish(bool success, string error = "")
    {
        var op = operation;
        if (op == null) return;
        if (Volatile.Read(ref shutdown) != 0) { success = false; error = "Workflow unloaded while an operation was pending; reconcile before resuming."; }
        var movementUncertain = !success && (op.Kind is GameOperationKind.Withdraw or GameOperationKind.Deposit)
            && (movementIssued || stage is Stage.WaitNumeric or Stage.WaitMove);
        if (movementUncertain)
            error += " A retainer movement may already have completed. Inspect and reconcile both inventories before resuming; the pending amount must not be retried automatically.";
        StopNavigation();
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
        if (Plugin.PlayerState.ContentId == op.CharacterId && Plugin.ClientState.IsLoggedIn && op.ItemId != 0)
        {
            try
            {
                var inventory = NativeRetainers.ReadBags(op.ItemId, op.HighQuality);
                result.BagQuantity = inventory.Quantity; result.BagCapacity = inventory.Capacity; result.StackSize = inventory.StackSize;
            }
            catch (Exception ex) { result.Success = false; result.RetainerRosterVerified = false; result.Error = $"Could not verify final carried inventory: {ex.Message}"; }
        }
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
    private bool ArBusy() { var state = ar.GetStatus(); return !state.Ready || state.Busy != false; }
    private static float DistanceTo(Vector3 position) => Plugin.ObjectTable.LocalPlayer is { } player ? Vector3.Distance(player.Position, position) : float.PositiveInfinity;
    private static uint CurrentWorld => Plugin.ObjectTable.LocalPlayer?.CurrentWorld.RowId ?? 0;
    private static uint HomeWorld => Plugin.ObjectTable.LocalPlayer?.HomeWorld.RowId ?? 0;
    private static bool PlayerReady => Plugin.ClientState.IsLoggedIn && Plugin.ObjectTable.LocalPlayer != null && !Plugin.Condition[ConditionFlag.BetweenAreas] && !Plugin.Condition[ConditionFlag.BetweenAreas51];
    private RetainerIdentity CurrentRetainer => roster[retainerIndex];
    private void Transition(Stage next, string description, int delayMs = 0) { stage = next; Status = description; stageStarted = Environment.TickCount64; actionAfter = stageStarted + delayMs; }
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
