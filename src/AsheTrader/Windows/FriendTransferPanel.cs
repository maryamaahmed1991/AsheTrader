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
    private void DrawFriendTransfers()
    {
        var service=plugin.Friends;
        ImGui.TextWrapped(service.Status);
        if(service.Error.Length>0)UiTone.Wrapped(UiTone.Warning,service.Error);
        if(service.NeedsReconnect&&ImGui.Button("Reconnect saved delivery"))plugin.QueueAction(service.Reconnect);
        if(service.HasPendingRequest&&!service.Busy&&ImGui.Button("Check / clear unsubmitted request"))plugin.QueueAction(service.ClearUnsubmittedRequest);
        if(service.Active is {} active)
        {
            TraderStyle.Badge(active.State.ToUpperInvariant(),TraderStyle.Gold);
            ImGui.SameLine();ImGui.TextUnformatted($"To {active.Recipient.Name}@{active.Recipient.World}");
            ImGui.TextWrapped("This request reserves its stock. An active delivery uses this PC until it finishes; your saved trusted-PC pairing is retained.");
            if(active.State=="queued"&&ImGui.Button("Cancel queued request"))plugin.QueueAction(()=>service.Cancel(active));
            if(active.State=="active"&&service.Supplying)
            {
                ImGui.TextWrapped(plugin.Workflow.Status);
                if(plugin.Workflow.Run is {Paused:true,Step:not CoordinatorStep.Finished}&&ImGui.Button("Resume delivery / reconcile"))plugin.QueueAction(plugin.Workflow.Resume);
                ImGui.SameLine();if(ImGui.Button("End delivery safely"))plugin.QueueAction(()=>{plugin.Workflow.Pause("Ending friend delivery.");plugin.Workflow.EndPausedJob();});
            }
            ImGui.Separator();
        }
        if(ImGui.BeginTabBar("FriendDeliveryTabs"))
        {
            if(ImGui.BeginTabItem("Request items")){DrawRequestItems();ImGui.EndTabItem();}
            if(ImGui.BeginTabItem("Supply from this PC")){DrawFriendSupplier();ImGui.EndTabItem();}
            if(plugin.Store.Owner!=null&&ImGui.BeginTabItem("Request permissions")){DrawFriendGrants();ImGui.EndTabItem();}
            if(ImGui.BeginTabItem("Requests & receipts")){DrawFriendOrders();ImGui.EndTabItem();}
            ImGui.EndTabBar();
        }
    }
    private void DrawRequestItems()
    {
        var service=plugin.Friends;
        TraderStyle.Section("Request a delivery","Only stock explicitly offered for requests is available here. Existing store-reader codes need the owner's request permission.");
        ImGui.BeginDisabled(service.Busy||service.Active!=null);
        if(ImGui.BeginCombo("Friend's store",service.SelectedAccess==null?"Select connected store":new Uri(service.SelectedAccess.Url).Host))
        {
            foreach(var access in plugin.Store.Readers)
                if(ImGui.Selectable(new Uri(access.Url).Host)){friendCart.Clear();friendSource="";plugin.QueueAction(()=>service.Refresh(access));}
            ImGui.EndCombo();
        }
        if(ImGui.Button("Refresh requestable stock"))plugin.QueueAction(()=>service.Refresh());
        var offer=service.Offers.FirstOrDefault(o=>o.SourceId==friendSource);
        if(ImGui.BeginCombo("Supplying PC",offer?.Label??"Choose a stock profile"))
        {
            foreach(var o in service.Offers)if(ImGui.Selectable($"{o.Label}##{o.SourceId}")){friendSource=o.SourceId;friendCart.Clear();}
            ImGui.EndCombo();
        }
        var recipient=plugin.Config.Characters.FirstOrDefault(c=>c.ContentId==friendRecipient);
        if(ImGui.Button("Refresh AutoRetainer characters"))plugin.QueueAction(plugin.Workflow.RefreshLocalRoster);
        if(ImGui.BeginCombo("Deliver to",recipient==null?"Select your character":$"{recipient.Name}@{recipient.World}"))
        {
            foreach(var c in plugin.Config.Characters.OrderBy(c=>c.World).ThenBy(c=>c.Name))
                if(ImGui.Selectable($"{c.Name}@{c.World}##friendRecipient{c.ContentId}"))friendRecipient=c.ContentId;
            ImGui.EndCombo();
        }
        if(offer!=null)
        {
            ImGui.TextDisabled($"Stock published {offer.UpdatedAt.ToLocalTime():g}. Reserved stock is excluded.");
            if(ImGui.BeginTable("FriendStock",3,ImGuiTableFlags.RowBg|ImGuiTableFlags.BordersInnerV|ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Item",ImGuiTableColumnFlags.WidthStretch,2);
                ImGui.TableSetupColumn("Available");ImGui.TableSetupColumn("Request");ImGui.TableHeadersRow();
                foreach(var item in offer.Items.OrderBy(x=>x.Name).ThenBy(x=>x.HighQuality))
                {
                    var key=(item.ItemId,item.HighQuality);var amount=friendCart.GetValueOrDefault(key);
                    ImGui.TableNextRow();ImGui.TableNextColumn();ImGui.TextUnformatted(item.Name+(item.HighQuality?" HQ":""));
                    ImGui.TableNextColumn();ImGui.TextColored(item.Quantity>0?UiTone.Good:TraderStyle.Muted,$"{item.Quantity:N0}");
                    ImGui.TableNextColumn();ImGui.SetNextItemWidth(-1);
                    if(ImGui.InputInt($"##friendQty{item.ItemId}{item.HighQuality}",ref amount))friendCart[key]=Math.Clamp(amount,0,(int)Math.Min(item.Quantity,1_000_000_000));
                }
                ImGui.EndTable();
            }
            ImGui.BeginDisabled(recipient==null||!friendCart.Any(c=>c.Value>0));
            if(TraderStyle.PrimaryButton("Continue",new Vector2(130,32)))confirmFriendRequest=true;
            ImGui.EndDisabled();
            if(confirmFriendRequest)
            {
                ImGui.TextWrapped("Reserve these quantities and allow this delivery to log into the selected character, pause AR Multi, travel, receive the items and deposit them to retainers as needed? AR Multi will be enabled after completion. Both games must remain running.");
                if(ImGui.Button("Request & receive"))
                {
                    var lines=offer.Items.Where(i=>friendCart.GetValueOrDefault((i.ItemId,i.HighQuality))>0).Select(i=>i with {Quantity=(ulong)friendCart[(i.ItemId,i.HighQuality)]}).ToArray();
                    var selected=friendRecipient;var chosen=offer;confirmFriendRequest=false;
                    plugin.QueueAction(()=>service.Request(chosen,selected,lines));
                }
                ImGui.SameLine();if(ImGui.Button("Back##friendRequest"))confirmFriendRequest=false;
            }
        }
        else ImGui.TextWrapped("No profile selected. Ask the owner to publish requestable stock and grant your invitation access to its supplying PC.");
        ImGui.EndDisabled();
    }
    private void DrawFriendSupplier()
    {
        var service=plugin.Friends;
        var profile=service.Supply;
        var changed=false;
        TraderStyle.Section("Supply from this PC","Choose who supplies items and what friends may request. No transfer job is needed.");
        if(plugin.Store.Publisher==null)UiTone.Wrapped(UiTone.Warning,"Connect a publisher code in Access & invitations to supply items from this PC.");
        ImGui.BeginDisabled(service.Busy||service.Active!=null||plugin.Store.Publisher==null);
        var name=profile.Name;
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
        if(TraderStyle.BeginCard("SupplyMeeting"))
        {
            TraderStyle.Section("3. Meeting point","One outdoor location; the world is chosen automatically for each delivery.");
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
        if(changed)plugin.QueueAction(service.SaveSupplyDraft);
        ImGui.BeginDisabled(invalid!=null);
        if(TraderStyle.PrimaryButton("Read cache & publish requestable stock"))plugin.QueueAction(service.PublishSupply);
        ImGui.EndDisabled();
        ImGui.TextDisabled($"Published profile: {service.ProfileName}");
        var enabled=service.Enabled;if(ImGui.Checkbox("Accept permitted friend requests automatically on this PC",ref enabled)){var value=enabled;plugin.QueueAction(()=>service.SetEnabled(value));}
        ImGui.EndDisabled();
        ImGui.TextWrapped("Edits are saved as a draft. Publish to replace the offered selection; the existing published profile stays active until then. Stock comes from selected providers' carried bags and retainers. Refresh after inventory changes; outstanding reservations prevent replacement.");
    }
    private void DrawFriendGrants()
    {
        var service=plugin.Friends;
        TraderStyle.Section("Who may request deliveries","Viewing stock stays read-only unless you explicitly grant delivery requests for a supplying PC.");
        ImGui.BeginDisabled(service.Busy||plugin.Store.Busy);
        if(ImGui.Button("Refresh invitations & permissions")){plugin.QueueAction(plugin.Store.RefreshManagement);plugin.QueueAction(()=>service.Refresh());}
        if(plugin.Store.Management is {} management)
        {
            foreach(var reader in management.Keys.Where(k=>k.Role=="reader"))
            {
                ImGui.TextColored(TraderStyle.Accent,reader.Label);
                foreach(var source in management.Sources)
                {
                    var enabled=service.Grants.Any(g=>g.ReaderId==reader.Id&&g.SourceId==source.Id);
                    if(ImGui.Checkbox($"May request from {source.Label}##{reader.Id}{source.Id}",ref enabled))pendingGrant=(reader.Id,source.Id,enabled);
                }
            }
        }
        if(pendingGrant is {} grant)
        {
            UiTone.Wrapped(UiTone.Warning,grant.Enable?"This invitation will be allowed to reserve offered stock and trigger automatic deliveries from this PC when enabled. Anyone holding the invitation can use that permission.":"Remove request access? Queued requests are cancelled. An active delivery stops communicating and must be reconciled before releasing its reservation.");
            if(ImGui.Button("Apply permission")){pendingGrant=null;plugin.QueueAction(()=>service.SetGrant(grant.Reader,grant.Source,grant.Enable));}
            ImGui.SameLine();if(ImGui.Button("Cancel##friendGrant"))pendingGrant=null;
        }
        ImGui.EndDisabled();
    }
    private void DrawFriendOrders()
    {
        var service=plugin.Friends;
        ImGui.BeginDisabled(service.Busy);
        if(ImGui.Button("Refresh my requests"))plugin.QueueAction(()=>service.Refresh());
        if(plugin.Store.Publisher!=null){ImGui.SameLine();if(ImGui.Button("Refresh incoming requests"))plugin.QueueAction(service.RefreshIncoming);}
        ImGui.EndDisabled();
        foreach(var order in service.Orders)
        {
            if(ImGui.CollapsingHeader($"{order.CreatedAt.ToLocalTime():g} · {order.Recipient.Name} · {order.State}##{order.Id}"))
            {
                foreach(var i in order.Items)ImGui.TextUnformatted($"{i.Name}{(i.HighQuality?" HQ":"")} × {i.Quantity:N0}");
                if(order.State=="queued"&&ImGui.Button($"Cancel request##{order.Id}"))plugin.QueueAction(()=>service.Cancel(order));
                if(order.Receipt is {} json)
                {
                    var receipt=Newtonsoft.Json.JsonConvert.DeserializeObject<TradeReceipt>(json);
                    if(receipt!=null){ImGui.TextWrapped(receipt.Outcome);foreach(var item in receipt.Items)ImGui.TextUnformatted($"Item {item.ItemId}: delivered {item.Delivered:N0}; shortfall {item.Shortage:N0}");}
                }
            }
        }
    }
}
