using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using Newtonsoft.Json;

namespace AsheTrader.Workflow;

public sealed record TradeHistoryMovement(Guid OperationId, uint ItemId, bool HighQuality,
    ulong Sender, ulong Receiver, uint Quantity, bool FinalDelivery, DateTimeOffset? At);
public sealed record TradeHistoryItem(uint ItemId, bool HighQuality, JobQuantityMode Mode,
    List<RecipientRule> Requested, ulong Planned, ulong Delivered, ulong Shortage,
    Dictionary<ulong, ulong> Withdrawn, Dictionary<ulong, ulong> Deposited);
public sealed record TradeHistoryNote(DateTimeOffset At, string Message);
public sealed class TradeReceipt
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Schedule { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public string Outcome { get; set; } = "";
    public string Detail { get; set; } = "";
    public Dictionary<ulong, string> Characters { get; set; } = [];
    public List<TradeHistoryItem> Items { get; set; } = [];
    public List<TradeHistoryMovement> Movements { get; set; } = [];
    public List<TradeHistoryNote> Notes { get; set; } = [];
}

public static class TradeHistoryProjection
{
    public static TradeReceipt Create(CoordinatorRun run)
    {
        var receipt = new TradeReceipt { Id = run.RunId, Name = run.Job.Definition.Name,
            Schedule = run.ScheduleName, StartedAt = run.StartedAt, EndedAt = run.EndedAt ?? DateTimeOffset.UtcNow,
            Outcome = !run.CompletedNormally ? "Ended" : run.HasShortages || run.HasSkippedCharacters ? "Completed with shortages / skips" : "Completed",
            Detail = run.Status, Characters = new(run.CharacterLabels), Notes = [.. run.HistoryNotes] };
        foreach (var (definition, index) in run.Job.ItemDefinitions.Select((d, i) => (d, i)))
        {
            var completed = run.CompletedItems.FirstOrDefault(i => i.ItemIndex == index);
            var visit = run.VisitItems.GetValueOrDefault(index);
            var journal = visit?.Journal ?? completed?.Journal ?? (index == run.ActiveItemIndex ? run.Journal : null);
            var withdrawn = visit?.Withdrawn ?? completed?.Withdrawn ?? (index == run.ActiveItemIndex ? run.Withdrawn : []);
            var deposited = visit?.Deposited ?? completed?.Deposited ?? (index == run.ActiveItemIndex ? run.Deposited : []);
            var snapshots = visit?.Snapshots ?? completed?.Snapshots ?? (index == run.ActiveItemIndex ? run.Snapshots : []);
            foreach (var s in snapshots.Values) receipt.Characters.TryAdd(s.CharacterId, $"{s.Name}@{s.World}");
            foreach (var op in journal?.Operations.Where(o => o.State == TransferOperationState.Committed).DistinctBy(o => o.Id) ?? [])
            {
                var allocation = journal!.Plan.Allocations.Single(a => a.Id == op.AllocationId);
                var leg = allocation.Legs[op.LegIndex];
                receipt.Movements.Add(new(op.Id, definition.ItemId, definition.HighQuality, leg.SenderId, leg.ReceiverId,
                    op.Quantity, op.LegIndex == allocation.Legs.Count - 1, op.ReceiverObservation?.ObservedAt));
            }
            var delivered = receipt.Movements.Where(m => m.ItemId == definition.ItemId && m.HighQuality == definition.HighQuality && m.FinalDelivery)
                .Aggregate(0UL, (sum, m) => checked(sum + m.Quantity));
            receipt.Items.Add(new(definition.ItemId, definition.HighQuality, definition.QuantityMode,
                definition.Recipients.Select(r => new RecipientRule { CharacterId = r.CharacterId, Quantity = r.Quantity }).ToList(),
                journal?.Plan.AllocatedQuantity ?? 0, delivered,
                definition.QuantityMode == JobQuantityMode.DeliverAmount
                    ? Math.Max(definition.Recipients.Aggregate(0UL,(sum,r)=>checked(sum+(r.Quantity??0))),delivered)-delivered
                    : journal?.Plan.UnmetTargets.Aggregate(0UL, (sum, t) => checked(sum + t.Missing)) ?? 0,
                new(withdrawn), new(deposited)));
            var skipped = visit?.SkippedCharacters ?? completed?.SkippedCharacters ?? (index == run.ActiveItemIndex ? run.SkippedCharacters : []);
            foreach (var reason in skipped.Values.Distinct()) receipt.Notes.Add(new(receipt.EndedAt, reason));
            var skip = visit?.ItemSkipReason ?? completed?.ItemSkipReason ?? (index == run.ActiveItemIndex ? run.ItemSkipReason : "");
            if (!string.IsNullOrEmpty(skip)) receipt.Notes.Add(new(receipt.EndedAt, skip));
        }
        return receipt;
    }
}

public sealed class TradeHistoryStore
{
    private readonly SettingsFileStore file;
    private readonly bool readable;
    public IReadOnlyList<TradeReceipt> Records { get; private set; } = [];
    public string Error { get; private set; } = "";
    public TradeHistoryStore(string path)
    {
        file = new(path);
        try
        {
            Records = JsonConvert.DeserializeObject<List<TradeReceipt>>(file.Load() ?? "[]") ?? throw new InvalidDataException("Empty receipt list.");
            if (Records.Any(r => r == null || r.Id == Guid.Empty || r.Items == null || r.Movements == null)) throw new InvalidDataException("Invalid receipt.");
            readable = true;
        }
        catch (Exception e) { Records = []; Error = "Past Trades could not be loaded: " + e.Message; }
    }
    public void Record(TradeReceipt receipt)
    {
        if (!readable || Records.Any(r => r.Id == receipt.Id)) return;
        var next = Records.Append(receipt).OrderByDescending(r => r.EndedAt).ToList();
        if (!file.TrySave(JsonConvert.SerializeObject(next), out var failure)) { Error = failure; return; }
        Records = next; Error = "";
    }
}
