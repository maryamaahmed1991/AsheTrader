using AsheTrader.Core;

namespace AsheTrader.Workflow;

/// <summary>Exact per-item evidence for one atomic trade. Totals cannot substitute for matching item/quality keys.</summary>
internal static class MultiItemTradeEvidence
{
    public static bool IsVector(WorkerCommand command) => command.TradeItems is { Count: > 0 };

    public static IReadOnlyList<TradeLine> Lines(WorkerCommand command)
    {
        IReadOnlyList<TradeLine> source = IsVector(command) ? command.TradeItems
            : [new TradeLine { ItemId = command.Operation.ItemId, HighQuality = command.Operation.HighQuality, Quantity = command.Operation.Quantity }];
        if (source.Count == 0 || source.Any(line => line == null || line.ItemId <= 1 || line.Quantity == 0)
            || source.Select(line => new ItemKey(line.ItemId, line.HighQuality)).Distinct().Count() != source.Count)
            throw new InvalidDataException("A trade requires unique item/quality lines with positive quantities.");
        _ = checked((uint)source.Aggregate(0UL, (sum, line) => sum + line.Quantity));
        return source.Select(line => new TradeLine { ItemId = line.ItemId, HighQuality = line.HighQuality, Quantity = line.Quantity }).ToArray();
    }

    public static WorkerResponse Capture(WorkerCommand command, WorkerResponse? previous,
        IReadOnlyList<TradeItemObservation> baselines, IReadOnlyDictionary<ItemKey, uint> current)
    {
        RequireTrade(command);
        if (previous != null && previous.CommandId != command.Id)
            throw new InvalidOperationException("The saved response belongs to another trade.");
        if (IsSettled(previous)) throw new InvalidOperationException("A settled trade must be replayed without replacing its item evidence.");
        var lines = Lines(command);
        var before = Index(baselines);
        RequireSameKeys(lines, before.Keys);
        var items = new List<TradeItemObservation>();
        foreach (var line in lines)
        {
            var key = new ItemKey(line.ItemId, line.HighQuality);
            if (before[key].BeforeQuantity is not { } starting || !current.TryGetValue(key, out var quantity))
                throw new InvalidOperationException("This trade has no complete saved starting inventory for every item. Its outcome cannot be tallied automatically.");
            items.Add(new()
            {
                ItemId = line.ItemId, HighQuality = line.HighQuality, BeforeQuantity = starting, CurrentQuantity = quantity,
                Quantity = Matches(command.Kind, starting, quantity, line.Quantity) ? line.Quantity : 0,
            });
        }
        return new()
        {
            CommandId = command.Id, TradeItems = items,
            BeforeQuantity = items.Count == 1 ? items[0].BeforeQuantity : null,
            CurrentQuantity = items.Count == 1 ? items[0].CurrentQuantity : null,
            Quantity = Sum(items), Unchanged = items.All(item => item.BeforeQuantity == item.CurrentQuantity), Ambiguous = true,
            Error = !string.IsNullOrWhiteSpace(previous?.Error) ? previous.Error
                : "Trade interrupted before its result was saved. Both PCs will tally every item before continuing.",
        };
    }

    public static TradeTallyOutcome Classify(WorkerCommand command, WorkerResponse response)
    {
        try
        {
            RequireTrade(command);
            if (response.CommandId != command.Id) return TradeTallyOutcome.Conflicting;
            var lines = Lines(command);
            if (!response.Ambiguous && response.NeverStarted) return TradeTallyOutcome.Unchanged;
            if (response.TradeItems is not { Count: > 0 }) return TradeTallyOutcome.Missing;
            var items = Index(response.TradeItems);
            RequireSameKeys(lines, items.Keys);
            if (!response.Ambiguous && response.Success)
                return lines.All(line => items[new(line.ItemId, line.HighQuality)].Quantity == line.Quantity)
                    && response.Quantity == Sum(response.TradeItems) ? TradeTallyOutcome.Transferred : TradeTallyOutcome.Conflicting;
            if (!response.Ambiguous && response.Unchanged && (response.AutoReconciled || response.OperatorReviewed))
                return response.Quantity == 0 && response.TradeItems.All(item => item.Quantity == 0)
                    ? TradeTallyOutcome.Unchanged : TradeTallyOutcome.Conflicting;
            if (response.TradeItems.Any(item => item.BeforeQuantity == null || item.CurrentQuantity == null))
                return TradeTallyOutcome.Missing;
            if (response.TradeItems.All(item => item.BeforeQuantity == item.CurrentQuantity)) return TradeTallyOutcome.Unchanged;
            return lines.All(line =>
            {
                var item = items[new(line.ItemId, line.HighQuality)];
                return Matches(command.Kind, item.BeforeQuantity!.Value, item.CurrentQuantity!.Value, line.Quantity);
            }) ? TradeTallyOutcome.Transferred : TradeTallyOutcome.Conflicting;
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or InvalidOperationException)
        { return TradeTallyOutcome.Conflicting; }
    }

    public static WorkerResponse Settle(WorkerRecord record, AutomaticTradeResolution resolution,
        IReadOnlyDictionary<ItemKey, uint> current)
    {
        if (record.Command.Id != resolution.CommandId)
            throw new InvalidOperationException("The tally command does not match its saved trade.");
        var wanted = resolution.Transferred ? TradeTallyOutcome.Transferred : TradeTallyOutcome.Unchanged;
        if (IsSettled(record.Response))
        {
            if (Classify(record.Command, record.Response!) != wanted)
                throw new InvalidOperationException("The durable trade result conflicts with the requested tally outcome.");
            return record.Response!;
        }
        var lines = Lines(record.Command);
        var expected = Index(resolution.ExpectedItems);
        var tallied = Index(record.TradeTallyItems);
        var baseline = Index(record.TradeBaselines);
        RequireSameKeys(lines, expected.Keys); RequireSameKeys(lines, tallied.Keys); RequireSameKeys(lines, baseline.Keys);
        if (record.TradeTallyRound != resolution.RoundId || resolution.RoundId == Guid.Empty)
            throw new InvalidOperationException("The saved paired tally round is missing or changed. Resume to tally both characters again.");
        foreach (var line in lines)
        {
            var key = new ItemKey(line.ItemId, line.HighQuality);
            var item = expected[key];
            var stored = tallied[key];
            if (baseline[key].BeforeQuantity is not { } before || item.BeforeQuantity != before
                || stored.BeforeQuantity != before || item.CurrentQuantity != stored.CurrentQuantity
                || item.Quantity != stored.Quantity || item.CurrentQuantity == null
                || !current.TryGetValue(key, out var now) || item.CurrentQuantity != now)
                throw new InvalidOperationException("An item's inventory changed after the paired tally. Resume to tally every item on both PCs again.");
        }
        var evidence = Capture(record.Command, record.Response, record.TradeBaselines, current);
        if (Classify(record.Command, evidence) != wanted)
            throw new InvalidOperationException("The current item inventories do not support the paired trade outcome.");
        evidence.Success = resolution.Transferred;
        evidence.Ambiguous = false;
        evidence.AutoReconciled = true;
        foreach (var item in evidence.TradeItems)
            item.Quantity = resolution.Transferred ? lines.Single(line => line.ItemId == item.ItemId && line.HighQuality == item.HighQuality).Quantity : 0;
        evidence.Quantity = Sum(evidence.TradeItems);
        return evidence;
    }

    public static uint Sum(IEnumerable<TradeItemObservation> items)
        => checked((uint)items.Aggregate(0UL, (sum, item) => sum + item.Quantity));

    private static bool Matches(WorkerCommandKind kind, uint before, uint current, uint requested)
        => kind == WorkerCommandKind.SendBatch ? before >= current && before - current == requested
            : current >= before && current - before == requested;

    private static bool IsSettled(WorkerResponse? response)
        => response is { Success: true } or { AutoReconciled: true } or { OperatorReviewed: true } or { NeverStarted: true };

    private static void RequireTrade(WorkerCommand command)
    {
        if (command.Kind is not (WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch))
            throw new InvalidOperationException("Item tally evidence requires a sender or receiver trade command.");
    }

    private static Dictionary<ItemKey, TradeItemObservation> Index(IEnumerable<TradeItemObservation> items)
    {
        var result = new Dictionary<ItemKey, TradeItemObservation>();
        foreach (var item in items)
            if (item == null || item.ItemId <= 1 || !result.TryAdd(new(item.ItemId, item.HighQuality), item))
                throw new InvalidDataException("Trade item evidence contains an invalid or duplicate item/quality key.");
        return result;
    }

    private static void RequireSameKeys(IReadOnlyList<TradeLine> lines, IEnumerable<ItemKey> keys)
    {
        if (!lines.Select(line => new ItemKey(line.ItemId, line.HighQuality)).ToHashSet().SetEquals(keys))
            throw new InvalidDataException("Trade evidence must include exactly the agreed items and qualities.");
    }
}
