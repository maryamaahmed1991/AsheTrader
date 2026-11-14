namespace AsheTrader.Core;

public enum FuelAccessStatus { Unknown, Eligible, Ineligible }
public enum FuelAccessRouteKind { Unknown, DirectFcEstate, CityAethernet, Unavailable, AlreadyThere }

/// <summary>A character's observed teleport entry, not a saved Lifestream house registration.</summary>
public sealed record FuelEstateTeleport
{
    public uint AetheryteId { get; init; }
    public byte SubIndex { get; init; }
    public uint World { get; init; }
    public int City { get; init; }
    public int Ward { get; init; }
    public int Plot { get; init; }
    public FuelHouseAddress Address => new((int)World, City, Ward, Plot);
}

public sealed record FuelCityAccess
{
    public int City { get; init; }
    public bool? AetheryteAttuned { get; init; }
    public bool? HousingQuestComplete { get; init; }
}

/// <summary>Persisted only from this character's live state; no inventory-discovery logins are needed.</summary>
public sealed record FuelAccessSnapshot
{
    public int Format { get; init; } = 1;
    public ulong CharacterId { get; init; }
    public uint HomeWorldId { get; init; }
    public ulong FreeCompanyId { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public bool TeleportsComplete { get; init; }
    public IReadOnlyList<FuelCityAccess> Cities { get; init; } = [];
    public IReadOnlyList<FuelEstateTeleport> FcTeleports { get; init; } = [];
    public IReadOnlyList<int> OtherEstateCities { get; init; } = [];
}

public sealed record FuelAccessPlan
{
    public FuelAccessStatus Status { get; init; }
    public FuelAccessRouteKind Kind { get; init; }
    public string RouteLabel { get; init; } = "Check on login";
    public string Reason { get; init; } = "This character's housing access has not been observed yet.";
    public string RecommendedRoute { get; init; } = string.Empty;
    public int? RecommendedCaretakerCity { get; init; }
    public DateTimeOffset? SnapshotObservedAtUtc { get; init; }
    public FuelEstateTeleport? Teleport { get; init; }
}

public static class FuelAccessPlanner
{
    public static readonly int[] Cities = [8, 2, 9, 70, 111];

    public static uint HousingQuestId(int city) => city switch
    {
        2 => 66748, 9 => 66749, 8 => 66750, 111 => 68167, 70 => 69708, _ => 0,
    };

    public static string HousingQuestName(int city) => city switch
    {
        2 => "Where the Heart Is (The Lavender Beds)", 9 => "Where the Heart Is (The Goblet)",
        8 => "Where the Heart Is (Mist)", 111 => "I Dream of Shirogane", 70 => "Ascending to Empyreum", _ => "housing unlock quest",
    };

    public static string DistrictName(int city) => city switch
    { 8 => "Mist", 2 => "Lavender Beds", 9 => "The Goblet", 70 => "Empyreum", 111 => "Shirogane", _ => "Unknown district" };

    public static int FcCity(uint aetheryteId) => aetheryteId switch
    { 56 => 8, 57 => 2, 58 => 9, 96 => 111, 164 => 70, _ => 0 };

    public static FuelAccessPlan Assess(FuelQueueEntry entry, uint expectedWorld, FuelAccessSnapshot? snapshot)
    {
        var unknown = new FuelAccessPlan { SnapshotObservedAtUtc = snapshot?.ObservedAtUtc };
        if (expectedWorld == 0 || entry.CharacterId == 0) return unknown with { Reason = "Character or home world could not be verified." };
        if (snapshot == null) return unknown;
        if (snapshot.Format != 1 || snapshot.CharacterId != entry.CharacterId || snapshot.HomeWorldId != expectedWorld)
            return unknown with { Reason = "No access snapshot matching this character and home world is available." };
        // FC teleports are character membership data. A roster change invalidates both positive
        // and negative cached conclusions until that character is next observed naturally.
        if (entry.FreeCompanyKnown && snapshot.FreeCompanyId != entry.FreeCompanyId)
            return unknown with { Reason = "FC membership changed since the last access check; recheck on login." };
        FuelHouseAddress house = default;
        if (!entry.UseCaretaker && (!FuelHouseAddress.TryParse(entry.HouseAddress, out house) || house.World != expectedWorld))
            return unknown with { Reason = "Choose a saved house on this character's home world." };
        var city = entry.UseCaretaker ? entry.CaretakerCity : house.City;
        if (!Cities.Contains(city)) return unknown with { Reason = "Choose a supported residential district." };

        var teleports = (snapshot.FcTeleports ?? []).Where(t => IsValid(t, expectedWorld)).ToArray();
        var direct = teleports.FirstOrDefault(t => t.City == city && (entry.UseCaretaker || t.Address == house));
        if (direct != null)
            return unknown with
            {
                Status = FuelAccessStatus.Eligible, Kind = FuelAccessRouteKind.DirectFcEstate, Teleport = direct,
                RouteLabel = entry.UseCaretaker ? $"FC estate → {DistrictName(city)} caretaker" : "Direct FC estate teleport",
                Reason = entry.UseCaretaker
                    ? "Use the available FC estate teleport as an entry point, then walk to a caretaker in that ward."
                    : "An available FC estate teleport matches the configured world, ward and plot. Workshop entry still depends on estate permissions.",
            };

        var access = snapshot.Cities?.FirstOrDefault(c => c.City == city);
        if (access?.AetheryteAttuned == true && access.HousingQuestComplete == true)
            return unknown with
            {
                Status = FuelAccessStatus.Eligible, Kind = FuelAccessRouteKind.CityAethernet,
                RouteLabel = $"City aetheryte → {DistrictName(city)}",
                Reason = "City attunement and the residential-district unlock quest were both verified. Estate entry permissions are checked on arrival.",
            };

        if (snapshot.OtherEstateCities?.Contains(city) == true)
            return unknown with
            {
                Kind = snapshot.TeleportsComplete && (access?.AetheryteAttuned == false || access?.HousingQuestComplete == false)
                    ? FuelAccessRouteKind.Unavailable : FuelAccessRouteKind.Unknown,
                RouteLabel = "Alternative estate entry needs checking",
                Reason = "A private, shared or apartment estate teleport may provide district access, but this automatic route currently supports FC estate teleports only.",
                RecommendedRoute = "Enter this district through that estate teleport, then resume from an accessible caretaker or the configured estate.",
            };

        // Absence is conclusive only when the complete home-world teleport list was observed.
        // Unknown native state, old formats and partial snapshots must never silently exclude an alt.
        if (!snapshot.TeleportsComplete || access == null || access.AetheryteAttuned == null || access.HousingQuestComplete == null)
            return unknown with { Reason = "Housing access is incomplete or not yet verified; check this character on its next login." };

        var missing = new List<string>();
        if (access.AetheryteAttuned != true) missing.Add("city aetheryte attunement");
        if (access.HousingQuestComplete != true) missing.Add($"“{HousingQuestName(city)}”");
        var recommendation = teleports.OrderBy(t => t.City).FirstOrDefault();
        var accessibleCity = recommendation?.City ?? snapshot.Cities?.Where(c => c.AetheryteAttuned == true && c.HousingQuestComplete == true)
            .OrderBy(c => c.City).Select(c => (int?)c.City).FirstOrDefault();
        return unknown with
        {
            Status = FuelAccessStatus.Ineligible, Kind = FuelAccessRouteKind.Unavailable, RouteLabel = "No verified route",
            Reason = $"The configured route needs {string.Join(" and ", missing)}; no matching FC estate teleport was observed. This does not mean the district can never be entered.",
            RecommendedCaretakerCity = accessibleCity,
            RecommendedRoute = accessibleCity is { } alternative
                ? $"Choose the {DistrictName(alternative)} caretaker, or unlock the configured city route."
                : "Complete the residential unlock and attune to its city aetheryte, or configure a destination reached by an available FC estate teleport.",
        };
    }

    public static bool IsValid(FuelEstateTeleport teleport, uint expectedWorld) => expectedWorld is > 0 and <= ushort.MaxValue && teleport.World == expectedWorld
        && teleport.City != 0 && FcCity(teleport.AetheryteId) == teleport.City
        && teleport.Ward is >= 1 and <= 30 && teleport.Plot is >= 1 and <= 60;
}
