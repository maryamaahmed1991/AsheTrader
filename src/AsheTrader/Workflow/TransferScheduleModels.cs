using AsheTrader.Core;

namespace AsheTrader.Workflow;

public sealed class TransferSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public SavedTransferJob Job { get; set; } = new();
    public TransferScheduleTiming Timing { get; set; } = new();
    public DateTimeOffset? NextDue { get; set; }
    public bool Enabled { get; set; } = true;
    public string LastResult { get; set; } = "Not run yet.";
}
public sealed record ScheduledOccurrence(Guid Id, Guid ScheduleId, string Name, DateTimeOffset Due, SavedTransferJob Job);
public sealed class TransferScheduleState
{
    public bool Suspended { get; set; }
    public List<TransferSchedule> Schedules { get; set; } = [];
    public ScheduledOccurrence? Claimed { get; set; }
}

