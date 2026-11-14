using AsheTrader.Core;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace AsheTrader.Fuel;

/// <summary>Observes naturally logged-in characters. UI assessments never read another login's native state.</summary>
internal sealed class FuelAccessService
{
    private readonly Plugin plugin;
    private readonly Dictionary<string, uint> worlds;
    private readonly object gate = new();
    private ulong sessionCharacter;
    private uint sessionWorld;
    private long stableSince;
    private long nextCapture;
    private bool capturedThisLogin;

    public FuelAccessService(Plugin plugin)
    {
        this.plugin = plugin;
        worlds = Plugin.DataManager.GetExcelSheet<World>().Where(w => w.IsPublic)
            .ToDictionary(w => w.Name.ExtractText(), w => w.RowId, StringComparer.OrdinalIgnoreCase);
    }

    public void Update()
    {
        if (!Ready(out var character, out var world))
        {
            sessionCharacter = 0; sessionWorld = 0; capturedThisLogin = false;
            return;
        }
        var now = Environment.TickCount64;
        if (sessionCharacter != character || sessionWorld != world)
        {
            sessionCharacter = character; sessionWorld = world; stableSince = now;
            capturedThisLogin = false; nextCapture = now + 2500;
        }
        if (capturedThisLogin || now < nextCapture || now - stableSince < 2500) return;
        nextCapture = now + 2000;
        if (!TryReadCurrent(out var snapshot)) return;
        Store(snapshot, force: true);
        capturedThisLogin = true;
    }

    public FuelAccessPlan Assess(CharacterProfile character, FuelQueueEntry entry)
    {
        FuelAccessSnapshot? snapshot;
        lock (gate) plugin.Config.FuelAccessSnapshots.TryGetValue(character.ContentId, out snapshot);
        worlds.TryGetValue(character.World, out var world);
        // The caller may be previewing an unfinished entry. Never attach the selected alt's
        // snapshot to a queue entry for somebody else.
        if (entry.CharacterId != character.ContentId)
            return new() { Reason = "The selected character does not match the fuel destination preview." };
        return FuelAccessPlanner.Assess(entry, world, snapshot);
    }

    public void RefreshCurrent()
    {
        if (TryReadCurrent(out var snapshot)) Store(snapshot, force: false);
    }

    public FuelAccessPlan AssessCurrent(FuelQueueEntry entry)
    {
        if (!Ready(out var character, out var world) || character != entry.CharacterId)
            return new() { Reason = "Waiting for this character's home-world access information." };
        if (FuelDestinationRunner.CanApproachLocally(entry))
            return new()
            {
                Status = FuelAccessStatus.Eligible, Kind = FuelAccessRouteKind.AlreadyThere,
                RouteLabel = entry.UseCaretaker ? "Already in the selected district" : "Already at the selected estate",
                Reason = "Continue from the current verified location; no city or estate teleport is needed.",
            };
        if (!TryReadCurrent(out var snapshot))
            return new() { Reason = "Current-character teleport or quest state is not ready; access remains unverified." };
        Store(snapshot, force: false);
        return FuelAccessPlanner.Assess(entry, world, snapshot);
    }

    private void Store(FuelAccessSnapshot snapshot, bool force)
    {
        lock (gate)
        {
            var existing = plugin.Config.FuelAccessSnapshots;
            if (!force && existing.TryGetValue(snapshot.CharacterId, out var previous) && SameAccess(previous, snapshot)) return;
            // Replace the dictionary, keeping published snapshots immutable for readers.
            plugin.Config.FuelAccessSnapshots = new(existing) { [snapshot.CharacterId] = snapshot };
        }
        plugin.Save();
        plugin.Workflow.RecordDiagnostic($"fuel access observed character={snapshot.CharacterId}; world={snapshot.HomeWorldId}; FC={snapshot.FreeCompanyId}; "
            + $"fcTeleports={snapshot.FcTeleports.Count}; teleportsComplete={snapshot.TeleportsComplete}; cities={string.Join(",", snapshot.Cities.Select(c => $"{c.City}:attuned={c.AetheryteAttuned}/quest={c.HousingQuestComplete}"))}");
    }

    private static bool SameAccess(FuelAccessSnapshot left, FuelAccessSnapshot right) => left.Format == right.Format
        && left.CharacterId == right.CharacterId && left.HomeWorldId == right.HomeWorldId && left.FreeCompanyId == right.FreeCompanyId
        && left.TeleportsComplete == right.TeleportsComplete && (left.Cities ?? []).SequenceEqual(right.Cities)
        && (left.FcTeleports ?? []).SequenceEqual(right.FcTeleports)
        && (left.OtherEstateCities ?? []).SequenceEqual(right.OtherEstateCities);

    private static bool Ready(out ulong character, out uint world)
    {
        character = 0; world = 0;
        if (Plugin.Framework.IsFrameworkUnloading || !Plugin.Framework.IsInFrameworkUpdateThread || !Plugin.ClientState.IsLoggedIn
            || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
            || Plugin.Condition[ConditionFlag.WatchingCutscene] || Plugin.Condition[ConditionFlag.WatchingCutscene78]) return false;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || player.HomeWorld.RowId == 0 || player.CurrentWorld.RowId != player.HomeWorld.RowId
            || Plugin.PlayerState.ContentId == 0) return false;
        character = Plugin.PlayerState.ContentId; world = player.HomeWorld.RowId;
        return true;
    }

    private static unsafe bool TryReadCurrent(out FuelAccessSnapshot snapshot)
    {
        snapshot = new();
        if (!Ready(out var character, out var world) || QuestManager.Instance() == null) return false;
        try
        {
            var teleports = Plugin.AetheryteList.ToArray();
            // An empty/loading list is not evidence that all cities and estate teleports are locked.
            if (teleports.Length == 0) return false;
            var cities = FuelAccessPlanner.Cities.Select(city => new FuelCityAccess
            {
                City = city, AetheryteAttuned = teleports.Any(t => t.AetheryteId == city && !t.IsApartment && !t.IsSharedHouse),
                HousingQuestComplete = QuestManager.IsQuestComplete(FuelAccessPlanner.HousingQuestId(city)),
            }).ToArray();
            var fcEntries = teleports.Where(t => !t.IsApartment && !t.IsSharedHouse && FuelAccessPlanner.FcCity(t.AetheryteId) != 0).ToArray();
            var resolvedEstates = new List<FuelEstateTeleport>();
            foreach (var entry in fcEntries)
                if (NativeFuelTeleports.TryResolve(entry.AetheryteId, entry.SubIndex, world, out var estate, out _))
                    resolvedEstates.Add(estate);
            var estates = resolvedEstates
                .OrderBy(t => t.AetheryteId).ThenBy(t => t.SubIndex).ThenBy(t => t.Ward).ThenBy(t => t.Plot).ToArray();
            snapshot = new()
            {
                CharacterId = character, HomeWorldId = world, FreeCompanyId = NativeFuelShop.CurrentCompanyId,
                ObservedAtUtc = DateTimeOffset.UtcNow, TeleportsComplete = estates.Length == fcEntries.Length, Cities = cities, FcTeleports = estates,
                OtherEstateCities = teleports.Select(t => t.AetheryteId switch
                    { 59 => 8, 60 => 2, 61 => 9, 97 => 111, 165 => 70, _ => 0 }).Where(c => c != 0).Distinct().Order().ToArray(),
            };
            // A relog cannot race a framework tick, but keep identity checks explicit for future callers.
            return Ready(out var afterCharacter, out var afterWorld) && afterCharacter == character && afterWorld == world;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Housing access could not be observed yet");
            return false;
        }
    }
}
