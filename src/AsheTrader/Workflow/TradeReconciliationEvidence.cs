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
        if (previous is { Success: true } or { OperatorReviewed: true } or { NeverStarted: true })
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
                : "Trade was interrupted before a durable success result. Review both PCs and explicitly reconcile this batch."
        };
    }
}
