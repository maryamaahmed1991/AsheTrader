using System.Globalization;
using System.Numerics;
using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace AsheTrader.Windows;

public sealed partial class WorkflowPanel
{
    private SavedTransferJob? recipientEditorJob;
    private ulong recipientEditorCharacter;
    private string recipientEditorSearch = "";
    private string recipientItemSearch = "";
    private bool recipientItemHq;

    private void DrawRecipientSetup(SavedTransferJob job)
    {
        if (!ReferenceEquals(recipientEditorJob, job))
        {
            recipientEditorJob = job;
            recipientEditorCharacter = RecipientAssignments.Characters(job).FirstOrDefault();
            recipientEditorSearch = "";
            recipientItemSearch = "";
            recipientItemHq = false;
        }
        var changed = false;
        if (TraderStyle.BeginCard("RecipientAssignments"))
        {
            TraderStyle.Section("Recipients & their items", "Choose a character, then add only the items they should receive.");
            var width = ImGui.GetContentRegionAvail().X;
            if (width < TraderStyle.Px(520))
            {
                DrawRecipientChoices(job, compact: true);
                ImGui.Spacing();
                DrawSelectedRecipient(job, ref changed);
            }
            else if (ImGui.BeginTable("RecipientSetup", 2))
            {
                ImGui.TableSetupColumn("Recipients", ImGuiTableColumnFlags.WidthFixed, Math.Max(TraderStyle.Px(150), width * .3f));
                ImGui.TableSetupColumn("Assigned items", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawRecipientChoices(job, compact: false);
                ImGui.TableNextColumn();
                DrawSelectedRecipient(job, ref changed);
                ImGui.EndTable();
            }
        }
        TraderStyle.EndCard();
        if (changed)
        {
            selectedJob = job.Definition.Id;
            selectedItemIndex = Math.Clamp(selectedItemIndex, 0, job.AdditionalItems.Count);
            plugin.Save();
        }
    }

    private void DrawRecipientChoices(SavedTransferJob job, bool compact)
    {
        ImGui.TextColored(TraderStyle.Gold, "CHOOSE A RECIPIENT");
        var assigned = RecipientAssignments.Characters(job).ToArray();
        var roster = Service.AllCharacters().ToArray();
        var candidates = assigned.Concat(roster.Select(entry => entry.Character.ContentId)).Distinct().ToArray();
        if (candidates.Length == 0)
        {
            UiTone.Wrapped(UiTone.Warning, "Connect both PCs and refresh characters to add recipients.");
            return;
        }
        if (compact)
        {
            ImGui.SetNextItemWidth(-1);
            if (!ImGui.BeginCombo("##recipientPicker", recipientEditorCharacter == 0 ? "Select a character" : CharacterName(recipientEditorCharacter))) return;
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##recipientSearch", "Search character or world", ref recipientEditorSearch, 100);
        if (ImGui.BeginChild("RecipientChoices", new Vector2(0, TraderStyle.Px(compact ? 220 : 320))))
        {
            foreach (var characterId in candidates)
            {
                var name = CharacterName(characterId);
                if (!name.Contains(recipientEditorSearch, StringComparison.OrdinalIgnoreCase)) continue;
                var count = RecipientAssignments.ForCharacter(job, characterId).Count();
                var entry = roster.FirstOrDefault(entry => entry.Character.ContentId == characterId);
                var detail = count == 0 ? "No items assigned" : $"{count} item{(count == 1 ? "" : "s")}";
                var width = ImGui.GetContentRegionAvail().X - TraderStyle.Px(28);
                var selected = TraderStyle.NavButton($"recipient{characterId}", FitRecipientText(name, width),
                    FitRecipientText(detail, width), recipientEditorCharacter == characterId);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{name}\n{(entry.Character == null ? "Roster unavailable" : PeerName(entry.PeerId))}\n{detail}");
                if (selected)
                {
                    recipientEditorCharacter = characterId;
                    recipientItemSearch = "";
                    if (compact) ImGui.CloseCurrentPopup();
                }
            }
        }
        ImGui.EndChild();
        if (compact) ImGui.EndCombo();
    }

    private void DrawSelectedRecipient(SavedTransferJob job, ref bool changed)
    {
        if (recipientEditorCharacter == 0)
        {
            ImGui.TextWrapped("Select a character to start their item list.");
        }
        else
        {
            UiTone.Wrapped(TraderStyle.Accent, CharacterName(recipientEditorCharacter));
            var assignments = RecipientAssignments.ForCharacter(job, recipientEditorCharacter).ToArray();
            if (assignments.Length == 0)
                ImGui.TextWrapped("No items assigned yet. Search below and select an item to add it for this character.");
            foreach (var (item, recipient) in assignments)
            {
                ImGui.PushID($"{item.Id}/{recipient.CharacterId}");
                if (TraderStyle.BeginCard("AssignedItem"))
                {
                    UiTone.Wrapped(TraderStyle.Gold, ItemLabel(item));
                    DrawRecipientAmount(item, recipient, ref changed);
                    var index = item.Recipients.IndexOf(recipient);
                    ImGui.TextDisabled($"Priority {index + 1} of {item.Recipients.Count} for this item");
                    if (ImGui.CollapsingHeader("Quantity rule and priority"))
                        DrawRecipientItemRules(item, recipient, ref changed);
                    if (TraderStyle.DangerButton("Remove item", new Vector2(-1, 30)))
                    {
                        changed |= RecipientAssignments.Remove(job, item.Id, recipient.CharacterId);
                        amountInputs.Remove($"{item.Id}/{recipient.CharacterId}");
                    }
                }
                TraderStyle.EndCard();
                ImGui.PopID();
            }
            ImGui.Separator();
            DrawRecipientItemSearch(job, ref changed);
        }
    }

    private static string FitRecipientText(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        var length = text.Length;
        while (length > 0 && ImGui.CalcTextSize(text[..length] + "…").X > width) length--;
        return text[..length] + "…";
    }

    private void DrawRecipientAmount(TransferJobDefinition item, RecipientRule recipient, ref bool changed)
    {
        var key = $"{item.Id}/{recipient.CharacterId}";
        if (recipient.Quantity == null)
        {
            UiTone.Wrapped(UiTone.Info, "Receives all remaining stock after higher-priority recipients.");
            return;
        }
        if (!amountInputs.TryGetValue(key, out var input))
            input = recipient.Quantity.Value.ToString(CultureInfo.InvariantCulture);
        var label = item.QuantityMode switch
        {
            JobQuantityMode.RestockToTotal => "Target total (bags + retainers)",
            JobQuantityMode.MoveAll => "Maximum to receive",
            _ => "Amount to deliver"
        };
        ImGui.TextWrapped(label);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##quantity", ref input, 24, ImGuiInputTextFlags.CharsDecimal))
        {
            amountInputs[key] = input;
            if (ulong.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) && quantity > 0)
            { recipient.Quantity = quantity; changed = true; }
        }
        if (!ulong.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var validQuantity) || validQuantity == 0)
            UiTone.Wrapped(UiTone.Error, "Enter a positive whole number. The previous amount stays saved until this is valid.");
    }

    private void DrawRecipientItemRules(TransferJobDefinition item, RecipientRule recipient, ref bool changed)
    {
        ImGui.TextWrapped($"The quantity rule applies to all {item.Recipients.Count} recipient(s) of {ItemLabel(item)}. Each keeps their own amount.");
        ImGui.TextUnformatted("Quantity rule");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##quantityRule", ModeName(item.QuantityMode)))
        {
            foreach (var mode in Enum.GetValues<JobQuantityMode>())
                if (ImGui.Selectable(ModeName(mode), item.QuantityMode == mode))
                {
                    changed |= RecipientAssignments.SetMode(item, mode);
                    foreach (var rule in item.Recipients) amountInputs.Remove($"{item.Id}/{rule.CharacterId}");
                }
            ImGui.EndCombo();
        }
        ImGui.TextWrapped(item.QuantityMode switch
        {
            JobQuantityMode.MoveAll => "Fill recipient caps in priority order. The last recipient can take the remainder.",
            JobQuantityMode.RestockToTotal => "Top each character up to their target, counting existing items in bags and retainers.",
            _ => "Deliver this additional amount during each run, regardless of existing stock."
        });
        var index = item.Recipients.IndexOf(recipient);
        if (item.QuantityMode == JobQuantityMode.MoveAll && index == item.Recipients.Count - 1)
        {
            var remainder = recipient.Quantity == null;
            if (ImGui.Checkbox("Take all remaining stock", ref remainder))
            {
                recipient.Quantity = remainder ? null : 10;
                amountInputs.Remove($"{item.Id}/{recipient.CharacterId}");
                changed = true;
            }
        }
        ImGui.BeginDisabled(index == 0 || recipient.Quantity == null || item.Recipients[index - 1].Quantity == null);
        if (TraderStyle.SecondaryButton("Earlier priority", new Vector2(-1, 30))) changed |= RecipientAssignments.MovePriority(item, recipient.CharacterId, -1);
        ImGui.EndDisabled();
        index = item.Recipients.IndexOf(recipient);
        ImGui.BeginDisabled(index >= item.Recipients.Count - 1 || recipient.Quantity == null || item.Recipients[index + 1].Quantity == null);
        if (TraderStyle.SecondaryButton("Later priority", new Vector2(-1, 30))) changed |= RecipientAssignments.MovePriority(item, recipient.CharacterId, 1);
        ImGui.EndDisabled();
        UiTone.Wrapped(TraderStyle.Muted, string.Join(" → ", item.Recipients.Select((rule, position) => $"{position + 1}. {CharacterName(rule.CharacterId)}")));
    }

    private void DrawRecipientItemSearch(SavedTransferJob job, ref bool changed)
    {
        UiTone.Wrapped(TraderStyle.Gold, "ADD AN ITEM FOR THIS CHARACTER");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##recipientItemSearch", "Search item name (at least 2 letters)", ref recipientItemSearch, 100);
        ImGui.Checkbox("High quality", ref recipientItemHq);
        itemCatalog ??= Plugin.DataManager.GetExcelSheet<Item>().Where(item => item.RowId > 19 && !item.IsUntradable && !item.Name.IsEmpty)
            .Select(item => (item.RowId, item.Name.ToString())).ToList();
        var existingItems = job.ItemDefinitions.Where(item => item.ItemId != 0 && item.HighQuality == recipientItemHq)
            .Select(item => item.ItemId).ToHashSet();
        var assignedItems = RecipientAssignments.ForCharacter(job, recipientEditorCharacter)
            .Where(pair => pair.Item.HighQuality == recipientItemHq).Select(pair => pair.Item.ItemId).ToHashSet();
        var results = itemCatalog.Where(candidate => !assignedItems.Contains(candidate.Id)
            && (recipientItemSearch.Length >= 2
                ? candidate.Name.Contains(recipientItemSearch, StringComparison.OrdinalIgnoreCase)
                : existingItems.Contains(candidate.Id))
            && (!recipientItemHq || Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(candidate.Id) is { CanBeHq: true }))
            .Take(80).ToArray();
        if (ImGui.BeginChild("RecipientItemMatches", new Vector2(0, TraderStyle.Px(170))))
        {
            if (results.Length == 0)
                ImGui.TextWrapped(recipientItemSearch.Length < 2 ? "Search to find another item." : "No matching items to add. Try another name or quality.");
            foreach (var candidate in results)
                if (ImGui.Selectable($"Add {candidate.Name}{(recipientItemHq ? " HQ" : "")}##assign{candidate.Id}"))
                {
                    try
                    {
                        RecipientAssignments.Assign(job, recipientEditorCharacter, candidate.Id, recipientItemHq, job.Definition.Sources);
                        changed = true;
                    }
                    catch (InvalidOperationException exception) { notice = exception.Message; }
                }
        }
        ImGui.EndChild();
        UiTone.Wrapped(TraderStyle.Muted, "New assignments start at 10. Adjust the amount above after adding.");
    }
}
