using Newtonsoft.Json;

namespace AsheTrader.Workflow;

public static class TransferScheduleQueue
{
    public static bool RecoverClaim(TransferScheduleState state, Guid? savedRunOccurrence)
    {
        if (state.Claimed == null) return false;
        if (state.Claimed.Id == savedRunOccurrence) state.Claimed = null;
        else state.Suspended = true;
        return true;
    }

    public static bool ExpireMissed(TransferScheduleState state, DateTimeOffset now)
    {
        if (state.Suspended) return false;
        var changed = false;
        foreach (var s in state.Schedules.Where(s => s.Enabled && s.NextDue.HasValue && s.Timing.Expired(s.NextDue.Value, now)))
        {
            s.LastResult = $"Skipped missed start at {s.NextDue:O}; catch-up window elapsed. No backlog is replayed.";
            s.NextDue = s.Timing.NextAfter(now); if (s.NextDue == null) s.Enabled = false;
            changed = true;
        }
        return changed;
    }

    public static ScheduledOccurrence? Claim(TransferScheduleState state, DateTimeOffset now, bool ready)
    {
        if (!ready || state.Suspended) return null;
        if (state.Claimed != null) return state.Claimed;
        var due = state.Schedules.Where(s => s.Enabled && s.NextDue <= now && !s.Timing.Expired(s.NextDue!.Value, now)).OrderBy(s => s.NextDue).ThenBy(s => s.Id).FirstOrDefault();
        if (due == null) return null;
        var snapshot = JsonConvert.DeserializeObject<SavedTransferJob>(JsonConvert.SerializeObject(due.Job))!;
        state.Claimed = new(Guid.NewGuid(), due.Id, due.Name, due.NextDue!.Value, snapshot);
        due.LastResult = $"Starting occurrence at {due.NextDue:O}. See Activity / Past Trades.";
        due.NextDue = due.Timing.NextAfter(now); if (due.NextDue == null) due.Enabled = false;
        return state.Claimed;
    }
}
