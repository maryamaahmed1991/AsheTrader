using System.Numerics;
using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using Dalamud.Bindings.ImGui;

namespace AsheTrader.Windows;

public sealed partial class WorkflowPanel
{
    private void DrawVisitProgress(CoordinatorRun run)
    {
        TraderStyle.Section(run.Job.Definition.Name, "Character visits · items share preparation, travel and trade windows");
        UiTone.Status(run.Status);
        if (run.Visit is { } visit)
        {
            if (ImGui.BeginTable("VisitParticipants", ImGui.GetContentRegionAvail().X >= TraderStyle.Px(530) ? 2 : 1,
                    ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGui.TextColored(TraderStyle.Gold, PeerName(visit.SenderPeerId));
                ImGui.TextWrapped(CharacterName(visit.SenderId));
                UiTone.Wrapped(UiTone.Info, VisitStage(visit.Sender.Stage));
                ImGui.TableNextColumn();
                ImGui.TextColored(TraderStyle.Gold, PeerName(visit.ReceiverPeerId));
                ImGui.TextWrapped(CharacterName(visit.ReceiverId));
                UiTone.Wrapped(UiTone.Info, VisitStage(visit.Receiver.Stage));
                ImGui.EndTable();
            }
            ImGui.Spacing();
            UiTone.Wrapped(TraderStyle.Accent, "This visit: " + string.Join(", ", visit.Lines.Select(line => ItemLabel(run.VisitItems[line.ItemIndex].Definition))));
            ImGui.TextWrapped($"Meeting on {run.MeetingWorldName} · {run.MeetingDataCenterName}");
            if (run.VisitBatch is { Lines.Count: > 0 } batch)
                foreach (var line in batch.Lines)
                    ImGui.TextWrapped($"Agreed offer: {ItemLabel(run.VisitItems[line.ItemIndex].Definition)} × {line.Quantity:N0}");
        }
        else if (run.Step != CoordinatorStep.Finished) ImGui.TextWrapped("Choosing the next funded character visit…");
        ImGui.Separator();
        foreach (var item in run.VisitItems.Values.OrderBy(item => item.ItemIndex))
        {
            ImGui.PushID($"visitProgress{item.ItemIndex}");
            ImGui.TextColored(TraderStyle.Gold, ItemLabel(item.Definition));
            DrawSkipNotices(item.SkippedCharacters, item.ItemSkipReason);
            if (item.Journal != null)
            {
                var ledger = new TransferJobLedger(item.Journal);
                foreach (var recipient in item.Journal.Plan.Definition.Recipients)
                {
                    var planned = item.Journal.Plan.Allocations.Where(allocation => allocation.RecipientCharacterId == recipient.CharacterId)
                        .Aggregate(0UL, (sum, allocation) => checked(sum + allocation.Quantity));
                    var delivered = ledger.DeliveredTo(recipient.CharacterId);
                    ImGui.TextWrapped($"{CharacterName(recipient.CharacterId)} · {delivered:N0} / {planned:N0} delivered");
                    UiTone.Progress(planned == 0 ? 1 : (float)((double)delivered / planned), new Vector2(-1, TraderStyle.Px(8)));
                }
                foreach (var shortage in item.Journal.Plan.UnmetTargets)
                    UiTone.Wrapped(UiTone.Warning, $"{CharacterName(shortage.CharacterId)}: {shortage.Missing:N0} short of the requested target.");
                if (ImGui.CollapsingHeader("Storage & relay details"))
                {
                    foreach (var pickup in item.Withdrawn.Where(pair => pair.Value > 0))
                        ImGui.TextWrapped($"Collected from retainers: {CharacterName(pickup.Key)} · {pickup.Value:N0}");
                    foreach (var deposit in item.Deposited.Where(pair => pair.Value > 0))
                        ImGui.TextWrapped($"Deposited: {CharacterName(deposit.Key)} · {deposit.Value:N0}");
                    foreach (var relay in item.AutomaticRelays.Values)
                        ImGui.TextWrapped($"Relay: {CharacterName(relay)} · {ledger.RelayCargo(relay):N0} still carried");
                }
            }
            ImGui.Spacing(); ImGui.PopID();
        }
        if (ImGui.CollapsingHeader("Operation details"))
            foreach (var pending in run.Pending)
                ImGui.TextWrapped($"{PeerName(pending.PeerId)} · {CharacterName(pending.Command.Operation.CharacterId)} · {OperationName(pending.Command)} · {(pending.Response == null ? "waiting" : pending.Response.Success ? "confirmed" : pending.Response.Error)}");
    }

    private static string VisitStage(VisitPreparationStage stage) => stage switch
    {
        VisitPreparationStage.Login => "Changing character",
        VisitPreparationStage.Withdraw => "Collecting assigned items",
        VisitPreparationStage.Deposit => "Storing delivered items",
        VisitPreparationStage.Travel => "Travelling to the meeting point",
        VisitPreparationStage.Ready => "Ready to trade",
        _ => "Checking carried items and space",
    };
}
