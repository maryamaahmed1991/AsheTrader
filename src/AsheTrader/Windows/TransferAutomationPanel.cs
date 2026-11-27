using System.Globalization;
using System.Numerics;
using AsheTrader.Core;
using AsheTrader.Workflow;
using Dalamud.Bindings.ImGui;
using Newtonsoft.Json;

namespace AsheTrader.Windows;

public sealed partial class WorkflowPanel
{
    private string scheduleName = "";
    private string scheduleDate = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd");
    private string scheduleTime = DateTime.Now.AddHours(1).ToString("HH:mm");
    private string scheduleZone = TimeZoneInfo.Local.Id;
    private int recurrence;
    private int catchUpMinutes = 60;
    private Guid scheduleJob;
    private Guid? editSchedule;
    private Guid? deleteSchedule;
    private string historySearch = "";
    private bool historyIssuesOnly;

    public void DrawSchedules()
    {
        if (ImGui.BeginChild("SchedulesPage", Vector2.Zero))
        {
            if (!CoordinatorRole) { ImGui.TextWrapped("Manage schedules on the coordinator PC."); }
            else
            {
                TraderStyle.Badge(Service.SchedulesSuspended ? "PAUSED" : "AUTOMATIC", Service.SchedulesSuspended ? UiTone.Warning : UiTone.Good);
                ImGui.SameLine();
                if (ImGui.Button(Service.SchedulesSuspended ? "Resume schedules" : "Pause schedules"))
                    plugin.QueueAction(() => { if (Service.SchedulesSuspended) Service.ResumeSchedules(); else Service.SuspendSchedules(); });
                UiTone.Status(Service.ScheduleStatus);
                if (Service.HasInterruptedScheduledStart && ImGui.Button("Discard interrupted start")) plugin.QueueAction(Service.DiscardScheduledStart);
                ImGui.Spacing();
                if (TraderStyle.BeginCard("ScheduleEditor"))
                {
                    TraderStyle.Section(editSchedule.HasValue ? "Edit schedule timing" : "Schedule a saved job", "The saved item and character rules are copied into this schedule.");
                    var existing = Service.Schedules.FirstOrDefault(s => s.Id == editSchedule);
                    var job = existing?.Job ?? Settings.Jobs.FirstOrDefault(j => j.Definition.Id == scheduleJob) ?? Settings.Jobs.FirstOrDefault();
                    ImGui.BeginDisabled(editSchedule.HasValue);
                    if (ImGui.BeginCombo("Saved job", job?.Definition.Name ?? "Create a job first"))
                    {
                        foreach (var candidate in Settings.Jobs)
                            if (ImGui.Selectable(candidate.Definition.Name + "##" + candidate.Definition.Id, candidate == job)) scheduleJob = candidate.Definition.Id;
                        ImGui.EndCombo();
                    }
                    ImGui.EndDisabled();
                    ImGui.InputText("Schedule name", ref scheduleName, 120);
                    ImGui.SetNextItemWidth(TraderStyle.Px(160)); ImGui.InputText("Date (YYYY-MM-DD)", ref scheduleDate, 16);
                    ImGui.SetNextItemWidth(TraderStyle.Px(100)); ImGui.InputText("Time (24-hour HH:mm)", ref scheduleTime, 8);
                    ImGui.InputText("Time zone", ref scheduleZone, 120);
                    if (ImGui.SmallButton("Use this PC's time zone")) scheduleZone = TimeZoneInfo.Local.Id;
                    ImGui.SameLine(); if (ImGui.SmallButton("Use UTC")) scheduleZone = TimeZoneInfo.Utc.Id;
                    string[] repeats = ["Once", "Every day", "Every week (same weekday)"];
                    ImGui.Combo("Repeat", ref recurrence, repeats, repeats.Length);
                    ImGui.InputInt("Catch-up window (minutes)", ref catchUpMinutes);
                    catchUpMinutes = Math.Clamp(catchUpMinutes, 0, 1440);
                    ImGui.TextDisabled("0 = skip missed runs; a one-minute start tolerance applies.");
                    var timing = ReadScheduleTiming(out var validation);
                    if (timing != null)
                        UiTone.Wrapped(UiTone.Info, $"First start: {TimeZoneInfo.ConvertTime(timing.First(), TimeZoneInfo.FindSystemTimeZoneById(timing.TimeZoneId)):yyyy-MM-dd HH:mm zzz}. Both PCs must be online.");
                    else UiTone.Wrapped(UiTone.Warning, validation);
                    ImGui.TextWrapped("Pauses AR Multi on both PCs, waits for current AR work, runs the transfer, then enables AR Multi. Paused or unfinished jobs block the queue.");
                    ImGui.BeginDisabled(job == null || timing == null);
                    if (TraderStyle.PrimaryButton(editSchedule.HasValue ? "Save timing" : "Create enabled schedule"))
                    {
                        var snapshot = job == null ? null : JsonConvert.DeserializeObject<SavedTransferJob>(JsonConvert.SerializeObject(job));
                        var name = scheduleName; var id = editSchedule;
                        plugin.QueueAction(() => { if (id.HasValue) Service.EditScheduleTiming(id.Value, timing!, name); else Service.AddSchedule(snapshot!, timing!, name); });
                        editSchedule = null;
                    }
                    ImGui.EndDisabled();
                    if (editSchedule.HasValue) { ImGui.SameLine(); if (ImGui.Button("Cancel edit")) editSchedule = null; }
                }
                TraderStyle.EndCard();
                TraderStyle.Section("Upcoming & saved", "Missed occurrences are skipped after their catch-up window; old runs are never replayed as a backlog.");
                if (Service.Schedules.Count == 0) ImGui.TextWrapped("No schedules yet. Save a transfer job, then choose its first run above.");
                foreach (var schedule in Service.Schedules.ToArray())
                {
                    ImGui.PushID(schedule.Id.ToString());
                    var enabled = schedule.Enabled;
                    ImGui.BeginDisabled(schedule.NextDue == null);
                    if (ImGui.Checkbox("##Enabled", ref enabled)) { var value = enabled; plugin.QueueAction(() => Service.SetScheduleEnabled(schedule.Id, value)); }
                    ImGui.EndDisabled(); ImGui.SameLine();
                    var next = schedule.NextDue is { } at ? TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(schedule.Timing.TimeZoneId)).ToString("ddd dd MMM HH:mm zzz") : "No future run";
                    if (ImGui.CollapsingHeader($"{schedule.Name} — {next}"))
                    {
                        ImGui.TextWrapped($"{schedule.Timing.Recurrence} · {schedule.Timing.TimeZoneId} · catch-up {schedule.Timing.CatchUpMinutes} min");
                        ImGui.TextWrapped(schedule.LastResult);
                        foreach (var item in schedule.Job.ItemDefinitions)
                        {
                            UiTone.Wrapped(UiTone.Info, $"{ItemName(item.ItemId)}{(item.HighQuality ? " HQ" : "")} · {ModeName(item.QuantityMode)}");
                            foreach (var recipient in item.Recipients) ImGui.TextWrapped($"  {CharacterName(recipient.CharacterId)}: {recipient.Quantity?.ToString("N0") ?? "remaining available stock"}");
                        }
                        if (ImGui.SmallButton("Edit timing"))
                        {
                            editSchedule = schedule.Id; scheduleName = schedule.Name; scheduleDate = schedule.Timing.LocalStart.ToString("yyyy-MM-dd");
                            scheduleTime = schedule.Timing.LocalStart.ToString("HH:mm"); scheduleZone = schedule.Timing.TimeZoneId;
                            recurrence = (int)schedule.Timing.Recurrence; catchUpMinutes = schedule.Timing.CatchUpMinutes;
                        }
                        ImGui.SameLine(); if (ImGui.SmallButton("Remove schedule")) deleteSchedule = schedule.Id;
                        if (deleteSchedule == schedule.Id)
                        {
                            ImGui.TextWrapped("Remove future starts? An existing run and its history are retained.");
                            if (ImGui.SmallButton("Confirm removal")) { plugin.QueueAction(() => Service.RemoveSchedule(schedule.Id)); deleteSchedule = null; }
                            ImGui.SameLine(); if (ImGui.SmallButton("Keep schedule")) deleteSchedule = null;
                        }
                    }
                    ImGui.PopID();
                }
            }
        }
        ImGui.EndChild();
    }

    private TransferScheduleTiming? ReadScheduleTiming(out string error)
    {
        try
        {
            if (!DateTime.TryParseExact(scheduleDate + " " + scheduleTime, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new ArgumentException("Enter a valid date and 24-hour time.");
            var value = new TransferScheduleTiming { LocalStart = date, TimeZoneId = scheduleZone.Trim(), Recurrence = (TransferRecurrence)recurrence, CatchUpMinutes = catchUpMinutes };
            value.Validate(); error = ""; return value;
        }
        catch (Exception e) { error = e.Message; return null; }
    }

    public void DrawPastTrades()
    {
        if (ImGui.BeginChild("PastTradesPage", Vector2.Zero))
        {
            ImGui.InputTextWithHint("##HistorySearch", "Search job, schedule, character or item…", ref historySearch, 200);
            ImGui.Checkbox("Only ended runs / shortages / skips", ref historyIssuesOnly);
            ImGui.TextDisabled("Stored on the coordinator PC. Totals count final deliveries, not relay hops.");
            if (Service.TradeHistory.Error.Length != 0) UiTone.Wrapped(UiTone.Error, Service.TradeHistory.Error);
            if (Service.TradeHistoryError.Length != 0) UiTone.Wrapped(UiTone.Error, Service.TradeHistoryError);
            var receipts = Service.TradeHistory.Records.Where(r => (!historyIssuesOnly || r.Outcome != "Completed")
                && (historySearch.Length == 0 || (r.Name + " " + r.Schedule + " " + string.Join(" ", r.Characters.Values) + " " + string.Join(" ", r.Items.Select(i => ItemName(i.ItemId))))
                    .Contains(historySearch, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (receipts.Length == 0) ImGui.TextWrapped("No matching past trades. Finished and safely ended transfer jobs will appear here.");
            foreach (var receipt in receipts)
            {
                ImGui.PushID(receipt.Id.ToString());
                var total = receipt.Items.Aggregate(0UL, (sum, i) => checked(sum + i.Delivered));
                if (ImGui.CollapsingHeader($"{receipt.EndedAt.ToLocalTime():dd MMM yyyy HH:mm} · {receipt.Name} · {total:N0} delivered"))
                {
                    TraderStyle.Badge(receipt.Outcome, receipt.Outcome == "Completed" ? UiTone.Good : UiTone.Warning);
                    ImGui.TextWrapped(receipt.Detail);
                    ImGui.TextWrapped($"Started: {receipt.StartedAt?.ToLocalTime().ToString("g") ?? "not recorded (older run)"} · Ended: {receipt.EndedAt.ToLocalTime():g}");
                    if (receipt.Schedule.Length > 0) ImGui.TextWrapped("Schedule: " + receipt.Schedule);
                    if (ImGui.SmallButton("Copy detailed receipt")) ImGui.SetClipboardText(JsonConvert.SerializeObject(receipt, Formatting.Indented));
                    foreach (var item in receipt.Items)
                    {
                        ImGui.Separator();
                        UiTone.Wrapped(UiTone.Info, $"{ItemName(item.ItemId)}{(item.HighQuality ? " HQ" : "")} — {item.Delivered:N0} delivered / {item.Planned:N0} planned");
                        ImGui.TextWrapped($"Request: {ModeName(item.Mode)} · unavailable at planning: {item.Shortage:N0}");
                        foreach (var recipient in item.Requested)
                        {
                            var actual = receipt.Movements.Where(m => m.FinalDelivery && m.Receiver == recipient.CharacterId && m.ItemId == item.ItemId && m.HighQuality == item.HighQuality).Sum(m => (decimal)m.Quantity);
                            ImGui.TextWrapped($"{ReceiptCharacter(receipt, recipient.CharacterId)} · requested {recipient.Quantity?.ToString("N0") ?? "all remaining"} · delivered {actual:N0}");
                        }
                        foreach (var pair in item.Withdrawn.Where(p => p.Value > 0)) ImGui.TextWrapped($"Retainer pickup · {ReceiptCharacter(receipt, pair.Key)} · {pair.Value:N0}");
                        foreach (var pair in item.Deposited.Where(p => p.Value > 0)) ImGui.TextWrapped($"Retainer deposit · {ReceiptCharacter(receipt, pair.Key)} · {pair.Value:N0}");
                    }
                    if (ImGui.TreeNode("Confirmed trade movements"))
                    {
                        foreach (var m in receipt.Movements.OrderBy(m => m.At))
                            ImGui.TextWrapped($"{m.At?.ToLocalTime():HH:mm:ss} · {ReceiptCharacter(receipt, m.Sender)} → {ReceiptCharacter(receipt, m.Receiver)} · {ItemName(m.ItemId)}{(m.HighQuality ? " HQ" : "")} × {m.Quantity:N0} · {(m.FinalDelivery ? "delivery" : "relay")}");
                        ImGui.TreePop();
                    }
                    if (receipt.Notes.Count > 0 && ImGui.TreeNode("Interruptions & notes"))
                    { foreach (var n in receipt.Notes) ImGui.TextWrapped($"{n.At.ToLocalTime():g} · {n.Message}"); ImGui.TreePop(); }
                }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }

    private static string ReceiptCharacter(TradeReceipt receipt, ulong id) => receipt.Characters.GetValueOrDefault(id, $"Character {id}");
}
