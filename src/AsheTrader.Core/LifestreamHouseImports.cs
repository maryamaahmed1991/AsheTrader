namespace AsheTrader.Core;

/// <summary>Detached fields from Lifestream's public HousePathData IPC result.</summary>
public sealed class LifestreamRegisteredHouse
{
    public int? ResidentialDistrict { get; set; }
    public int? Ward { get; set; }
    public int? Plot { get; set; }
    public bool? IsPrivate { get; set; }
    public ulong? CID { get; set; }
}

/// <summary>The two public Lifestream house sources use different address conventions.</summary>
public static class LifestreamHouseImports
{
    public static bool TryAddressBookHouse(string? name, int world, int city, int ward, int propertyType,
        int plot, out HouseProfile house)
    {
        house = new();
        // Address Book entries already use one-based wards and plots; apartments have no workshop.
        return propertyType == 0 && TryHouse(name, world, city, ward, plot, out house);
    }

    public static bool TryRegisteredFcHouse(ulong characterId, int homeWorld, string? characterName,
        LifestreamRegisteredHouse? registered, out HouseProfile house)
    {
        house = new();
        // Registration is permitted only on the owner's home world in Lifestream. HousePathData
        // carries no world, so never attach another character's result to the current login's world.
        if (characterId == 0 || registered?.CID != characterId || registered.IsPrivate != false
            || registered.ResidentialDistrict is not { } city || registered.Ward is not (>= 0 and < 30)
            || registered.Plot is not (>= 0 and < 60)) return false;
        var name = string.IsNullOrWhiteSpace(characterName) ? "Registered FC house" : $"{characterName.Trim()} · FC house";
        return TryHouse(name, homeWorld, city, registered.Ward.Value + 1, registered.Plot.Value + 1, out house);
    }

    public static IReadOnlyList<HouseProfile> Merge(IEnumerable<HouseProfile> addressBook,
        IEnumerable<HouseProfile> registered) => addressBook.Concat(registered)
        .DistinctBy(house => house.Address, StringComparer.Ordinal).ToArray();

    private static bool TryHouse(string? name, int world, int city, int ward, int plot, out HouseProfile house)
    {
        house = new();
        var address = FormattableString.Invariant($"ls-house:{world}:{city}:{ward}:{plot}");
        if (!FuelHouseAddress.TryParse(address, out _)) return false;
        house.Address = address;
        house.Name = string.IsNullOrWhiteSpace(name) ? $"Registered house W{ward}, P{plot}" : name.Trim();
        return true;
    }
}
