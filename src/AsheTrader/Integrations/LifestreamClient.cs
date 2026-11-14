using System.Globalization;
using AsheTrader.Core;
using Lumina.Excel.Sheets;
using HouseTuple = (string Name, int World, int City, int Ward, int PropertyType, int Plot, int Apartment, bool ApartmentSubdivision, bool AliasEnabled, string Alias);

namespace AsheTrader.Integrations;

public sealed record LifestreamStatus(bool Ready, bool? Busy, string? Error);

/// <summary>Optional typed Lifestream IPC. Every operation must run on the framework thread.</summary>
public sealed class LifestreamClient
{
    public string LastHouseImportStatus { get; private set; } = "Lifestream houses have not been read yet.";

    public LifestreamStatus GetStatus()
    {
        if (!CanCall(out var error)) return new(false, null, error);
        try
        {
            return new(true, Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(), null);
        }
        catch (Exception)
        {
            return new(false, null, "Lifestream is unavailable or its IPC is not ready.");
        }
    }

    public bool TryGetHouses(out IReadOnlyList<HouseProfile> houses, out string error) =>
        TryGetHouses([], out houses, out error);

    public bool TryGetHouses(IReadOnlyList<CharacterProfile> characters, out IReadOnlyList<HouseProfile> houses, out string error)
    {
        houses = Array.Empty<HouseProfile>();
        LastHouseImportStatus = "Lifestream houses could not be read.";
        if (!CanCall(out error)) return false;
        var addressBook = new List<HouseProfile>();
        var registered = new List<HouseProfile>();
        var warnings = new List<string>();
        var addressBookRead = false;
        var apartments = 0;
        var invalid = 0;
        try
        {
            // Upstream Lifestream/IPC/IPCProvider.cs and Data/AddressBookEntry.cs define this exact tuple.
            // This endpoint exposes Address Book entries only; the Housing tab is a separate source.
            var entries = Plugin.PluginInterface.GetIpcSubscriber<List<HouseTuple>>("Lifestream.GetAddressBookEntries").InvokeFunc();
            if (entries is null) throw new InvalidOperationException("No address book collection was returned.");
            addressBookRead = true;
            foreach (var entry in entries)
            {
                if (entry.PropertyType == 1) { apartments++; continue; }
                var name = string.IsNullOrWhiteSpace(entry.Name) ? $"{DistrictName(entry.City)} W{entry.Ward}, P{entry.Plot}" : entry.Name;
                if (LifestreamHouseImports.TryAddressBookHouse(name, entry.World, entry.City, entry.Ward, entry.PropertyType, entry.Plot, out var house))
                    addressBook.Add(house);
                else invalid++;
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Debug(exception, "Lifestream address book import failed.");
            warnings.Add("Lifestream Address Book could not be read.");
        }

        // HousePathData has no world field. Resolve each known local character's home world,
        // never the currently visited world, before reading its registered FC house.
        var owners = characters.Where(character => character.ContentId != 0).DistinctBy(character => character.ContentId).ToList();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player != null && Plugin.PlayerState.ContentId != 0 && owners.All(x => x.ContentId != Plugin.PlayerState.ContentId))
            owners.Add(new() { ContentId = Plugin.PlayerState.ContentId, Name = player.Name.TextValue, World = player.HomeWorld.Value.Name.ExtractText() });
        var registeredReads = 0;
        var registeredFailures = 0;
        var missingWorld = 0;
        var privateHouses = 0;
        foreach (var owner in owners)
        {
            var world = Plugin.DataManager.GetExcelSheet<World>().FirstOrDefault(row => row.IsPublic
                && string.Equals(row.Name.ExtractText(), owner.World.Trim(), StringComparison.OrdinalIgnoreCase));
            if (world.RowId is 0 or > ushort.MaxValue) { missingWorld++; continue; }
            try
            {
                // Dalamud JSON-converts the public return type into these detached nullable DTOs.
                // Housing-tab wards/plots are zero-based, unlike the Address Book tuple above.
                var paths = Plugin.PluginInterface.GetIpcSubscriber<ulong,
                    (LifestreamRegisteredHouse? Private, LifestreamRegisteredHouse? FC)>("Lifestream.GetHousePathData").InvokeFunc(owner.ContentId);
                registeredReads++;
                if (paths.Private != null) privateHouses++; // Private houses cannot contain company workshops.
                if (paths.FC == null) continue;
                if (LifestreamHouseImports.TryRegisteredFcHouse(owner.ContentId, checked((int)world.RowId), owner.Name, paths.FC, out var house))
                    registered.Add(house);
                else
                {
                    invalid++;
                    Plugin.Log.Debug("Rejected incompatible Lifestream FC house registration for character {CharacterId}.", owner.ContentId);
                }
            }
            catch (Exception exception)
            {
                registeredFailures++;
                if (registeredFailures == 1) Plugin.Log.Debug(exception, "Lifestream registered FC house import failed.");
            }
        }
        if (registeredFailures > 0) warnings.Add($"Registered FC houses could not be read for {registeredFailures} character(s).");
        if (missingWorld > 0) warnings.Add($"Skipped {missingWorld} housing registration lookup(s) with unknown home worlds.");
        if (owners.Count == 0) warnings.Add("Refresh the AutoRetainer roster to include registered FC houses for your characters.");
        houses = LifestreamHouseImports.Merge(addressBook, registered);
        var duplicates = addressBook.Count + registered.Count - houses.Count;
        LastHouseImportStatus = $"{houses.Count} Lifestream house(s) found: {addressBook.Count} Address Book, {registered.Count} FC registration(s)"
            + (duplicates > 0 ? $", {duplicates} duplicate address(es) combined" : "") + ".";
        if (apartments + privateHouses + invalid > 0)
            LastHouseImportStatus += $" Excluded: {apartments} apartment(s), {privateHouses} private registration(s), {invalid} invalid record(s).";
        if (registeredReads > 0 && registered.Count == 0)
            LastHouseImportStatus += " No FC registrations were returned. Check Lifestream → House Registration → Free Company House. Custom House entries need an Address Book address or manual registration here.";
        error = string.Join(" ", warnings);
        return addressBookRead || registeredReads > 0;
    }

    /// <summary>Requests travel to the plot only. Workshop entry and vendor interaction are separate.</summary>
    public bool TryTravel(HouseProfile house, out string error)
    {
        if (!CanCall(out error)) return false;
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer is null)
        {
            error = "Log in before starting travel.";
            return false;
        }
        if (!TryParseAddress(house.Address, out var destination))
        {
            error = "Import this house from Lifestream or capture its workshop before traveling.";
            return false;
        }
        var state = GetStatus();
        if (!state.Ready || state.Busy != false)
        {
            error = state.Error ?? "Lifestream is already busy.";
            return false;
        }
        try
        {
            HouseTuple address = (house.Name, destination.World, destination.City, destination.Ward,
                0, destination.Plot, 1, false, false, string.Empty);
            Plugin.PluginInterface.GetIpcSubscriber<HouseTuple, object>("Lifestream.GoToHousingAddress").InvokeAction(address);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Log.Debug(exception, "Lifestream travel request failed.");
            error = "Lifestream could not accept the house travel request.";
            return false;
        }
    }

    internal static string FormatAddress(int world, int city, int ward, int plot) =>
        FormattableString.Invariant($"ls-house:{world}:{city}:{ward}:{plot}");

    internal static bool TryParseAddress(string? address, out HouseDestination destination)
    {
        destination = default;
        var parts = address?.Split(':');
        if (parts is not { Length: 5 } || parts[0] != "ls-house"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var world)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var city)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ward)
            || !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var plot)
            || !IsValid(world, city, ward, plot)) return false;
        destination = new(world, city, ward, plot);
        return true;
    }

    internal static string DistrictName(int city) => city switch
    {
        9 => "The Goblet", 2 => "The Lavender Beds", 8 => "Mist", 70 => "Empyreum", 111 => "Shirogane", _ => "Unknown district",
    };

    private static bool IsValid(int world, int city, int ward, int plot) =>
        world is > 0 and <= ushort.MaxValue && city is 9 or 2 or 8 or 70 or 111 && ward is >= 1 and <= 30 && plot is >= 1 and <= 60;

    private static bool CanCall(out string error)
    {
        error = Plugin.Framework.IsFrameworkUnloading ? "The game framework is unloading."
            : !Plugin.Framework.IsInFrameworkUpdateThread ? "Lifestream operations must run on the game framework thread."
            : string.Empty;
        return error.Length == 0;
    }

    internal readonly record struct HouseDestination(int World, int City, int Ward, int Plot);
}
