namespace AsheTrader.Core;

public sealed record FuelDestinationChoice(FuelQueueEntry Destination, FuelAccessPlan Access, string Explanation);

/// <summary>Deterministic route preferences, not a claim of measured travel times.</summary>
public static class FuelAutomaticDestination
{
    public static string Key(FuelQueueEntry entry) => entry.UseCaretaker
        ? $"caretaker:{entry.CaretakerCity}" : entry.HouseAddress;

    public static IReadOnlyList<FuelQueueEntry> Candidates(FuelQueueEntry buyer, uint world, IReadOnlyList<HouseProfile> houses)
    {
        if (world == 0) return [];
        var choices = new List<FuelQueueEntry>();
        foreach (var house in houses.Where(h => FuelHouseAddress.IsOnWorld(h, world))
                     .OrderBy(h => h.Address, StringComparer.Ordinal).DistinctBy(h => h.Address))
            choices.Add(Candidate(buyer, house, 0));
        foreach (var city in new[] { 8, 2, 9, 111, 70 }) choices.Add(Candidate(buyer, null, city));
        return choices;
    }

    private static FuelQueueEntry Candidate(FuelQueueEntry buyer, HouseProfile? house, int city) => new()
    {
        CharacterId = buyer.CharacterId, CharacterName = buyer.CharacterName, World = buyer.World,
        FreeCompanyId = buyer.FreeCompanyId, FreeCompanyKnown = buyer.FreeCompanyKnown,
        HouseId = house?.Id, HouseAddress = house?.Address ?? "", HouseName = house?.Name ?? "",
        UseCaretaker = house == null, CaretakerCity = city,
    };

    public static FuelDestinationChoice? Choose(IReadOnlyList<FuelQueueEntry> candidates,
        Func<FuelQueueEntry, FuelAccessPlan> assess, string? previousSuccessfulDestination = null)
    {
        // Unknown access is not permission to dispatch travel. It is checked after the buyer logs in.
        var eligible = candidates.Select(c => (Entry: c, Access: assess(c)))
            .Where(c => c.Access.Status == FuelAccessStatus.Eligible && c.Access.Kind != FuelAccessRouteKind.Unavailable)
            .OrderBy(c => Score(c.Entry, c.Access, previousSuccessfulDestination)).ToArray();
        if (eligible.Length == 0) return null;
        var chosen = eligible[0];
        var why = chosen.Access.Kind == FuelAccessRouteKind.AlreadyThere ? "Already at this destination."
            : Key(chosen.Entry) == previousSuccessfulDestination ? "Previously successful destination; access is still available."
            : chosen.Access.Kind == FuelAccessRouteKind.DirectFcEstate && !chosen.Entry.UseCaretaker ? "Matching FC estate teleport with an on-site workshop supplier."
            : chosen.Access.Kind == FuelAccessRouteKind.DirectFcEstate ? "FC estate teleport provides access to this caretaker's district."
            : !chosen.Entry.UseCaretaker ? "Registered same-world workshop with verified city access."
            : "Caretaker with verified city and residential access; no workshop required.";
        return new(chosen.Entry, chosen.Access, why);
    }

    private static int Score(FuelQueueEntry entry, FuelAccessPlan access, string? previous)
        => access.Kind == FuelAccessRouteKind.AlreadyThere ? 0
            : Key(entry) == previous ? 10
            : access.Kind == FuelAccessRouteKind.DirectFcEstate ? entry.UseCaretaker ? 30 : 20
            : entry.UseCaretaker ? 50 : 40;

    public static void Apply(FuelQueueEntry target, FuelDestinationChoice choice)
    {
        target.HouseId = choice.Destination.HouseId; target.HouseAddress = choice.Destination.HouseAddress;
        target.HouseName = choice.Destination.HouseName; target.UseCaretaker = choice.Destination.UseCaretaker;
        target.CaretakerCity = choice.Destination.CaretakerCity;
        target.DestinationReason = choice.Explanation;
    }
}
