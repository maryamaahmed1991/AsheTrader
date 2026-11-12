namespace AsheTrader.Workflow;

/// <summary>Refreshes inventory evidence without replacing the durable reason a trade stopped.</summary>
internal static class TradeReconciliationEvidence
{
    public static WorkerResponse Capture(WorkerCommand command, WorkerResponse? previous, uint before, uint current)
    {
        if (command.Kind is not (WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch))
            throw new ArgumentException("Inventory reconciliation evidence requires a trade command.", nameof(command));
        if (previous != null && previous.CommandId != command.Id)
            throw new ArgumentException("The saved response belongs to another trade.", nameof(previous));
        if (previous is { Success: true } or { OperatorReviewed: true } or { AutoReconciled: true } or { NeverStarted: true })
            throw new InvalidOperationException("A settled trade result must be replayed without changing its evidence.");
        var expected = command.Operation.Quantity;
        var matches = command.Kind == WorkerCommandKind.SendBatch
            ? before >= current && before - current == expected
            : current >= before && current - before == expected;
        return new()
        {
            CommandId = command.Id, BeforeQuantity = before, CurrentQuantity = current,
            Quantity = matches ? expected : 0, Unchanged = current == before, Ambiguous = true,
            Error = !string.IsNullOrWhiteSpace(previous?.Error) ? previous.Error
                : "Trade was interrupted before a durable success result. Both PCs' current inventories will be tallied before continuing."
        };
    }

    public static TradeTallyOutcome Classify(WorkerCommand command, WorkerResponse response)
    {
        if (response.CommandId != command.Id || command.Operation.Quantity == 0) return TradeTallyOutcome.Conflicting;
        if (!response.Ambiguous && response.Success)
            return response.Quantity == command.Operation.Quantity ? TradeTallyOutcome.Transferred : TradeTallyOutcome.Conflicting;
        if (!response.Ambiguous && (response.NeverStarted
            || response.Unchanged && (response.AutoReconciled || response.OperatorReviewed))) return TradeTallyOutcome.Unchanged;
        if (response.BeforeQuantity is not { } before || response.CurrentQuantity is not { } current)
            return TradeTallyOutcome.Missing;
        if (before == current) return TradeTallyOutcome.Unchanged;
        var moved = command.Kind == WorkerCommandKind.SendBatch
            ? before >= current && before - current == command.Operation.Quantity
            : current >= before && current - before == command.Operation.Quantity;
        return moved ? TradeTallyOutcome.Transferred : TradeTallyOutcome.Conflicting;
    }

    public static WorkerResponse Settle(WorkerRecord record, AutomaticTradeResolution resolution, uint current)
    {
        var previous = record.Response;
        if (record.Command.Id != resolution.CommandId) throw new InvalidOperationException("The reconciliation command does not match its saved worker record.");
        if (previous is { Success: true } or { AutoReconciled: true } or { OperatorReviewed: true } or { NeverStarted: true })
        {
            var settled = Classify(record.Command, previous);
            if (settled != (resolution.Transferred ? TradeTallyOutcome.Transferred : TradeTallyOutcome.Unchanged))
                throw new InvalidOperationException("The saved trade outcome conflicts with the requested automatic tally result.");
            return previous;
        }
        if (record.TradeTallyRound != resolution.RoundId || record.BeforeQuantity != resolution.ExpectedBefore
            || record.TradeTallyCurrent != resolution.ExpectedCurrent || resolution.ExpectedCurrent != current
            || !record.BeforeQuantity.HasValue)
            throw new InvalidOperationException("Inventory changed after the paired tally, or the saved tally is missing. Close trade windows and use Resume / tally inventories to read both characters again.");
        var evidence = Capture(record.Command, previous, record.BeforeQuantity.Value, current);
        if (Classify(record.Command, evidence) != (resolution.Transferred ? TradeTallyOutcome.Transferred : TradeTallyOutcome.Unchanged))
            throw new InvalidOperationException("The current inventory does not support the paired trade tally.");
        evidence.Success = resolution.Transferred;
        evidence.Ambiguous = false;
        evidence.AutoReconciled = true;
        evidence.Quantity = resolution.Transferred ? record.Command.Operation.Quantity : 0;
        return evidence;
    }
}

internal enum TradeTallyOutcome { Missing, Unchanged, Transferred, Conflicting }
