using System.Globalization;
using AsheTrader.Core;
using HouseTuple = (string Name, int World, int City, int Ward, int PropertyType, int Plot, int Apartment, bool ApartmentSubdivision, bool AliasEnabled, string Alias);

namespace AsheTrader.Integrations;

public sealed record LifestreamStatus(bool Ready, bool? Busy, string? Error);

/// <summary>Optional typed Lifestream IPC. Every operation must run on the framework thread.</summary>
public sealed class LifestreamClient
{
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

    public bool TryGetHouses(out IReadOnlyList<HouseProfile> houses, out string error)
    {
        houses = Array.Empty<HouseProfile>();
        if (!CanCall(out error)) return false;
        try
        {
            // Upstream Lifestream/IPC/IPCProvider.cs and Data/AddressBookEntry.cs define this exact tuple.
            var entries = Plugin.PluginInterface.GetIpcSubscriber<List<HouseTuple>>("Lifestream.GetAddressBookEntries").InvokeFunc();
            if (entries is null) throw new InvalidOperationException("No address book collection was returned.");
            houses = entries.Where(entry => entry.PropertyType == 0)
                .Where(entry => IsValid(entry.World, entry.City, entry.Ward, entry.Plot))
                .Select(entry => new HouseProfile
                {
                    Name = string.IsNullOrWhiteSpace(entry.Name)
                        ? $"{DistrictName(entry.City)} W{entry.Ward}, P{entry.Plot}"
                        : entry.Name,
                    Address = FormatAddress(entry.World, entry.City, entry.Ward, entry.Plot),
                })
                .DistinctBy(house => house.Address).ToArray();
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Log.Debug(exception, "Lifestream address book import failed.");
            error = "Could not import Lifestream houses. Check that its address book IPC is available.";
            return false;
        }
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
