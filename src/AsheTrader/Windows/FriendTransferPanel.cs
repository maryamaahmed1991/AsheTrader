using System.Numerics;
using AsheTrader.Core.Store;
using AsheTrader.Workflow;
using Dalamud.Bindings.ImGui;

namespace AsheTrader.Windows;

internal sealed partial class StorePanel
{
    private Guid friendProfile;
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
        TraderStyle.Section("Delivery profile",service.ProfileName);
        ImGui.TextWrapped("Choose a saved transfer job. Its items, providers on this PC, and meeting point become the delivery profile. The friend's chosen character replaces its recipients. Other PCs' providers are excluded.");
        ImGui.BeginDisabled(service.Busy||service.Active!=null||plugin.Store.Publisher==null);
        var selected=plugin.Config.Workflow.Jobs.FirstOrDefault(j=>j.Definition.Id==friendProfile);
        if(ImGui.BeginCombo("Saved job",selected?.Definition.Name??"Choose a job"))
        {
            foreach(var job in plugin.Config.Workflow.Jobs)if(ImGui.Selectable($"{job.Definition.Name}##friendProfile{job.Definition.Id}"))friendProfile=job.Definition.Id;
            ImGui.EndCombo();
        }
        ImGui.BeginDisabled(selected==null);
        if(ImGui.Button("Read cache & publish requestable stock")){var snapshot=selected!;plugin.QueueAction(()=>service.PublishProfile(snapshot));}
        ImGui.EndDisabled();
        var enabled=service.Enabled;if(ImGui.Checkbox("Accept permitted friend requests automatically on this PC",ref enabled)){var value=enabled;plugin.QueueAction(()=>service.SetEnabled(value));}
        ImGui.EndDisabled();
        ImGui.TextWrapped("Publishing does not enable automatic deliveries. Only friends you grant permission can reserve stock. A delivery waits for this PC's current job to finish. Refresh stock after deliveries or other inventory changes; active reservations prevent replacement.");
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
