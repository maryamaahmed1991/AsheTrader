using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using Newtonsoft.Json;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private SettingsFileStore? schedulesFile;
    private TransferScheduleState schedules = new();
    private bool schedulesHealthy = true;
    private long nextSchedulePoll;
    public IReadOnlyList<TransferSchedule> Schedules => schedules.Schedules;
    public bool SchedulesSuspended => schedules.Suspended;
    public string ScheduleStatus { get; private set; } = "Schedules run on the coordinator while both game clients are online.";

    private void InitializeSchedules()
    {
        schedulesFile = new(Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "transfer-schedules.json"));
        try
        {
            schedules = Decode<TransferScheduleState>(schedulesFile.Load() ?? "{}");
            if (schedules.Schedules == null || schedules.Schedules.Any(s => s == null || s.Id == Guid.Empty || s.Job == null || s.Timing == null))
                throw new InvalidDataException("Invalid saved schedule.");
            foreach (var s in schedules.Schedules) s.Timing.Validate();
            if (TransferScheduleQueue.RecoverClaim(schedules, Run?.ScheduleOccurrenceId))
            {
                if (schedules.Claimed != null) ScheduleStatus = "A scheduled start was interrupted before its run was saved. Resume schedules to retry that start, or discard it.";
                SaveSchedules();
            }
        }
        catch (Exception e) { schedulesHealthy = false; schedules.Suspended = true; ScheduleStatus = "Schedules unavailable: " + e.Message; }
    }

    private void SaveSchedules()
    {
        if (!schedulesHealthy || schedulesFile == null) throw new InvalidOperationException("The schedule file is unavailable; its contents were preserved.");
        if (!schedulesFile.TrySave(JsonConvert.SerializeObject(schedules), out var failure))
        { schedulesHealthy = false; schedules.Suspended = true; ScheduleStatus = failure; throw new IOException(failure); }
    }

    public void AddSchedule(SavedTransferJob job, TransferScheduleTiming timing, string name)
    {
        if (!schedulesHealthy) throw new InvalidOperationException(ScheduleStatus);
        if (Settings.RememberedHost != true && !host) throw new InvalidOperationException("Create schedules on the coordinator PC.");
        timing.Validate();
        foreach (var item in job.ItemDefinitions) JobPlanner.ValidateDefinition(item);
        if (job.Meeting.TerritoryId == 0 || job.Meeting.AetheryteId == 0) throw new InvalidOperationException("Save a meeting point before scheduling this job.");
        if (job.ItemDefinitions.Select(d => new ItemKey(d.ItemId, d.HighQuality)).Distinct().Count() != job.ItemDefinitions.Count())
            throw new InvalidOperationException("Each item/quality must appear only once in this job.");
        var snapshot = Clone(job); snapshot.EnableAutoRetainerAfterCompletion = true;
        schedules.Schedules.Add(new() { Name = string.IsNullOrWhiteSpace(name) ? job.Definition.Name : name.Trim(),
            Job = snapshot, Timing = Clone(timing), NextDue = timing.First() });
        SaveSchedules();
        ScheduleStatus = $"Saved schedule: {schedules.Schedules.Last().Name}.";
    }

    public void SetScheduleEnabled(Guid id, bool enabled)
    {
        var schedule = schedules.Schedules.Single(s => s.Id == id);
        if (enabled && schedule.NextDue == null) throw new InvalidOperationException("This one-time schedule has finished. Create a new schedule for another time.");
        schedule.Enabled = enabled; SaveSchedules();
    }
    public void EditScheduleTiming(Guid id, TransferScheduleTiming timing, string name)
    {
        if (schedules.Claimed?.ScheduleId == id) throw new InvalidOperationException("Resolve the interrupted start before editing this schedule.");
        timing.Validate();
        var schedule = schedules.Schedules.Single(s => s.Id == id);
        schedule.Timing = Clone(timing); schedule.NextDue = timing.First(); schedule.Enabled = true;
        if (!string.IsNullOrWhiteSpace(name)) schedule.Name = name.Trim();
        schedule.LastResult = "Timing updated. Existing runs keep their original snapshot.";
        SaveSchedules();
    }
    public void RemoveSchedule(Guid id)
    {
        if (schedules.Claimed?.ScheduleId == id) throw new InvalidOperationException("Discard the interrupted start first.");
        schedules.Schedules.RemoveAll(s => s.Id == id); SaveSchedules();
    }
    public void ResumeSchedules()
    {
        if (!schedulesHealthy) throw new InvalidOperationException(ScheduleStatus);
        schedules.Suspended = false; SaveSchedules(); ScheduleStatus = "Schedules enabled; waiting for the next eligible start.";
    }
    public void SuspendSchedules(string reason = "Paused by user.")
    {
        if (reason == "Plugin unloaded." || schedulesFile == null || !schedulesHealthy || schedules.Suspended) return;
        schedules.Suspended = true; ScheduleStatus = "Schedules paused: " + reason; SaveSchedules();
    }
    public bool HasInterruptedScheduledStart => schedules.Claimed != null;
    public void DiscardScheduledStart()
    {
        schedules.Claimed = null; SaveSchedules(); ScheduleStatus = "Interrupted scheduled start discarded. Schedules remain paused until enabled.";
    }

    private void UpdateSchedules()
    {
        if (!schedulesHealthy || schedules.Suspended || Environment.TickCount64 < nextSchedulePoll) return;
        nextSchedulePoll = Environment.TickCount64 + 1000;
        if (Settings.RememberedHost != true && !host) return;
        if (schedules.Claimed is { } saved && Run?.ScheduleOccurrenceId == saved.Id)
        { schedules.Claimed = null; SaveSchedules(); }
        var now = DateTimeOffset.UtcNow;
        if (TransferScheduleQueue.ExpireMissed(schedules, now)) SaveSchedules();
        var due = schedules.Schedules.Where(s => s.Enabled && s.NextDue <= now).OrderBy(s => s.NextDue).FirstOrDefault();
        if (due == null && schedules.Claimed == null) return;
        if (!HasAcceptedConnection || RemoteRoster is not { ControlEnabled: true, ProtocolVersion: >= 5 })
        { ScheduleStatus = "Scheduled job waiting for both PCs to be connected and updated, with worker control enabled."; return; }
        if (!LocalReadyForJob || Run is { Step: not CoordinatorStep.Finished } || !RemoteReadyForJob || remoteHasUnfinishedJob
            || WaitingForPeer || HasQueuedTransfer || completionDeadline.HasValue || plugin.CompletionHandoff.Pending)
        { ScheduleStatus = "Scheduled job queued; waiting for current jobs and AR completion handoff."; return; }
        var occurrence = TransferScheduleQueue.Claim(schedules, now, ready: true);
        if (occurrence == null) return;
        SaveSchedules(); // Claim before saving the run or issuing any game/AR command.
        Start(occurrence.Job, false, occurrence.Id, occurrence.Name);
        schedules.Claimed = null; SaveSchedules();
        ScheduleStatus = $"Started {occurrence.Name}; pausing AR on both PCs.";
    }

    private void UpdateScheduledArPause(long now)
    {
        if (activeWorker?.Command.Kind != WorkerCommandKind.PauseAutoRetainer) return;
        if (now - arPauseStarted > 600_000)
        { CompleteWorker(new() { CommandId = activeWorker.Command.Id, Error = "AutoRetainer did not become idle within ten minutes. Its current task was not aborted." }); return; }
        if (!plugin.AutoRetainer.TryDisableMultiMode(out var failure))
        { CompleteWorker(new() { CommandId = activeWorker.Command.Id, Error = failure }); return; }
        var ar = plugin.AutoRetainer.GetStatus();
        if (ar.Busy != false || plugin.Lifestream.GetStatus().Busy != false)
        { arIdleSince = null; return; }
        arIdleSince ??= now;
        if (now - arIdleSince.Value < 500) return;
        CompleteWorker(new() { CommandId = activeWorker.Command.Id, Success = true });
    }
}
