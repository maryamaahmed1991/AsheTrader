using System.Numerics;
using AsheTrader.Core.Utilities;
using Dalamud.Bindings.ImGui;

namespace AsheTrader.Windows;

internal sealed class UtilitiesPanel(Plugin plugin)
{
    private string? diagnostics;
    public void Draw()
    {
        if (Interlocked.Exchange(ref diagnostics, null) is { } report) ImGui.SetClipboardText(report);
        var service = plugin.Utilities;
        if (ImGui.BeginChild("UtilitiesBody", new Vector2(0, 0)))
        {
            if (TraderStyle.BeginCard("SaddlebagUnlock"))
            {
                TraderStyle.Section("Unlock saddlebags", "My Feisty Little Chocobo · one character at a time · this PC");
                TraderStyle.Badge(service.Available ? "QUESTIONABLE AVAILABLE" : "QUESTIONABLE REQUIRED", service.Available ? TraderStyle.Accent : TraderStyle.Gold);
                ImGui.TextWrapped("Requires level 30 on the active class/job and My Little Chocobo. Eligibility is checked after login. Questionable handles this quest using your existing combat and navigation setup.");
                if (!string.IsNullOrEmpty(service.Error)) UiTone.Wrapped(UiTone.Error, service.Error);
                if (service.Run != null) UiTone.Status(service.Run.Status);
                var enableAr = service.HasUnfinishedRun ? service.Run!.EnableAutoRetainerAfterCompletion : plugin.Config.UtilityEnableAutoRetainer;
                if (CompletionOption.Draw(ref enableAr, service.HasUnfinishedRun, false)) { plugin.Config.UtilityEnableAutoRetainer = enableAr; plugin.Save(); }
                CompletionOption.Status(plugin);
                if (service.HasUnfinishedRun)
                {
                    if (service.Active)
                    { if (ImGui.Button("Pause")) plugin.QueueAction(() => service.Pause()); }
                    else if (ImGui.Button("Resume")) plugin.QueueAction(service.Resume);
                    ImGui.SameLine();
                    if (ImGui.Button("End queue")) plugin.QueueAction(service.End);
                    var run = service.Run!;
                    ImGui.ProgressBar(run.Entries.Count == 0 ? 0 : (float)run.Index / run.Entries.Count, new(-1, 0), $"{run.Index} / {run.Entries.Count} processed");
                }
                else
                {
                    ImGui.BeginDisabled(!service.Available || plugin.Config.UtilityCharacters.Count == 0 || plugin.Workflow.Busy
                        || plugin.Workflow.Run is { Step: not Workflow.CoordinatorStep.Finished } || plugin.FuelQueue.HasUnfinishedRun);
                    if (ImGui.Button("Start selected characters")) plugin.QueueAction(service.Start);
                    ImGui.EndDisabled();
                }
                if (ImGui.Button("Copy diagnostics")) plugin.QueueAction(() => Interlocked.Exchange(ref diagnostics, plugin.Workflow.CaptureDiagnosticReport()));
            }
            TraderStyle.EndCard();
            if (TraderStyle.BeginCard("UtilityCharacters"))
            {
                TraderStyle.Section("Characters", $"{plugin.Config.UtilityCharacters.Count} selected");
                ImGui.BeginDisabled(service.HasUnfinishedRun);
                if (ImGui.Button("Refresh AutoRetainer characters")) plugin.QueueAction(plugin.Workflow.RefreshLocalRoster);
                ImGui.SameLine();
                if (ImGui.Button("Select All")) { plugin.Config.UtilityCharacters = plugin.Config.Characters.Select(c => c.ContentId).ToHashSet(); plugin.Save(); }
                ImGui.SameLine();
                if (ImGui.Button("Select None")) { plugin.Config.UtilityCharacters.Clear(); plugin.Save(); }
                ImGui.EndDisabled();
                foreach (var world in plugin.Config.Characters.OrderBy(c => c.World).ThenBy(c => c.Name).GroupBy(c => c.World))
                {
                    ImGui.Separator(); ImGui.TextUnformatted(world.Key);
                    foreach (var character in world)
                    {
                        var selected = plugin.Config.UtilityCharacters.Contains(character.ContentId);
                        ImGui.BeginDisabled(service.HasUnfinishedRun);
                        if (ImGui.Checkbox($"{character.Name}##utility{character.ContentId}", ref selected))
                        {
                            if (selected) plugin.Config.UtilityCharacters.Add(character.ContentId); else plugin.Config.UtilityCharacters.Remove(character.ContentId);
                            plugin.Save();
                        }
                        ImGui.EndDisabled();
                        var entry = service.Run?.Entries.FirstOrDefault(e => e.CharacterId == character.ContentId);
                        if (entry != null && entry.Result != "Waiting")
                        { ImGui.Indent(); UiTone.Wrapped(entry.Result.StartsWith("Skipped") ? TraderStyle.Gold : TraderStyle.Accent, entry.Result); ImGui.Unindent(); }
                    }
                }
            }
            TraderStyle.EndCard();
        }
        ImGui.EndChild();
    }
}
