using System.Globalization;

namespace AsheTrader.Core;

public sealed class FuelQueueSettings
{
    public Dictionary<string, FuelCompanyRule> Companies { get; set; } = [];
}

public sealed class FuelCompanyRule
{
    public string NameOverride { get; set; } = string.Empty;
    public Guid? HouseId { get; set; }
    public uint CreditReserve { get; set; }
    public bool UseCaretaker { get; set; }
    public int CaretakerCity { get; set; } = 8;
}

public enum FuelQueueEntryStatus { Queued, Running, Completed, Skipped, StorageFull }

/// <summary>A detached run definition. Editing setup cannot change a purchase already in progress.</summary>
public sealed class FuelQueueEntry
{
    public ulong CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public string CompanyKey { get; set; } = string.Empty;
    public ulong FreeCompanyId { get; set; }
    public bool FreeCompanyKnown { get; set; }
    public string FreeCompanyName { get; set; } = string.Empty;
    public Guid? HouseId { get; set; }
    public string HouseAddress { get; set; } = string.Empty;
    public string HouseName { get; set; } = string.Empty;
    public bool UseCaretaker { get; set; }
    public int CaretakerCity { get; set; } = 8;
    public uint CreditReserve { get; set; }
    public uint KeepCarried { get; set; }
    public bool ExhaustCredits { get; set; }
    public uint TargetQuantity { get; set; }
    public uint BuyBelow { get; set; }
    public bool DepositToRetainers { get; set; } = true;
    public FuelQueueEntryStatus Status { get; set; }
    public string Note { get; set; } = string.Empty;
    public ulong Purchased { get; set; }
    public ulong Deposited { get; set; }
}

/// <summary>Uses the same one-based ward/plot format as Lifestream's house address tuple.</summary>
public readonly record struct FuelHouseAddress(int World, int City, int Ward, int Plot)
{
    public static bool TryParse(string? address, out FuelHouseAddress destination)
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

    public static string Format(int world, int city, int ward, int plot)
    {
        if (!IsValid(world, city, ward, plot)) throw new ArgumentException("Choose a valid world, housing district, ward and house plot.");
        return FormattableString.Invariant($"ls-house:{world}:{city}:{ward}:{plot}");
    }

    public static bool IsOnWorld(HouseProfile house, uint worldId) => worldId != 0
        && TryParse(house.Address, out var destination) && destination.World == worldId;

    private static bool IsValid(int world, int city, int ward, int plot) => world is > 0 and <= ushort.MaxValue
        && city is 9 or 2 or 8 or 70 or 111 && ward is >= 1 and <= 30 && plot is >= 1 and <= 60;
}

public static class FuelQueuePlanner
{
    /// <summary>Unknown memberships get independent groups; FC names/tags are never used as identity.</summary>
    public static string CompanyKey(CharacterProfile character)
    {
        ArgumentNullException.ThrowIfNull(character);
        var world = character.World.Trim().ToUpperInvariant();
        return character.FreeCompanyId is > 0
            ? $"{world}:fc:{character.FreeCompanyId.Value.ToString(CultureInfo.InvariantCulture)}"
            : $"{world}:character:{character.ContentId.ToString(CultureInfo.InvariantCulture)}";
    }

    public static IReadOnlyList<FuelQueueEntry> Build(IReadOnlyList<CharacterProfile> characters,
        IReadOnlyList<HouseProfile> houses, FuelQueueSettings settings, Func<string, uint?> resolveWorld)
    {
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(houses);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(resolveWorld);
        var selected = characters.Where(character => character.Fuel.Enabled)
            .OrderBy(character => character.World.Trim(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(CompanyKey, StringComparer.Ordinal)
            .ThenBy(character => character.Fuel.QueueOrder)
            .ThenBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(character => character.ContentId).ToArray();
        var entries = new List<FuelQueueEntry>(selected.Length);
        var seenCharacters = new HashSet<ulong>();
        foreach (var character in selected)
        {
            var key = CompanyKey(character);
            var rule = settings.Companies?.GetValueOrDefault(key);
            var fuel = character.Fuel;
            var useCaretaker = !fuel.HouseId.HasValue && rule?.UseCaretaker == true;
            var houseId = fuel.HouseId ?? (useCaretaker ? null : rule?.HouseId);
            var matches = houseId.HasValue ? houses.Where(house => house.Id == houseId.Value).ToArray() : [];
            var house = matches.Length == 1 ? matches[0] : null;
            var companyName = !string.IsNullOrWhiteSpace(rule?.NameOverride) ? rule.NameOverride
                : !string.IsNullOrWhiteSpace(character.FreeCompanyName) ? character.FreeCompanyName
                : character.FreeCompanyId is > 0 ? $"FC {character.FreeCompanyId.Value}"
                : character.FreeCompanyId == 0 ? "No free company" : "FC membership unknown";
            var entry = new FuelQueueEntry
            {
                CharacterId = character.ContentId, CharacterName = character.Name, World = character.World,
                CompanyKey = key, FreeCompanyId = character.FreeCompanyId ?? 0,
                FreeCompanyKnown = character.FreeCompanyId.HasValue, FreeCompanyName = companyName,
                HouseId = houseId, HouseAddress = house?.Address ?? "", HouseName = house?.Name ?? "",
                UseCaretaker = useCaretaker, CaretakerCity = rule?.CaretakerCity ?? 8,
                CreditReserve = rule?.CreditReserve ?? 0, KeepCarried = fuel.KeepCarried,
                ExhaustCredits = fuel.ExhaustCredits, TargetQuantity = fuel.TargetQuantity, BuyBelow = fuel.BuyBelow,
                DepositToRetainers = fuel.DepositToRetainers,
            };
            entries.Add(entry);
            string? reason = null;
            if (character.ContentId == 0 || string.IsNullOrWhiteSpace(character.Name)) reason = "Character identity is incomplete. Refresh the AutoRetainer roster.";
            else if (!seenCharacters.Add(character.ContentId)) reason = "This character is already included in the queue.";
            else if (character.FreeCompanyId == 0) reason = "This character has no free company.";
            else if (!fuel.ExhaustCredits && (fuel.BuyBelow == 0 || fuel.TargetQuantity == 0 || fuel.BuyBelow > fuel.TargetQuantity))
                reason = "Choose credit exhaustion or a positive purchase threshold no greater than the target.";
            else if (resolveWorld(character.World) is not { } worldId || worldId == 0)
                reason = "The character's home world is unknown. Refresh the roster before buying fuel.";
            else if (useCaretaker && entry.CaretakerCity is not (2 or 8 or 9 or 70 or 111))
                reason = "Choose a valid housing district for the Resident Caretaker.";
            else if (!useCaretaker)
            {
                if (!houseId.HasValue) reason = "Assign a registered workshop house on this character's home world, or choose a Resident Caretaker.";
                else if (matches.Length != 1) reason = "The assigned workshop house is missing or duplicated. Choose a registered house.";
                else if (!FuelHouseAddress.TryParse(house!.Address, out _)) reason = "The assigned house has an invalid world, district, ward or plot.";
                else if (!FuelHouseAddress.IsOnWorld(house, worldId)) reason = "The workshop house must be on this character's home world.";
            }
            if (reason != null) { entry.Status = FuelQueueEntryStatus.Skipped; entry.Note = reason; }
            else if (!entry.FreeCompanyKnown) entry.Note = "FC membership will be checked after login.";
        }
        return entries;
    }
}
