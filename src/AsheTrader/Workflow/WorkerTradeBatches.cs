using AsheTrader.Core;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private readonly Dictionary<Guid, (Guid Round, string Signature, long Since)> batchTallySamples = [];

    private Dictionary<ItemKey, uint> ReadBatchItemCounts(WorkerCommand command)
        => BatchItemCounts(command, plugin.Trader.SnapshotInventoryNow());

    private static Dictionary<ItemKey, uint> BatchItemCounts(WorkerCommand command, IEnumerable<InventoryStack> inventory)
    {
        var totals = TradePlanner.Count(inventory);
        return MultiItemTradeEvidence.Lines(command).ToDictionary(line => new ItemKey(line.ItemId, line.HighQuality),
            line => checked((uint)totals.GetValueOrDefault(new(line.ItemId, line.HighQuality))));
    }

    private WorkerResponse CompletedBatchResponse(WorkerRecord record)
    {
        var succeeded = plugin.Trader.Succeeded;
        var response = new WorkerResponse
        {
            CommandId = record.Command.Id, Success = succeeded, Ambiguous = !succeeded,
            Quantity = plugin.Trader.VerifiedQuantity, Error = succeeded ? "" : plugin.Trader.Status,
        };
        if (!MultiItemTradeEvidence.IsVector(record.Command)) return response;
        // The runner's successful snapshot is the one it just verified against the exact
        // outgoing/incoming offer. Do not replace it with a later, unrelated inventory read.
        var current = succeeded ? BatchItemCounts(record.Command, plugin.Trader.ReadInventory()) : null;
        var verified = plugin.Trader.VerifiedItems.ToDictionary(line => new ItemKey(line.ItemId, line.HighQuality), line => line.Quantity);
        foreach (var line in MultiItemTradeEvidence.Lines(record.Command))
        {
            var key = new ItemKey(line.ItemId, line.HighQuality);
            var before = record.TradeBaselines.Single(item => item.ItemId == line.ItemId && item.HighQuality == line.HighQuality).BeforeQuantity;
            response.TradeItems.Add(new()
            {
                ItemId = line.ItemId, HighQuality = line.HighQuality, BeforeQuantity = before,
                CurrentQuantity = current?.GetValueOrDefault(key), Quantity = verified.GetValueOrDefault(key),
            });
        }
        if (response.TradeItems.Count == 1)
        {
            response.BeforeQuantity = response.TradeItems[0].BeforeQuantity;
            response.CurrentQuantity = response.TradeItems[0].CurrentQuantity;
        }
        if (succeeded && MultiItemTradeEvidence.Classify(record.Command, response) != TradeTallyOutcome.Transferred)
            throw new InvalidOperationException("The verified trade omitted an agreed item or quality. Preserve its journal for automatic tally recovery.");
        return response;
    }

    private TradeTallyReply CaptureBatchTally(WorkerRecord record, TradeTallyRequest request)
    {
        var current = ReadBatchItemCounts(record.Command);
        var signature = string.Join("|", current.OrderBy(pair => pair.Key.ItemId).ThenBy(pair => pair.Key.HighQuality)
            .Select(pair => $"{pair.Key.ItemId}:{pair.Key.HighQuality}:{pair.Value}"));
        var now = Environment.TickCount64;
        if (!batchTallySamples.TryGetValue(request.CommandId, out var sample)
            || sample.Round != request.RoundId || sample.Signature != signature)
        {
            batchTallySamples[request.CommandId] = (request.RoundId, signature, now);
            return new(request.CommandId, request.RoundId, false, true, null, "Checking that every item in the stopped trade is stable…");
        }
        if (now - sample.Since < 500)
            return new(request.CommandId, request.RoundId, false, true, null, "Checking that every item in the stopped trade is stable…");
        record.Response = MultiItemTradeEvidence.Capture(record.Command, record.Response, record.TradeBaselines, current);
        record.TradeTallyRound = request.RoundId;
        record.TradeTallyItems = Clone(record.Response.TradeItems);
        record.TradeTallyCurrent = current.Count == 1 ? current.Values.Single() : null;
        SaveWorkers();
        RecordDiagnostic($"Automatic multi-item trade tally; command={request.CommandId}; round={request.RoundId}; items={signature}");
        return new(request.CommandId, request.RoundId, true, false, Clone(record.Response));
    }
}
