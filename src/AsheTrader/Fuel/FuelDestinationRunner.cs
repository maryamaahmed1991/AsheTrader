using System.Numerics;
using AsheTrader.Core;
using AsheTrader.Integrations;
using AsheTrader.Workflow.Native;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AsheTrader.Fuel;

/// <summary>Framework-thread-only travel and vendor entry. This runner never purchases or moves items.</summary>
internal sealed unsafe class FuelDestinationRunner
{
    private enum Stage { Idle, Travel, WaitTravel, Door, DoorPrompt, Interior, WorkshopDoor, WorkshopMenu, Workshop, Supplier, SupplierMenu, Shop }
    private static readonly string[] EntranceNames = ["Entrance", "ハウスへ入る", "进入房屋", "進入房屋", "Eingang", "Entrée", "주택으로 들어가기"];
    private readonly LifestreamClient lifestream = new();
    private FuelQueueEntry entry = new();
    private LifestreamClient.HouseDestination address;
    private Stage stage;
    private long stageStarted, actionAfter, lastUpdate, travelIdleSince, lastInteraction, pathStarted;
    private ulong characterId, targetId;
    private uint targetDataId, targetTerritory;
    private int attempts, pathAttempts;
    private bool ownsLifestream, ownsNavigation;
    private Vector3? caretakerWaypoint;
    private int shutdown;
    private long nextClose, closeIdleSince;
    private ulong closeCharacter, closeTarget;
    private uint closeBaseId, closeTerritory;
    private string closeRoute = string.Empty;
    private nint closeShopAddress, closeActionAddress;
    private ushort closeShopId, closeActionId;
    private int closeActionAttempts, closeTotalActions;

    public bool Active { get; private set; }
    public bool Success { get; private set; }
    public string Error { get; private set; } = string.Empty;
    public string Status { get; private set; } = "Fuel destination idle.";
    public Action<string>? Diagnostic { get; set; }

    public bool Start(FuelQueueEntry destination, out string error)
    {
        error = string.Empty;
        if (Volatile.Read(ref shutdown) != 0 || Plugin.Framework.IsFrameworkUnloading)
            error = "The fuel destination runner is shutting down.";
        else if (!Plugin.Framework.IsInFrameworkUpdateThread)
            error = "Fuel travel must start on the game framework thread.";
        else if (Active) error = "Fuel travel is already running.";
        else if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
            error = "The fuel buyer is not logged in yet.";
        else if (destination.CharacterId != 0 && destination.CharacterId != Plugin.PlayerState.ContentId)
            error = "The selected fuel buyer is not the logged-in character.";
        else if (!string.Equals(Plugin.ObjectTable.LocalPlayer!.HomeWorld.Value.Name.ExtractText(), destination.World, StringComparison.OrdinalIgnoreCase))
            error = "The fuel destination must be on this character's home world.";
        else if (!NativeRetainers.HasActiveCutscene && HasOpenInteraction)
            error = "Close the current interaction before starting fuel travel.";
        else if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.LoggingOut])
            error = "Fuel travel cannot start during combat or logout.";
        if (error.Length != 0) return false;

        if (!destination.UseCaretaker && (!LifestreamClient.TryParseAddress(destination.HouseAddress, out address)
            || address.World != Plugin.ObjectTable.LocalPlayer!.HomeWorld.RowId))
        { error = "Choose a registered workshop house on the buyer's home world."; return false; }
        if (destination.UseCaretaker && CaretakerId(destination.CaretakerCity) == 0)
        { error = "Choose a supported residential district for the Resident Caretaker."; return false; }
        var state = lifestream.GetStatus();
        if (!state.Ready || state.Busy != false)
        { error = state.Error ?? "Lifestream is already busy."; return false; }
        try
        {
            if (NavigationBusy()) { error = "Another navigation operation is running."; return false; }
        }
        catch (Exception)
        { error = "vnavmesh is unavailable or its IPC is not ready."; return false; }

        // Snapshot only the route fields; changes in setup cannot redirect an active operation.
        entry = new()
        {
            CharacterId = destination.CharacterId, World = destination.World,
            UseCaretaker = destination.UseCaretaker, CaretakerCity = destination.CaretakerCity,
            HouseAddress = destination.HouseAddress, HouseName = destination.HouseName,
        };
        characterId = Plugin.PlayerState.ContentId;
        Active = true;
        Success = false;
        Error = string.Empty;
        ownsLifestream = ownsNavigation = false;
        caretakerWaypoint = null;
        ResetClosure();
        lastUpdate = Environment.TickCount64;
        Set(Stage.Travel, destination.UseCaretaker ? "Traveling to the Resident Caretaker…" : "Traveling to the configured workshop house…");
        return true;
    }

    public bool Start(HouseProfile house, out string error) => Start(new FuelQueueEntry
    {
        CharacterId = Plugin.PlayerState.ContentId,
        World = Plugin.ObjectTable.LocalPlayer?.HomeWorld.Value.Name.ExtractText() ?? string.Empty,
        HouseName = house.Name, HouseAddress = house.Address,
    }, out error);

    public void Update()
    {
        if (!Active || Volatile.Read(ref shutdown) != 0 || Plugin.Framework.IsFrameworkUnloading
            || !Plugin.Framework.IsInFrameworkUpdateThread) return;
        var now = Environment.TickCount64;
        var elapsed = Math.Max(0, now - lastUpdate);
        lastUpdate = now;
        try
        {
            // Inn/estate cutscenes are allowed to finish naturally, including their entire timeout budget.
            if (NativeRetainers.HasActiveCutscene)
            {
                stageStarted += elapsed;
                actionAfter += elapsed;
                if (travelIdleSince != 0) travelIdleSince += elapsed;
                if (lastInteraction != 0) lastInteraction += elapsed;
                if (pathStarted != 0) pathStarted += elapsed;
                Status = "Waiting for the cutscene to finish…";
                return;
            }
            if (Plugin.Condition[ConditionFlag.InCombat]) throw new InvalidOperationException("Fuel travel stopped because the character entered combat.");
            if (Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.ContentId != characterId)
                throw new InvalidOperationException("The logged-in character changed during fuel travel.");
            var limit = stage is Stage.Travel or Stage.WaitTravel ? 900_000
                : stage is Stage.Door or Stage.WorkshopDoor or Stage.Supplier ? 300_000 : 90_000;
            if (now - stageStarted > limit)
                throw new TimeoutException($"Timed out while {Status.TrimEnd('…', '.')}. Fuel navigation stage: {stage}.");
            if (now < actionAfter) return;
            if (stage == Stage.WaitTravel) { WaitTravel(now); return; }
            if (!PlayerReady) return;
            if (stage != Stage.Travel && !AtRouteWorld())
                throw new InvalidOperationException("The character left the configured home world during fuel travel.");

            switch (stage)
            {
                case Stage.Travel:
                    // Character-login acknowledgement can precede the last animation lock or
                    // occupied frame. Allow those states to settle without pausing the queue.
                    if (NativeRetainers.BellInteractionBlocked) break;
                    if (entry.UseCaretaker && AtCaretakerDistrict(entry)) { Set(Stage.Supplier, "Approaching the Resident Caretaker…"); break; }
                    if (!entry.UseCaretaker && AtHouse(address)) { EnterHouseStage(); break; }
                    var house = entry.UseCaretaker
                        ? new HouseProfile { Name = "Resident Caretaker district", Address = LifestreamClient.FormatAddress((int)Plugin.ObjectTable.LocalPlayer!.HomeWorld.RowId, entry.CaretakerCity, 1, 1) }
                        : new HouseProfile { Name = entry.HouseName, Address = entry.HouseAddress };
                    if (!lifestream.TryTravel(house, out var travelError)) throw new InvalidOperationException(travelError);
                    ownsLifestream = true;
                    Set(Stage.WaitTravel, entry.UseCaretaker ? "Lifestream is traveling to the housing district…" : "Lifestream is traveling to the configured plot…");
                    break;
                case Stage.Door:
                    RequireHouse();
                    if (InsideHouse) { EnterHouseStage(); break; }
                    ApproachAndInteract(FindNearest(x => EntranceNames.Contains(x.Name.TextValue, StringComparer.Ordinal)), Stage.DoorPrompt, "Waiting for the estate entrance…");
                    break;
                case Stage.DoorPrompt:
                    RequireHouse();
                    if (InsideHouse) { EnterHouseStage(); break; }
                    var yesno = (AddonSelectYesno*)NativeRetainers.Ready("SelectYesno");
                    if (yesno != null)
                    {
                        RequireInteractionTarget();
                        var expected = Plugin.DataManager.GetExcelSheet<Warp>().GetRow(131148).Question.ExtractText();
                        var prompt = yesno->PromptText == null ? string.Empty : SeString.Parse(yesno->PromptText->NodeText.StringPtr.Value).TextValue;
                        if (Normalize(prompt) != Normalize(expected)) throw new InvalidOperationException("An unrelated estate confirmation appeared; it was left untouched.");
                        if (ClickYes(yesno)) Set(Stage.Interior, "Entering the estate hall…");
                    }
                    else RetryInteraction(now);
                    break;
                case Stage.Interior:
                    if (InsideHouse) { RequireHouse(); EnterHouseStage(); }
                    break;
                case Stage.WorkshopDoor:
                    RequireHouse();
                    if (InWorkshop) { Set(Stage.Supplier, "Approaching the workshop supplier…"); break; }
                    ApproachAndInteract(FindNearest(x => x.BaseId == 2004353), Stage.WorkshopMenu, "Opening the workshop entrance menu…");
                    break;
                case Stage.WorkshopMenu:
                    RequireHouse();
                    if (InWorkshop) { Set(Stage.Supplier, "Approaching the workshop supplier…"); break; }
                    if (NativeRetainers.Ready("SelectString") != null)
                    {
                        RequireInteractionTarget();
                        var label = Plugin.DataManager.GetExcelSheet<FuelDialogueRow>(name: "custom/001/CmnDefHousingPersonalRoomEntrance_00178").GetRow(11).Text.ExtractText();
                        SelectMenu(label, icon: false);
                        Set(Stage.Workshop, "Entering the company workshop…");
                    }
                    else RetryInteraction(now);
                    break;
                case Stage.Workshop:
                    if (InWorkshop) { RequireHouse(); Set(Stage.Supplier, "Approaching the workshop supplier…"); }
                    break;
                case Stage.Supplier:
                    RequireVendorLocation();
                    var npc = FindNearest(x => x.BaseId == (entry.UseCaretaker ? CaretakerId(entry.CaretakerCity) : 1011274));
                    if (npc == null && entry.UseCaretaker)
                    {
                        caretakerWaypoint ??= FindCaretakerWaypoint();
                        if (caretakerWaypoint == null) throw new InvalidOperationException("The game data has no Resident Caretaker waypoint for this district.");
                        MoveNear(caretakerWaypoint.Value, 5);
                        break;
                    }
                    ApproachAndInteract(npc, Stage.SupplierMenu, "Opening the Company Credit Exchange…");
                    break;
                case Stage.SupplierMenu:
                    RequireVendorLocation();
                    RequireInteractionTarget();
                    if (NativeFuelShop.TryRead(out _, out _)) { Finish(); break; }
                    if (AdvanceExpectedVendorTalk()) break;
                    var shopLabel = Plugin.DataManager.GetExcelSheet<FccShop>().GetRow(entry.UseCaretaker ? 2752513u : 2752515u).Name.ExtractText();
                    if (NativeRetainers.Ready("SelectIconString") != null)
                    { SelectMenu(shopLabel, icon: true); Set(Stage.Shop, "Waiting for the Ceruleum Tank shop…", resetTarget: false); }
                    else if (NativeRetainers.Ready("SelectString") != null)
                    { SelectMenu(shopLabel, icon: false); Set(Stage.Shop, "Waiting for the Ceruleum Tank shop…", resetTarget: false); }
                    else RetryInteraction(now);
                    break;
                case Stage.Shop:
                    RequireVendorLocation();
                    RequireInteractionTarget();
                    if (NativeFuelShop.TryRead(out _, out _)) Finish();
                    break;
            }
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            Status = Error;
            Active = false;
            Success = false;
            Trace($"stopped stage={stage}; error={Error}");
            CleanupOwnedServices();
        }
    }

    private void WaitTravel(long now)
    {
        var state = lifestream.GetStatus();
        if (!state.Ready) throw new InvalidOperationException(state.Error ?? "Lifestream became unavailable during fuel travel.");
        if (state.Busy == true || !PlayerReady) { travelIdleSince = 0; return; }
        if (travelIdleSince == 0) travelIdleSince = now;
        // Lifestream's queue can empty before the territory's objects and HousingManager have settled.
        if (now - travelIdleSince < 1_000) return;
        if (entry.UseCaretaker ? AtCaretakerDistrict(entry) : AtHouse(address))
        {
            ownsLifestream = false;
            if (entry.UseCaretaker) Set(Stage.Supplier, "Approaching the Resident Caretaker…");
            else EnterHouseStage();
        }
        else if (now - travelIdleSince > 20_000)
            throw new InvalidOperationException("Lifestream finished, but the configured fuel destination could not be verified. Check district unlocks and house access.");
    }

    private void EnterHouseStage() => Set(InWorkshop ? Stage.Supplier : InsideHouse ? Stage.WorkshopDoor : Stage.Door,
        InWorkshop ? "Approaching the workshop supplier…" : InsideHouse ? "Approaching the workshop entrance…" : "Approaching the estate entrance…");

    private void ApproachAndInteract(IGameObject? target, Stage next, string text)
    {
        if (target == null)
        {
            if (Environment.TickCount64 - stageStarted > 20_000)
                throw new InvalidOperationException($"The required entrance or supplier was not found while {Status.TrimEnd('…')}.");
            return;
        }
        if (!MoveNear(target.Position, 3.5f) || NativeRetainers.BellInteractionBlocked || NativeRetainers.Ready("SelectIconString") != null) return;
        if (Plugin.TargetManager.Target?.GameObjectId != target.GameObjectId)
        { Plugin.TargetManager.Target = target; actionAfter = Environment.TickCount64 + 200; return; }
        targetId = target.GameObjectId;
        targetDataId = target.BaseId;
        targetTerritory = Plugin.ClientState.TerritoryType;
        Set(next, text, resetTarget: false);
        Interact(target);
    }

    private bool MoveNear(Vector3 position, float distance)
    {
        var now = Environment.TickCount64;
        if (Vector3.Distance(Plugin.ObjectTable.LocalPlayer!.Position, position) <= distance)
        {
            if (ownsNavigation) { StopNavigation(); actionAfter = now + 350; return false; }
            return true;
        }
        if (NativeRetainers.BellInteractionBlocked || NativeRetainers.Ready("SelectIconString") != null) return false;
        if (!Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc())
        { Status = "Waiting for vnavmesh to load the fuel destination…"; actionAfter = now + 500; return false; }
        if (ownsNavigation)
        {
            if (NavigationBusy() || now - pathStarted < 1_000) return false;
            ownsNavigation = false;
            if (pathAttempts >= 2) throw new InvalidOperationException("The fuel destination path ended before the entrance or supplier was within reach.");
        }
        if (NavigationBusy()) throw new InvalidOperationException("Another plugin started navigation while approaching the fuel destination.");
        if (!Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo").InvokeFunc(position, false))
            throw new InvalidOperationException("vnavmesh could not start a path to the fuel destination.");
        ownsNavigation = true;
        pathStarted = now;
        pathAttempts++;
        Status = entry.UseCaretaker ? "Walking to the Resident Caretaker…" : "Walking to the workshop entrance or supplier…";
        return false;
    }

    private void RetryInteraction(long now)
    {
        if (now - lastInteraction < 3_000 || NativeRetainers.BellInteractionBlocked || NativeRetainers.Ready("SelectIconString") != null) return;
        var target = RequireInteractionTarget();
        if (attempts >= 3) throw new InvalidOperationException("The entrance or supplier did not respond to three interaction attempts.");
        Interact(target);
    }

    private void Interact(IGameObject target)
    {
        var control = TargetSystem.Instance();
        if (control == null) throw new InvalidOperationException("The game's targeting system is not ready.");
        if (Vector3.Distance(Plugin.ObjectTable.LocalPlayer!.Position, target.Position) > 4.5f)
            throw new InvalidOperationException("The entrance or supplier moved out of interaction range.");
        Plugin.TargetManager.Target = target;
        control->InteractWithObject((NativeObject*)target.Address, false);
        attempts++;
        lastInteraction = Environment.TickCount64;
        actionAfter = lastInteraction + 500;
        Trace($"interact stage={stage}; target={targetId}; data={targetDataId}; attempt={attempts}");
    }

    private IGameObject RequireInteractionTarget()
    {
        var target = Plugin.ObjectTable.FirstOrDefault(x => x.GameObjectId == targetId && x.BaseId == targetDataId);
        if (target == null || targetTerritory != Plugin.ClientState.TerritoryType
            || Plugin.TargetManager.Target?.GameObjectId != targetId
            || Vector3.Distance(Plugin.ObjectTable.LocalPlayer!.Position, target.Position) > 7)
            throw new InvalidOperationException("The pending fuel interaction no longer belongs to the expected entrance or supplier.");
        return target;
    }

    private bool AdvanceExpectedVendorTalk()
    {
        var talk = (AddonTalk*)NativeRetainers.Ready("Talk");
        if (talk == null || talk->AtkTextNode220 == null) return false;
        var npc = RequireInteractionTarget();
        var speaker = SeString.Parse(talk->AtkTextNode220->NodeText.StringPtr.Value).TextValue;
        if (!string.Equals(Normalize(speaker), Normalize(npc.Name.TextValue), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An unrelated conversation appeared while opening the fuel vendor.");
        var ui = AtkStage.Instance();
        if (ui == null) return true;
        var evt = new AtkEvent { Listener = (AtkEventListener*)talk, Target = &ui->AtkEventTarget, State = new() { StateFlags = (AtkEventStateFlags)132 } };
        var data = new AtkEventData();
        talk->ReceiveEvent(AtkEventType.MouseDown, 0, &evt, &data);
        talk->ReceiveEvent(AtkEventType.MouseClick, 0, &evt, &data);
        talk->ReceiveEvent(AtkEventType.MouseUp, 0, &evt, &data);
        actionAfter = Environment.TickCount64 + 500;
        return true;
    }

    private void SelectMenu(string expected, bool icon)
    {
        if (string.IsNullOrWhiteSpace(expected)) throw new InvalidOperationException("The expected localized fuel menu label is unavailable.");
        var addon = NativeRetainers.Ready(icon ? "SelectIconString" : "SelectString");
        if (addon == null || NativeRetainers.HasUnrelatedModal || NativeRetainers.HasNumeric)
            throw new InvalidOperationException("The expected fuel menu is no longer ready.");
        var count = icon ? ((AddonSelectIconString*)addon)->PopupMenu.PopupMenu.EntryCount : ((AddonSelectString*)addon)->PopupMenu.PopupMenu.EntryCount;
        if (count is <= 0 or > 40) throw new InvalidOperationException("The fuel menu has an unsupported entry count.");
        var selected = -1;
        var labels = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var ptr = icon ? ((AddonSelectIconString*)addon)->PopupMenu.PopupMenu.EntryNames[i].Value : ((AddonSelectString*)addon)->PopupMenu.PopupMenu.EntryNames[i].Value;
            var label = ptr == null ? string.Empty : Normalize(SeString.Parse(ptr).TextValue);
            labels.Add(label);
            if (!string.Equals(label, Normalize(expected), StringComparison.Ordinal)) continue;
            if (selected >= 0) throw new InvalidOperationException("More than one fuel menu entry matched; the menu was left untouched.");
            selected = i;
        }
        if (selected < 0)
            throw new InvalidOperationException($"The expected fuel menu option '{expected}' was not available. Visible options: {string.Join(" | ", labels)}");
        var value = new AtkValue { Type = AtkValueType.Int, Int = selected };
        Trace($"select stage={stage}; menu={(icon ? "SelectIconString" : "SelectString")}; index={selected}; label={labels[selected]}");
        addon->FireCallback(1, &value, true);
    }

    private static bool ClickYes(AddonSelectYesno* addon)
    {
        var button = addon->YesButton;
        if (button == null || button->OwnerNode == null || !button->IsEnabled || !button->OwnerNode->AtkResNode.IsVisible()) return false;
        var registered = button->OwnerNode->AtkResNode.AtkEventManager.Event;
        for (var count = 0; registered != null && count < 64; count++, registered = registered->NextEvent)
        {
            if (registered->State.EventType != AtkEventType.ButtonClick || registered->Listener != (AtkEventListener*)addon) continue;
            var copy = *registered;
            var data = new AtkEventData();
            registered->Listener->ReceiveEvent(AtkEventType.ButtonClick, checked((int)registered->Param), &copy, &data);
            return true;
        }
        throw new InvalidOperationException("The estate entrance confirmation has no supported button event.");
    }

    public static bool IsExpectedLocation(FuelQueueEntry destination)
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread || Plugin.Framework.IsFrameworkUnloading || !PlayerReady) return false;
        if (destination.CharacterId != 0 && Plugin.PlayerState.ContentId != destination.CharacterId) return false;
        if (destination.UseCaretaker)
            return AtCaretakerDistrict(destination) && FindNearest(x => x.BaseId == CaretakerId(destination.CaretakerCity)) is { } npc
                && Vector3.Distance(Plugin.ObjectTable.LocalPlayer!.Position, npc.Position) <= 7;
        return LifestreamClient.TryParseAddress(destination.HouseAddress, out var house) && AtHouse(house) && InWorkshop;
    }

    private void RequireVendorLocation()
    {
        if (entry.UseCaretaker)
        { if (!AtCaretakerDistrict(entry)) throw new InvalidOperationException("The character is no longer in the configured home-world housing district."); }
        else
        { RequireHouse(); if (!InWorkshop) throw new InvalidOperationException("The character is not inside the configured house's workshop."); }
    }

    private void RequireHouse()
    { if (!AtHouse(address)) throw new InvalidOperationException("The current world, district, ward, and plot do not match the configured workshop house."); }

    private static bool AtHouse(LifestreamClient.HouseDestination target)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var housing = HousingManager.Instance();
        if (player == null || housing == null || player.CurrentWorld.RowId != target.World
            || player.HomeWorld.RowId != target.World || housing->GetCurrentWard() + 1 != target.Ward
            || housing->GetCurrentPlot() + 1 != target.Plot) return false;
        var id = housing->GetCurrentHouseId();
        if (id.WorldId != 0 && id.WorldId != target.World) return false;
        var territory = housing->IsOutside() ? Plugin.ClientState.TerritoryType : (uint)id.TerritoryTypeId;
        if (territory == 0) territory = HousingManager.GetOriginalHouseTerritoryTypeId();
        return District(territory) == target.City;
    }

    private static bool AtCaretakerDistrict(FuelQueueEntry target)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var housing = HousingManager.Instance();
        return player != null && housing != null && housing->IsOutside() && player.CurrentWorld.RowId == player.HomeWorld.RowId
            && string.Equals(player.HomeWorld.Value.Name.ExtractText(), target.World, StringComparison.OrdinalIgnoreCase)
            && District(Plugin.ClientState.TerritoryType) == target.CaretakerCity;
    }

    private static int District(uint territory) => Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.PlaceNameRegion.RowId switch
    { 24 => 9, 23 => 2, 22 => 8, 25 => 70, 2402 => 111, _ => 0 };
    private static uint CaretakerId(int city) => city switch { 8 => 1005632, 9 => 1005639, 2 => 1005646, 111 => 1019100, 70 => 1039486, _ => 0 };
    private static bool InsideHouse => HousingManager.Instance() != null && HousingManager.Instance()->IsInside();
    private static bool InWorkshop => HousingManager.Instance() != null && HousingManager.Instance()->IsInWorkshop();
    private bool AtRouteWorld() => Plugin.ObjectTable.LocalPlayer!.CurrentWorld.RowId == Plugin.ObjectTable.LocalPlayer.HomeWorld.RowId;
    private static bool PlayerReady => Plugin.ClientState.IsLoggedIn && Plugin.ObjectTable.LocalPlayer != null
        && !Plugin.Condition[ConditionFlag.BetweenAreas] && !Plugin.Condition[ConditionFlag.BetweenAreas51] && !NativeRetainers.HasActiveCutscene;
    private static bool HasOpenInteraction => NativeRetainers.HasUnrelatedModal || NativeRetainers.HasNumeric
        || NativeRetainers.HasRetainerList || NativeRetainers.HasRetainerInventory
        || NativeRetainers.Ready("SelectString") != null || NativeRetainers.Ready("SelectIconString") != null
        || NativeRetainers.Ready("Talk") != null || NativeRetainers.Ready("FreeCompanyCreditShop") != null;
    private static string Normalize(string text) => text.Replace("\u00AD", string.Empty).Trim().TrimEnd('.', '。');
    private static IGameObject? FindNearest(Func<IGameObject, bool> predicate) => Plugin.ObjectTable
        .Where(x => x.IsTargetable && predicate(x)).OrderBy(x => Vector3.DistanceSquared(Plugin.ObjectTable.LocalPlayer!.Position, x.Position)).FirstOrDefault();
    private Vector3? FindCaretakerWaypoint()
    {
        var id = CaretakerId(entry.CaretakerCity);
        // Use the game's own placements, including subdivision positions, instead of version-specific coordinates.
        var positions = Plugin.DataManager.GetExcelSheet<Level>().Where(x => x.Territory.RowId == Plugin.ClientState.TerritoryType && x.Object.RowId == id)
            .Select(x => new Vector3(x.X, x.Y, x.Z)).ToList();
        if (positions.Count == 0)
        {
            // Newer residential districts keep some static NPC placements only in their
            // event layout. They cannot be discovered through the object table at a distance.
            var bg = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRow(Plugin.ClientState.TerritoryType).Bg.ExtractText();
            var slash = bg.LastIndexOf('/');
            if (slash > 0)
            {
                foreach (var name in new[] { "planevent", "planlive" })
                {
                    var path = $"bg/{bg[..slash]}/{name}.lgb";
                    try
                    {
                        var layout = Plugin.DataManager.GetFile<LgbFile>(path);
                        if (layout?.Layers == null) continue;
                        foreach (var layer in layout.Layers)
                        foreach (var obj in layer.InstanceObjects)
                        {
                            if (obj.Object is not LayerCommon.ENPCInstanceObject npc || npc.ParentData.ParentData.BaseId != id) continue;
                            var p = obj.Transform.Translation;
                            if (float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)) positions.Add(new(p.X, p.Y, p.Z));
                        }
                    }
                    catch (Exception exception) { Plugin.Log.Debug(exception, "Could not read fuel caretaker layout {Path}.", path); }
                }
            }
        }
        Trace($"caretaker waypoints territory={Plugin.ClientState.TerritoryType}; npc={id}; count={positions.Count}");
        return positions.OrderBy(x => Vector3.DistanceSquared(Plugin.ObjectTable.LocalPlayer!.Position, x)).Select(x => (Vector3?)x).FirstOrDefault();
    }

    private void Set(Stage next, string status, bool resetTarget = true)
    {
        StopNavigation();
        stage = next;
        Status = status;
        stageStarted = Environment.TickCount64;
        actionAfter = stageStarted + 250;
        travelIdleSince = lastInteraction = pathStarted = 0;
        attempts = pathAttempts = 0;
        if (resetTarget) { targetId = 0; targetDataId = targetTerritory = 0; }
        Trace($"stage={stage}; status={status}");
    }

    private void Finish()
    {
        if (!IsExpectedLocation(entry)) throw new InvalidOperationException("The opened fuel shop could not be bound to the configured destination.");
        BindClosure(entry);
        CleanupOwnedServices();
        Active = false;
        Success = true;
        Error = string.Empty;
        Status = "The Ceruleum Tank shop is ready.";
        Trace("fuel shop ready");
    }

    public void Stop()
    {
        Active = false;
        Success = false;
        Status = "Fuel destination stopped.";
        ScheduleCleanup();
    }

    public void Shutdown()
    {
        if (Interlocked.Exchange(ref shutdown, 1) != 0) return;
        Active = false;
        ScheduleCleanup();
    }

    private void ScheduleCleanup()
    {
        try
        {
            if (Plugin.Framework.IsFrameworkUnloading) return;
            if (Plugin.Framework.IsInFrameworkUpdateThread) CleanupOwnedServices();
            else _ = Plugin.Framework.RunOnFrameworkThread(CleanupOwnedServices).ContinueWith(task =>
            {
                if (task.Exception != null)
                    try { Plugin.Log.Warning(task.Exception, "Could not clean up fuel travel services."); } catch { }
            }, TaskScheduler.Default);
        }
        catch (Exception exception)
        { try { Plugin.Log.Warning(exception, "Could not schedule fuel travel cleanup."); } catch { } }
    }

    private static bool NavigationBusy() => Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc()
        || Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc();
    private void StopNavigation()
    {
        if (!ownsNavigation) return;
        ownsNavigation = false;
        try { Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction(); }
        catch (Exception exception) { Plugin.Log.Debug(exception, "Fuel navigation cleanup failed."); }
    }
    private void CleanupOwnedServices()
    {
        if (Plugin.Framework.IsFrameworkUnloading || !Plugin.Framework.IsInFrameworkUpdateThread) return;
        StopNavigation();
        if (!ownsLifestream) return;
        ownsLifestream = false;
        try { Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); }
        catch (Exception exception) { Plugin.Log.Debug(exception, "Fuel Lifestream cleanup failed."); }
    }
    private void Trace(string text)
    {
        Plugin.Log.Debug("Fuel destination: {Trace}", text);
        try { Diagnostic?.Invoke(text); } catch { /* Diagnostics must not interrupt gameplay cleanup. */ }
    }

    /// <summary>Closes the verified vendor interaction, including its returning menu or farewell.</summary>
    public bool TryCloseShop(FuelQueueEntry destination, out string error)
    {
        error = string.Empty;
        if (!Plugin.Framework.IsInFrameworkUpdateThread || Plugin.Framework.IsFrameworkUnloading)
        { error = "Fuel shop closure requires the game framework thread."; return false; }
        if (!PlayerReady) return false;
        try
        {
            var now = Environment.TickCount64;
            var shop = NativeRetainers.Ready("FreeCompanyCreditShop");
            var iconMenu = NativeRetainers.Ready("SelectIconString");
            var textMenu = NativeRetainers.Ready("SelectString");
            var talk = (AddonTalk*)NativeRetainers.Ready("Talk");
            if (NativeRetainers.Ready("Trade") != null || NativeRetainers.HasRetainerList || NativeRetainers.HasRetainerInventory || NativeRetainers.HasNumeric)
                throw new InvalidOperationException("Another interaction appeared while closing the fuel vendor; it was left untouched.");
            // The final purchase's yes/no can disappear a frame after the inventory/credit update.
            // Never confirm or cancel it here; wait for normal purchase reconciliation to settle.
            if (NativeFuelShop.HasConfirmation) { closeIdleSince = 0; return false; }
            if (shop == null && iconMenu == null && textMenu == null && talk == null)
            {
                if (Plugin.Condition[ConditionFlag.Occupied] || Plugin.Condition[ConditionFlag.OccupiedInEvent]
                    || Plugin.Condition[ConditionFlag.OccupiedInQuestEvent]) { closeIdleSince = 0; return false; }
                if (closeIdleSince == 0) closeIdleSince = now;
                // Do not start travel in the empty frame between shop closure and the NPC menu.
                if (now - closeIdleSince < 750) return false;
                ResetClosure();
                return true;
            }
            closeIdleSince = 0;
            if (closeTarget == 0) BindClosure(destination);
            var supplier = RequireClosureIdentity(destination);
            if (shop != null)
            {
                if (!NativeFuelShop.TryRead(out var snapshot, out var readError)) throw new InvalidOperationException(readError);
                if (closeShopAddress == 0) { closeShopAddress = snapshot.Address; closeShopId = snapshot.AddonId; }
                if (snapshot.Address != closeShopAddress || snapshot.AddonId != closeShopId)
                    throw new InvalidOperationException("The fuel shop changed during closure; the new shop was left untouched.");
                CancelOwnedAddon(shop, now, "FreeCompanyCreditShop");
                return false;
            }
            if (iconMenu != null && textMenu != null)
                throw new InvalidOperationException("Two selection menus appeared while closing the fuel vendor; both were left untouched.");
            if (iconMenu != null || textMenu != null)
            {
                var menu = iconMenu != null ? iconMenu : textMenu;
                var expected = Plugin.DataManager.GetExcelSheet<FccShop>().GetRow(destination.UseCaretaker ? 2752513u : 2752515u).Name.ExtractText();
                if (!IsSupplierMenu(menu, iconMenu != null, expected))
                    throw new InvalidOperationException("The returning selection menu does not match the expected Company Credit Exchange; it was left untouched.");
                CancelOwnedAddon(menu, now, iconMenu != null ? "SelectIconString" : "SelectString");
                return false;
            }
            if (talk != null)
            {
                var speaker = talk->AtkTextNode220;
                if (speaker == null || speaker->NodeText.StringPtr.Value == null
                    || !string.Equals(Normalize(SeString.Parse(speaker->NodeText.StringPtr.Value).TextValue), Normalize(supplier.Name.TextValue), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("An unrelated conversation appeared during fuel vendor closure; it was left untouched.");
                var ui = AtkStage.Instance();
                if (ui == null) return false;
                if (!CanCloseAction((AtkUnitBase*)talk, now)) return false;
                var evt = new AtkEvent { Listener = (AtkEventListener*)talk, Target = &ui->AtkEventTarget, State = new() { StateFlags = (AtkEventStateFlags)132 } };
                var data = new AtkEventData();
                talk->ReceiveEvent(AtkEventType.MouseDown, 0, &evt, &data);
                talk->ReceiveEvent(AtkEventType.MouseClick, 0, &evt, &data);
                talk->ReceiveEvent(AtkEventType.MouseUp, 0, &evt, &data);
                Trace($"close vendor farewell; target={closeTarget}; attempt={closeActionAttempts}");
            }
            return false;
        }
        catch (Exception exception)
        { error = exception.Message; Trace("vendor closure stopped: " + error); return false; }
    }

    private void BindClosure(FuelQueueEntry destination)
    {
        if (!IsExpectedLocation(destination)) throw new InvalidOperationException("The fuel vendor closure no longer matches the configured character and location.");
        var expectedBase = destination.UseCaretaker ? CaretakerId(destination.CaretakerCity) : 1011274;
        var supplier = Plugin.TargetManager.Target;
        if (supplier == null || supplier.BaseId != expectedBase || Vector3.Distance(Plugin.ObjectTable.LocalPlayer!.Position, supplier.Position) > 7)
            throw new InvalidOperationException("The expected fuel supplier is not the current target; no menu was closed.");
        closeCharacter = Plugin.PlayerState.ContentId;
        closeTarget = supplier.GameObjectId;
        closeBaseId = supplier.BaseId;
        closeTerritory = Plugin.ClientState.TerritoryType;
        closeRoute = ClosureRoute(destination);
        if (NativeFuelShop.TryRead(out var shop, out _)) { closeShopAddress = shop.Address; closeShopId = shop.AddonId; }
    }

    private IGameObject RequireClosureIdentity(FuelQueueEntry destination)
    {
        var target = Plugin.TargetManager.Target;
        if (closeCharacter != Plugin.PlayerState.ContentId || closeRoute != ClosureRoute(destination)
            || closeTerritory != Plugin.ClientState.TerritoryType || !IsExpectedLocation(destination)
            || target == null || target.GameObjectId != closeTarget || target.BaseId != closeBaseId
            || Vector3.Distance(Plugin.ObjectTable.LocalPlayer!.Position, target.Position) > 7)
            throw new InvalidOperationException("The fuel vendor identity changed during closure; the current interaction was left untouched.");
        return target;
    }

    private static string ClosureRoute(FuelQueueEntry destination) => destination.UseCaretaker
        ? $"{destination.CharacterId}/{destination.World}/caretaker/{destination.CaretakerCity}"
        : $"{destination.CharacterId}/{destination.World}/{destination.HouseAddress}";

    private static bool IsSupplierMenu(AtkUnitBase* addon, bool icon, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var count = icon ? ((AddonSelectIconString*)addon)->PopupMenu.PopupMenu.EntryCount : ((AddonSelectString*)addon)->PopupMenu.PopupMenu.EntryCount;
        if (count is <= 0 or > 40) return false;
        var matches = 0;
        for (var i = 0; i < count; i++)
        {
            var ptr = icon ? ((AddonSelectIconString*)addon)->PopupMenu.PopupMenu.EntryNames[i].Value : ((AddonSelectString*)addon)->PopupMenu.PopupMenu.EntryNames[i].Value;
            if (ptr != null && Normalize(SeString.Parse(ptr).TextValue) == Normalize(expected)) matches++;
        }
        return matches == 1;
    }

    private void CancelOwnedAddon(AtkUnitBase* addon, long now, string name)
    {
        if (!CanCloseAction(addon, now)) return;
        var cancel = new AtkValue { Type = AtkValueType.Int, Int = -1 };
        addon->FireCallback(1, &cancel, true);
        Trace($"close vendor window={name}; target={closeTarget}; attempt={closeActionAttempts}");
    }

    private bool CanCloseAction(AtkUnitBase* addon, long now)
    {
        if (now < nextClose) return false;
        if (closeActionAddress != (nint)addon || closeActionId != addon->Id)
        { closeActionAddress = (nint)addon; closeActionId = addon->Id; closeActionAttempts = 0; }
        if (closeActionAttempts >= 3 || closeTotalActions >= 9)
            throw new InvalidOperationException("The expected fuel vendor did not finish closing after bounded retries.");
        closeActionAttempts++;
        closeTotalActions++;
        nextClose = now + 1_000;
        return true;
    }

    private void ResetClosure()
    {
        nextClose = closeIdleSince = 0;
        closeCharacter = closeTarget = 0;
        closeBaseId = closeTerritory = 0;
        closeRoute = string.Empty;
        closeShopAddress = closeActionAddress = 0;
        closeShopId = closeActionId = 0;
        closeActionAttempts = closeTotalActions = 0;
    }
}

// Custom quest-dialogue sheets are not in Lumina's generated sheet catalog. Their
// schema consists of a key string followed by the localized value string.
[Sheet]
internal readonly struct FuelDialogueRow(ExcelPage page, uint offset, uint rowId) : IExcelRow<FuelDialogueRow>
{
    public ExcelPage ExcelPage => page;
    public uint RowOffset => offset;
    public uint RowId => rowId;
    public ReadOnlySeString Text => page.ReadString(offset + 4, offset);
    public static FuelDialogueRow Create(ExcelPage page, uint offset, uint rowId) => new(page, offset, rowId);
}
