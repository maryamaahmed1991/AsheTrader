using System.Text;

namespace AsheTrader.Core;

public sealed record FuelBuyerReceipt
{
    public ulong CharacterId { get; init; }
    public ulong FreeCompanyId { get; init; }
    public string CharacterName { get; init; } = "";
    public string World { get; init; } = "";
    public string CompanyName { get; init; } = "";
    public FuelQueueEntryStatus Status { get; init; }
    public string Note { get; init; } = "";
    public ulong Purchased { get; init; }
    public ulong Deposited { get; init; }
    public ulong? CreditsSpent { get; init; }
    public string Destination { get; init; } = "";
    public string DestinationKey { get; init; } = "";
    public bool SupplierReached { get; init; }
}

public sealed record FuelRunReceipt
{
    public Guid Id { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset FinishedAtUtc { get; init; }
    public bool EndedByUser { get; init; }
    public bool FinishTimeEstimated { get; init; }
    public List<FuelBuyerReceipt> Buyers { get; init; } = [];
    public ulong Purchased => Buyers.Aggregate(0UL, (n, b) => checked(n + b.Purchased));
    public ulong Deposited => Buyers.Aggregate(0UL, (n, b) => checked(n + b.Deposited));
    public ulong? CreditsSpent => Buyers.Any(b => b.CreditsSpent == null) ? null : Buyers.Aggregate(0UL, (n, b) => checked(n + b.CreditsSpent!.Value));
}

public static class FuelRunHistory
{
    public const int Capacity = 100;

    public static FuelRunReceipt Capture(Guid id, DateTimeOffset? started, DateTimeOffset finished, bool ended, IReadOnlyList<FuelQueueEntry> entries, bool finishTimeEstimated = false)
        => new()
        {
            Id = id, StartedAtUtc = started, FinishedAtUtc = finished, EndedByUser = ended, FinishTimeEstimated = finishTimeEstimated,
            Buyers = entries.Select(e => new FuelBuyerReceipt
            {
                CharacterId = e.CharacterId, CharacterName = e.CharacterName, World = e.World,
                FreeCompanyId = e.FreeCompanyId, CompanyName = e.FreeCompanyName,
                Status = e.Status, Note = e.Note, Purchased = e.Purchased, Deposited = e.Deposited,
                CreditsSpent = e.Purchased == 0 ? 0 : e.CreditsSpent,
                Destination = e.AutomaticDestination && string.IsNullOrEmpty(e.DestinationReason) ? "Automatic (not resolved)"
                    : e.UseCaretaker ? $"{FuelAccessPlanner.DistrictName(e.CaretakerCity)} caretaker" : e.HouseName,
                DestinationKey = FuelAutomaticDestination.Key(e), SupplierReached = e.SupplierReached,
            }).ToList(),
        };

    public static IReadOnlyList<FuelRunReceipt> Upsert(IEnumerable<FuelRunReceipt> history, FuelRunReceipt receipt)
        => history.Where(r => r.Id != receipt.Id).Append(receipt).OrderByDescending(r => r.FinishedAtUtc)
            .ThenBy(r => r.Id).Take(Capacity).ToArray();

    public static ulong? AddPurchaseCost(ulong? existing, ulong purchasedBefore, uint quantity, uint unitPrice)
        => existing.HasValue ? checked(existing.Value + (ulong)quantity * unitPrice)
            : purchasedBefore == 0 ? (ulong)quantity * unitPrice : null;

    public static string Format(FuelRunReceipt receipt)
    {
        var text = new StringBuilder($"Ashe's Trader fuel receipt\nRun: {receipt.Id}\n{(receipt.FinishTimeEstimated ? "Imported (original finish time unknown)" : "Finished")}: {receipt.FinishedAtUtc:O}\n");
        text.AppendLine($"Outcome: {(receipt.EndedByUser ? "Ended by user" : "Finished")}");
        text.AppendLine($"Purchased: {receipt.Purchased:N0}; deposited: {receipt.Deposited:N0}; credits spent: {receipt.CreditsSpent?.ToString("N0") ?? "not recorded (older run)"}");
        foreach (var b in receipt.Buyers)
            text.AppendLine($"{b.CharacterName}@{b.World} | {b.CompanyName} | {b.Status} | bought {b.Purchased:N0}, stored {b.Deposited:N0} | {b.Destination} | {b.Note}");
        return text.ToString();
    }
}
