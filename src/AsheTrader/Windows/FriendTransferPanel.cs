using System.Numerics;
using AsheTrader.Core.Store;
using AsheTrader.Workflow;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace AsheTrader.Windows;

internal sealed partial class StorePanel
{
    private string supplyItemSearch="",supplyCharacterSearch="";
    private string friendSource="";
    private ulong friendRecipient;
    private readonly Dictionary<(uint,bool),int> friendCart=[];
    private bool confirmFriendRequest;
    private (string Reader,string Source,bool Enable)? pendingGrant;
    private void DrawFriendSupplier()
    {
        var service=plugin.Friends;
        var profile=service.Supply;
        var changed=false;
        TraderStyle.Section("Items friends can request","Choose providers, choose items, then publish your stock.");
        TraderStyle.Badge(!service.HasPublishedSupply?"NOT PUBLISHED":service.SupplyHasChanges?"UNPUBLISHED CHANGES":"PUBLISHED",service.SupplyHasChanges?TraderStyle.Gold:TraderStyle.Accent);
        ImGui.SameLine();StoreHelp("SupplyStates","Draft, published, and accepting requests","Your selections are saved locally as a draft. Publishing reads Allagan Tools and replaces the offered items and counts. Existing published selections remain available until you publish again. Allow deliveries controls whether this PC starts permitted requests while idle; it does not remove stock or grant friends access. Manage those permissions in People & PCs. Refresh published stock after deliveries or other inventory changes.");
        if(service.HasPublishedSupply)ImGui.TextDisabled($"Published: {service.ProfileName}"+(service.PublishedAt is {} at?$" · {at.ToLocalTime():g}":""));
        if(service.Busy)ImGui.TextWrapped(service.Status);
        ImGui.BeginDisabled(service.Busy||service.Active!=null||plugin.Store.Publisher==null);
        if(ImGui.BeginTable("SupplySteps",3,ImGuiTableFlags.SizingStretchSame))
        {
            string[] steps=["1  Providers","2  Items","3  Publish"];
            for(var i=0;i<steps.Length;i++)
            {
                ImGui.TableNextColumn();
                if(supplyStep==i?TraderStyle.PrimaryButton(steps[i],new(-1,32)):TraderStyle.SecondaryButton(steps[i],new(-1,32)))supplyStep=i;
            }
            ImGui.EndTable();
        }
        if(supplyStep==0)
        {
        var name=profile.Name;
        ImGui.TextUnformatted("Name your supplies");
        ImGui.SetNextItemWidth(-1);
        if(ImGui.InputTextWithHint("##supplyName","Name shown to friends",ref name,81)){profile.Name=name;changed=true;}
        if(TraderStyle.BeginCard("SupplyProviders"))
        {
            TraderStyle.Section("1. Provider characters",$"{profile.Providers.Count} selected · this PC only");
            if(ImGui.Button("Refresh AutoRetainer characters##supply"))plugin.QueueAction(plugin.Workflow.RefreshLocalRoster);
            if(ImGui.CollapsingHeader("Choose provider characters###SupplyProvidersPicker",ImGuiTreeNodeFlags.DefaultOpen))
            {
                if(ImGui.Button("Select All##supplyProviders")){profile.Providers=plugin.Config.Characters.Select(c=>c.ContentId).Where(id=>id!=0).Distinct().ToList();changed=true;}
                ImGui.SameLine();
                if(ImGui.Button("Select None##supplyProviders")){profile.Providers.Clear();changed=true;}
                ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##supplyCharacterSearch","Filter characters or worlds…",ref supplyCharacterSearch,100);
                if(ImGui.BeginChild("SupplyCharacterList",new Vector2(0,TraderStyle.Px(170)),true))
                {
                    foreach(var world in plugin.Config.Characters.Where(c=>string.IsNullOrWhiteSpace(supplyCharacterSearch)||$"{c.Name}@{c.World}".Contains(supplyCharacterSearch.Trim(),StringComparison.OrdinalIgnoreCase)).GroupBy(c=>c.World).OrderBy(g=>g.Key))
                    {
                        ImGui.TextColored(TraderStyle.Accent,world.Key);
                        foreach(var character in world.OrderBy(c=>c.Name))
                        {
                            var selected=profile.Providers.Contains(character.ContentId);
                            if(ImGui.Checkbox($"{character.Name}##supplyProvider{character.ContentId}",ref selected))
                            {if(selected)profile.Providers.Add(character.ContentId);else profile.Providers.Remove(character.ContentId);changed=true;}
                        }
                    }
                    foreach(var missing in profile.Providers.Where(id=>!plugin.Config.Characters.Any(c=>c.ContentId==id)).ToArray())
                    {
                        UiTone.Wrapped(UiTone.Warning,$"Unavailable character {missing}");ImGui.SameLine();
                        if(ImGui.SmallButton($"Remove##missingSupply{missing}")){profile.Providers.Remove(missing);changed=true;}
                    }
                }
                ImGui.EndChild();
            }
        }
        TraderStyle.EndCard();
        }
        if(supplyStep==1)
        {
        if(TraderStyle.BeginCard("SupplyItems"))
        {
            TraderStyle.Section("2. Items to provide",$"{profile.Items.Count}/50 selected · normal and HQ stock are separate");
            catalog ??= Plugin.DataManager.GetExcelSheet<Item>().Where(x=>x.RowId>1&&!x.Name.IsEmpty&&!x.IsUntradable).Select(x=>(x.RowId,x.Name.ToString())).OrderBy(x=>x.Item2).ToList();
            ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##supplyItemSearch","Find items to provide…",ref supplyItemSearch,200);
            if(supplyItemSearch.Trim().Length>=2)
            {
                if(ImGui.BeginChild("SupplyItemMatches",new Vector2(0,TraderStyle.Px(145)),true))
                {
                    foreach(var item in catalog.Where(i=>i.Name.Contains(supplyItemSearch.Trim(),StringComparison.OrdinalIgnoreCase)).Take(80))
                    {
                        ImGui.PushID((int)item.Id);
                        foreach(var hq in new[]{false,true})
                        {
                            if(hq&&Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(item.Id)?.CanBeHq!=true)continue;
                            var selected=profile.Items.Any(i=>i.ItemId==item.Id&&i.HighQuality==hq);
                            ImGui.BeginDisabled(!selected&&profile.Items.Count>=50);
                            if(ImGui.Checkbox(hq?"HQ":"Normal",ref selected))
                            {if(selected)profile.Items.Add(new(){ItemId=item.Id,HighQuality=hq});else profile.Items.RemoveAll(i=>i.ItemId==item.Id&&i.HighQuality==hq);changed=true;}
                            ImGui.EndDisabled();ImGui.SameLine();
                        }
                        ImGui.TextUnformatted(item.Name);ImGui.PopID();
                    }
                }
                ImGui.EndChild();
            }
            foreach(var item in profile.Items.ToArray())
            {
                ImGui.PushID($"supplyItem{item.ItemId}{item.HighQuality}");
                var itemName=catalog.FirstOrDefault(i=>i.Id==item.ItemId).Name??$"Item {item.ItemId}";
                if(ImGui.SmallButton("Remove")){profile.Items.Remove(item);changed=true;ImGui.PopID();continue;}
                ImGui.SameLine();
                if(ImGui.CollapsingHeader($"{itemName}{(item.HighQuality?" HQ":"")} · {(item.Providers==null?"All selected providers":$"{item.Providers.Count} specific providers")}###SupplyItemProviders"))
                {
                    var custom=item.Providers!=null;
                    if(ImGui.Checkbox("Choose different providers for this item",ref custom)){item.Providers=custom?profile.Providers.ToList():null;changed=true;}
                    if(item.Providers is {} providers)
                    {
                        if(ImGui.SmallButton("Select All##itemProviders")){item.Providers=profile.Providers.ToList();providers=item.Providers;changed=true;}
                        ImGui.SameLine();if(ImGui.SmallButton("Select None##itemProviders")){providers.Clear();changed=true;}
                        foreach(var character in plugin.Config.Characters.Where(c=>profile.Providers.Contains(c.ContentId)).OrderBy(c=>c.World).ThenBy(c=>c.Name))
                        {
                            var selected=providers.Contains(character.ContentId);
                            if(ImGui.Checkbox($"{character.Name}@{character.World}##itemProvider{character.ContentId}",ref selected))
                            {if(selected)providers.Add(character.ContentId);else providers.Remove(character.ContentId);changed=true;}
                        }
                        if(providers.Any(id=>!profile.Providers.Contains(id)))
                        {
                            UiTone.Wrapped(UiTone.Warning,"Some item providers are no longer selected above. Reselect them or remove them here.");
                            if(ImGui.SmallButton("Remove deselected providers")){providers.RemoveAll(id=>!profile.Providers.Contains(id));changed=true;}
                        }
                    }
                }
                ImGui.PopID();
            }
            if(profile.Items.Count==0)ImGui.TextDisabled("Search above to select your first item.");
        }
        TraderStyle.EndCard();
        }
        if(supplyStep==2)
        {
        ImGui.TextWrapped($"{profile.Providers.Count} provider characters · {profile.Items.Count} item / quality selections");
        if(TraderStyle.BeginCard("SupplyMeeting"))
        {
            TraderStyle.Section("Meeting point","One outdoor location; the world is chosen automatically for each delivery.");
            if(profile.Meeting.TerritoryId!=0)
            {
                var place=Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(profile.Meeting.TerritoryId)?.PlaceName.Value.Name.ToString();
                ImGui.TextWrapped($"{place??profile.Meeting.TerritoryId.ToString()} · ({profile.Meeting.X:F1}, {profile.Meeting.Y:F1}, {profile.Meeting.Z:F1})");
            }
            if(ImGui.Button(profile.Meeting.TerritoryId==0?"Record my current location##supply":"Replace with my current location##supply"))plugin.QueueAction(service.CaptureSupplyMeeting);
            if(profile.Meeting.TerritoryId!=0&&ImGui.CollapsingHeader("Arrival aetheryte##supply"))
            {
                var choices=Plugin.DataManager.GetExcelSheet<Aetheryte>().Where(a=>a.IsAetheryte&&a.Territory.RowId==profile.Meeting.TerritoryId).ToArray();
                var current=choices.FirstOrDefault(a=>a.RowId==profile.Meeting.AetheryteId);
                if(ImGui.BeginCombo("##supplyAetheryte",current.RowId==0?"Select aetheryte":current.PlaceName.Value.Name.ToString()))
                {
                    foreach(var choice in choices)if(ImGui.Selectable(choice.PlaceName.Value.Name.ToString(),choice.RowId==profile.Meeting.AetheryteId)){profile.Meeting.AetheryteId=choice.RowId;changed=true;}
                    ImGui.EndCombo();
                }
            }
        }
        TraderStyle.EndCard();
        string? invalid=null;
        try{_ = profile.ToTemplate(plugin.Config.Characters.Select(c=>c.ContentId));}catch(InvalidOperationException ex){invalid=ex.Message;}
        if(invalid!=null)UiTone.Wrapped(UiTone.Warning,invalid);
        ImGui.BeginDisabled(invalid!=null);
        if(TraderStyle.PrimaryButton(service.HasPublishedSupply?"Refresh & publish stock":"Publish delivery stock"))plugin.QueueAction(service.PublishSupply);
        ImGui.EndDisabled();
        ImGui.TextWrapped("Publishes carried and retainer stock from Allagan Tools. Existing reservations must finish or be cancelled before replacing stock.");
        }
        if(changed)plugin.QueueAction(service.SaveSupplyDraft);
        ImGui.Spacing();
        if(supplyStep>0&&TraderStyle.SecondaryButton("Back##supplyStep"))supplyStep--;
        if(supplyStep<2)
        {
            if(supplyStep>0)ImGui.SameLine();
            ImGui.BeginDisabled(supplyStep==0?profile.Providers.Count==0:profile.Items.Count==0);
            if(TraderStyle.PrimaryButton(supplyStep==0?"Next: choose items":"Next: publish"))supplyStep++;
            ImGui.EndDisabled();
        }
        if(service.HasPublishedSupply)
        {
            ImGui.Separator();
            var enabled=service.Enabled;
            if(ImGui.Checkbox("Allow deliveries while this PC is idle",ref enabled)){var value=enabled;plugin.QueueAction(()=>service.SetEnabled(value));}
            ImGui.TextDisabled(enabled?"Permitted requests will start when this PC is ready.":"Automatic deliveries are off. Published stock is still visible.");
        }
        ImGui.EndDisabled();
        if(plugin.Store.Owner!=null&&TraderStyle.SecondaryButton("Manage who can request"))OpenStorePage(StorePage.People);
    }
}
