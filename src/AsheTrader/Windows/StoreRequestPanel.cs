using System.Numerics;
using AsheTrader.Core.Store;
using AsheTrader.Workflow;
using Dalamud.Bindings.ImGui;

namespace AsheTrader.Windows;

internal sealed partial class StorePanel
{
    private string requestFilter="";
    private void DrawRequestItems()
    {
        var service=plugin.Friends;
        if(service.SelectedAccess?.Url!=shopAccess?.Url)return;
        if(service.Offers.Length==0)
        {
            TraderStyle.Section("No delivery stock available");
            ImGui.TextWrapped("The owner needs to publish items for delivery and allow your invitation to request from a supplying PC.");
            if(TraderStyle.SecondaryButton("Browse the catalogue instead"))shopCatalogue=true;
            return;
        }
        if(service.Offers.Length==1&&friendSource!=service.Offers[0].SourceId){friendSource=service.Offers[0].SourceId;friendCart.Clear();}
        ImGui.BeginDisabled(service.Active!=null||service.HasPendingRequest||service.Busy);
        if(service.Offers.Length>1)
        {
            ImGui.SetNextItemWidth(-1);
            if(ImGui.BeginCombo("##RequestSupplier",service.Offers.FirstOrDefault(o=>o.SourceId==friendSource)?.Label??"Choose a supplying PC"))
            {
                foreach(var offer in service.Offers)if(ImGui.Selectable(offer.Label+"##"+offer.SourceId,offer.SourceId==friendSource))
                {friendSource=offer.SourceId;friendCart.Clear();confirmFriendRequest=false;}
                ImGui.EndCombo();
            }
        }
        var selected=service.Offers.FirstOrDefault(o=>o.SourceId==friendSource);
        if(selected==null){ImGui.TextDisabled("Choose which PC will supply this delivery.");ImGui.EndDisabled();return;}
        TraderStyle.Section(selected.Label,$"Available after reservations · published {selected.UpdatedAt.ToLocalTime():g}");
        if(!confirmFriendRequest)
        {
            ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##RequestFilter","Find items…",ref requestFilter,200);
            if(ImGui.BeginTable("RequestStock",3,ImGuiTableFlags.RowBg|ImGuiTableFlags.BordersInnerV|ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Item",ImGuiTableColumnFlags.WidthStretch,2);
                ImGui.TableSetupColumn("Available",ImGuiTableColumnFlags.WidthFixed,TraderStyle.Px(100));
                ImGui.TableSetupColumn("Your quantity",ImGuiTableColumnFlags.WidthFixed,TraderStyle.Px(160));ImGui.TableHeadersRow();
                foreach(var item in selected.Items.Where(i=>i.Name.Contains(requestFilter.Trim(),StringComparison.OrdinalIgnoreCase)).OrderBy(i=>i.Name).ThenBy(i=>i.HighQuality))
                {
                    var key=(item.ItemId,item.HighQuality);var amount=friendCart.GetValueOrDefault(key);
                    ImGui.TableNextRow();ImGui.TableNextColumn();ImGui.TextWrapped(item.Name+(item.HighQuality?" HQ":""));
                    ImGui.TableNextColumn();ImGui.TextColored(item.Quantity>0?TraderStyle.Accent:TraderStyle.Muted,$"{item.Quantity:N0}");
                    ImGui.TableNextColumn();ImGui.SetNextItemWidth(-1);ImGui.BeginDisabled(item.Quantity==0&&amount==0);
                    if(ImGui.InputInt($"##request{item.ItemId}{item.HighQuality}",ref amount))friendCart[key]=Math.Clamp(amount,0,(int)Math.Min(item.Quantity,1_000_000_000));
                    ImGui.EndDisabled();
                }
                ImGui.EndTable();
            }
        }
        var lines=selected.Items.Where(i=>friendCart.GetValueOrDefault((i.ItemId,i.HighQuality))>0)
            .Select(i=>i with{Quantity=(ulong)friendCart[(i.ItemId,i.HighQuality)]}).ToArray();
        var stockChanged=friendCart.Any(c=>c.Value>0&&!selected.Items.Any(i=>i.ItemId==c.Key.Item1&&i.HighQuality==c.Key.Item2&&(ulong)c.Value<=i.Quantity));
        if(stockChanged)
        {
            UiTone.Wrapped(UiTone.Warning,"Stock changed. Adjust your quantities before continuing.");
            if(ImGui.Button("Use available quantities"))
            {
                foreach(var key in friendCart.Keys.ToArray())friendCart[key]=(int)Math.Min((ulong)Math.Max(0,friendCart[key]),selected.Items.FirstOrDefault(i=>i.ItemId==key.Item1&&i.HighQuality==key.Item2)?.Quantity??0);
                confirmFriendRequest=false;
            }
        }
        if(!confirmFriendRequest)
        {
            ImGui.TextUnformatted($"{lines.Length} item types · {lines.Sum(i=>(long)i.Quantity):N0} items selected");
            ImGui.BeginDisabled(lines.Length==0||stockChanged);
            if(TraderStyle.PrimaryButton("Choose recipient & review"))confirmFriendRequest=true;
            ImGui.EndDisabled();ImGui.SameLine();
            if(ImGui.Button("Clear quantities"))friendCart.Clear();
        }
        else
        {
            if(TraderStyle.BeginCard("RequestReview"))
            {
                TraderStyle.Section("Review your delivery");
                foreach(var item in lines)ImGui.TextWrapped($"{item.Name}{(item.HighQuality?" HQ":"")} × {item.Quantity:N0}");
                ImGui.Spacing();
                var recipient=plugin.Config.Characters.FirstOrDefault(c=>c.ContentId==friendRecipient);
                ImGui.SetNextItemWidth(-1);
                if(ImGui.BeginCombo("##DeliverTo",recipient==null?"Choose your receiving character":$"{recipient.Name}@{recipient.World}"))
                {
                    foreach(var character in plugin.Config.Characters.OrderBy(c=>c.World).ThenBy(c=>c.Name))
                        if(ImGui.Selectable($"{character.Name}@{character.World}##{character.ContentId}",friendRecipient==character.ContentId))friendRecipient=character.ContentId;
                    ImGui.EndCombo();
                }
                if(ImGui.SmallButton("Refresh characters from AutoRetainer"))plugin.QueueAction(plugin.Workflow.RefreshLocalRoster);
                ImGui.TextWrapped("Both games must stay running. This will log in your recipient, travel, receive and deposit these items. AR Multi pauses for delivery and resumes after successful completion.");
                ImGui.BeginDisabled(recipient==null||lines.Length==0||stockChanged||!plugin.Workflow.CanStartFriendDelivery);
                if(TraderStyle.PrimaryButton("Reserve stock & request delivery"))
                {var recipientId=friendRecipient;var offer=selected;confirmFriendRequest=false;plugin.QueueAction(()=>service.Request(offer,recipientId,lines));storePage=StorePage.Activity;incomingHistory=false;}
                ImGui.EndDisabled();ImGui.SameLine();
                if(ImGui.Button("Back to items"))confirmFriendRequest=false;
                if(!plugin.Workflow.CanStartFriendDelivery)ImGui.TextWrapped("Finish this PC's current job before requesting a delivery.");
            }
            TraderStyle.EndCard();
        }
        ImGui.EndDisabled();
    }

    private void DrawDeliveryBanner()
    {
        var service=plugin.Friends;
        if(service.Active is not {} active)
        {
            if(service.HasPendingRequest)
            {
                UiTone.Wrapped(UiTone.Warning,"A request submission needs to be checked. Its original request ID is saved.");
                ImGui.BeginDisabled(service.Busy);
                if(ImGui.Button("Check submission"))plugin.QueueAction(service.ClearUnsubmittedRequest);
                ImGui.SameLine();if(ImGui.Button("Retry saved submission"))plugin.QueueAction(service.Reconnect);
                ImGui.EndDisabled();
            }
            return;
        }
        if(TraderStyle.BeginCard("CurrentStoreDelivery"))
        {
            var paused=service.NeedsReconnect||service.Supplying&&plugin.Workflow.Run is {Paused:true,Step:not CoordinatorStep.Finished};
            TraderStyle.Badge(paused?"NEEDS ATTENTION":active.State.ToUpperInvariant(),paused?TraderStyle.Gold:TraderStyle.Accent);
            ImGui.SameLine();ImGui.TextWrapped($"{(service.Supplying?"Supplying":"Receiving")} · {active.Recipient.Name}@{active.Recipient.World}");
            ImGui.TextWrapped(service.Supplying&&plugin.Workflow.FriendDeliveryConnected?plugin.Workflow.Status:service.Status);
            ImGui.BeginDisabled(service.Busy);
            if(service.NeedsReconnect&&ImGui.Button("Reconnect saved delivery"))plugin.QueueAction(service.Reconnect);
            if(active.State=="queued"&&ImGui.Button("Cancel queued request"))plugin.QueueAction(()=>service.Cancel(active));
            if(active.State=="active"&&service.Supplying&&plugin.Workflow.FriendDeliveryConnected)
            {
                if(plugin.Workflow.Run is {Paused:true,Step:not CoordinatorStep.Finished}&&!service.NeedsReconnect&&ImGui.Button("Resume / reconcile"))plugin.QueueAction(plugin.Workflow.Resume);
                if(ImGui.Button("End delivery safely"))plugin.QueueAction(()=>{plugin.Workflow.Pause("Ending friend delivery.");plugin.Workflow.EndPausedJob();});
            }
            ImGui.EndDisabled();
        }
        TraderStyle.EndCard();
    }
    private void DrawFriendOrders()
    {
        var service=plugin.Friends;
        TraderStyle.Section("Delivery activity","Queued requests and completed receipts.");
        ImGui.BeginDisabled(service.Busy);
        if(plugin.Store.Publisher!=null&&ImGui.RadioButton("Supplying from this PC",incomingHistory))
        {incomingHistory=true;plugin.QueueAction(service.RefreshIncoming);}
        if(plugin.Store.Publisher!=null&&plugin.Store.Readers.Count>0)ImGui.SameLine();
        if(plugin.Store.Readers.Count>0&&ImGui.RadioButton("My requests",!incomingHistory))
        {incomingHistory=false;plugin.QueueAction(()=>service.Refresh(shopAccess?.Role=="reader"?shopAccess:null));}
        if(!incomingHistory&&plugin.Store.Readers.Count>1&&ImGui.BeginCombo("##RequestHistoryStore",service.SelectedAccess==null?"Choose store":new Uri(service.SelectedAccess.Url).Host))
        {
            foreach(var a in plugin.Store.Readers)if(ImGui.Selectable(new Uri(a.Url).Host))plugin.QueueAction(()=>service.Refresh(a));
            ImGui.EndCombo();
        }
        if(ImGui.Button("Refresh activity"))
        {if(incomingHistory&&plugin.Store.Publisher!=null)plugin.QueueAction(service.RefreshIncoming);else plugin.QueueAction(()=>service.Refresh());}
        ImGui.EndDisabled();
        var orders=incomingHistory?service.IncomingOrders:service.RequestedOrders;
        if(orders.Length==0)ImGui.TextDisabled(service.Busy?"Loading requests…":"No requests loaded. Refresh to check for recent deliveries.");
        foreach(var order in orders.OrderByDescending(o=>o.State is "queued" or "active").ThenByDescending(o=>o.CreatedAt))
        {
            if(!ImGui.CollapsingHeader($"{order.Recipient.Name} · {order.State} · {order.CreatedAt.ToLocalTime():g}##{order.Id}"))continue;
            foreach(var item in order.Items)ImGui.TextWrapped($"{item.Name}{(item.HighQuality?" HQ":"")} × {item.Quantity:N0} requested");
            ImGui.BeginDisabled(service.Busy);
            if(order.State=="queued"&&ImGui.Button($"Cancel request##{order.Id}"))plugin.QueueAction(()=>service.Cancel(order));
            ImGui.EndDisabled();
            if(order.Receipt is not {} json)continue;
            try
            {
                var receipt=Newtonsoft.Json.JsonConvert.DeserializeObject<TradeReceipt>(json,new Newtonsoft.Json.JsonSerializerSettings{MaxDepth=32,TypeNameHandling=Newtonsoft.Json.TypeNameHandling.None});
                if(receipt?.Items==null)continue;
                ImGui.TextWrapped(receipt.Outcome);
                foreach(var item in receipt.Items.Take(50))
                {
                    var name=order.Items.FirstOrDefault(i=>i.ItemId==item.ItemId&&i.HighQuality==item.HighQuality)?.Name??$"Item {item.ItemId}";
                    ImGui.TextWrapped($"{name}{(item.HighQuality?" HQ":"")}: {item.Delivered:N0} delivered · {item.Shortage:N0} short");
                }
            }
            catch(Newtonsoft.Json.JsonException){UiTone.Wrapped(UiTone.Warning,"This receipt could not be displayed. The supplying PC retains its local Past Trades record.");}
        }
    }
}
