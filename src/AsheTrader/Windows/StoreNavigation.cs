using System.Numerics;
using AsheTrader.Core.Store;
using Dalamud.Bindings.ImGui;

namespace AsheTrader.Windows;

internal sealed partial class StorePanel
{
    private enum StorePage { Shop, Stock, People, Activity }
    private StorePage storePage;
    private bool storePageInitialized;
    private StoreAccess? shopAccess;
    private string shopLoadedIdentity="";
    private bool shopCatalogue;
    private int supplyStep;
    private bool incomingHistory;

    public void Draw()
    {
        if(!storePageInitialized)
        {
            storePage=plugin.Store.Publisher!=null?StorePage.Stock:plugin.Store.Readers.Count>0?StorePage.Shop:StorePage.People;
            incomingHistory=plugin.Store.Publisher!=null;storePageInitialized=true;
        }
        if(ImGui.BeginChild("StoresPage",Vector2.Zero))
        {
            if(ImGui.BeginTable("StoreNavigation",4,ImGuiTableFlags.SizingStretchSame))
            {
                string[] labels=["Shop","My stock","People & PCs","Activity"];
                for(var i=0;i<labels.Length;i++)
                {
                    ImGui.TableNextColumn();
                    var clicked=storePage==(StorePage)i?TraderStyle.PrimaryButton(labels[i],new(-1,34)):TraderStyle.SecondaryButton(labels[i],new(-1,34));
                    if(clicked)OpenStorePage((StorePage)i);
                }
                ImGui.EndTable();
            }
            ImGui.Spacing();
            DrawDeliveryBanner();
            if(plugin.Store.Error.Length>0)UiTone.Wrapped(UiTone.Warning,plugin.Store.Error);
            if(plugin.Friends.Error.Length>0)UiTone.Wrapped(UiTone.Warning,plugin.Friends.Error);
            if(notice.Length>0){ImGui.TextWrapped(notice);if(ImGui.SmallButton("Dismiss##storeNotice"))notice="";}
            switch(storePage)
            {
                case StorePage.Shop: DrawShop();break;
                case StorePage.Stock: DrawMyStock();break;
                case StorePage.People: DrawPeople();break;
                case StorePage.Activity: DrawFriendOrders();break;
            }
        }
        ImGui.EndChild();
    }

    private void OpenStorePage(StorePage page)
    {
        if(storePage==page)return;
        storePage=page;
        if(page==StorePage.People)RefreshPeople();
        if(page==StorePage.Activity&&!plugin.Friends.Busy)
        {
            if(incomingHistory&&plugin.Store.Publisher!=null)plugin.QueueAction(plugin.Friends.RefreshIncoming);
            else if(plugin.Store.Readers.Count>0)plugin.QueueAction(()=>plugin.Friends.Refresh(shopAccess?.Role=="reader"?shopAccess:null));
        }
    }
    private void StoreHelp(string id,string title,string body) =>
        MascotGuide.HelpButton(id,()=>plugin.Mascot.Get(MascotPose.Coin),title,body,plugin.Config.ShowMascotIllustrations);

    private void DrawShop()
    {
        var access=plugin.Store.Readers.Concat(plugin.Store.Owner is {} owner?[owner]:Array.Empty<StoreAccess>())
            .Concat(plugin.Store.Publisher is {} publisher?[publisher]:Array.Empty<StoreAccess>()).DistinctBy(a=>a.Url).ToArray();
        if(access.Length==0)
        {
            TraderStyle.Section("Shop","Browse a catalogue or request items for your character.");
            ImGui.TextWrapped("Have an invitation? Connect it to see your friend's stock.");
            if(TraderStyle.PrimaryButton("Connect an invitation"))OpenStorePage(StorePage.People);
            return;
        }
        if(shopAccess==null||!access.Contains(shopAccess))shopAccess=access[0];
        if(confirmFriendRequest&&!shopCatalogue&&plugin.Friends.SelectedAccess?.Url==shopAccess.Url)
        {DrawRequestItems();return;}
        TraderStyle.Section("Shop","Browse a catalogue or request items for your character.");
        ImGui.BeginDisabled(plugin.Store.Busy||plugin.Friends.Busy||plugin.Friends.Active!=null||plugin.Friends.HasPendingRequest);
        ImGui.SetNextItemWidth(-1);
        if(ImGui.BeginCombo("##ShopStore",new Uri(shopAccess.Url).Host))
        {
            foreach(var a in access)if(ImGui.Selectable(new Uri(a.Url).Host,a==shopAccess))
            {shopAccess=a;shopCatalogue=a.Role!="reader";friendSource="";friendCart.Clear();confirmFriendRequest=false;shopLoadedIdentity="";browsePage=0;}
            ImGui.EndCombo();
        }
        if(ImGui.Button("Refresh stock"))RefreshShop();
        ImGui.SameLine();StoreHelp("ShopStockHelp","Catalogue and delivery stock","Available to request shows only items the owner has offered for delivery from a particular PC, after reservations. The catalogue is a separate, view-only selection and can include other items. Both are published cache snapshots. Delivery needs both games running; viewing the catalogue does not.");
        ImGui.EndDisabled();
        // Fetch once on first visit or after changing stores; never read/write inventory as a UI side effect.
        var identity=shopAccess.Url+"/"+shopAccess.Role;
        if(shopLoadedIdentity!=identity&&!plugin.Store.Busy&&!plugin.Friends.Busy)RefreshShop();
        if(plugin.Store.Busy||plugin.Friends.Busy){ImGui.TextColored(TraderStyle.Gold,"Refreshing store…");return;}
        if(shopAccess.Role!="reader")shopCatalogue=true;
        ImGui.BeginDisabled(shopAccess.Role!="reader");
        if(ImGui.RadioButton("Available to request",!shopCatalogue))shopCatalogue=false;
        ImGui.EndDisabled();ImGui.SameLine();
        if(ImGui.RadioButton("View catalogue",shopCatalogue))shopCatalogue=true;
        ImGui.Separator();
        if(shopCatalogue)
        {
            if(plugin.Store.SelectedAccess?.Url==shopAccess.Url)DrawBrowse();
            else ImGui.TextWrapped("This store's catalogue has not loaded. Use Refresh stock to try again.");
        }
        else DrawRequestItems();
    }
    private void RefreshShop()
    {
        if(shopAccess==null)return;
        var selected=shopAccess;
        shopLoadedIdentity=selected.Url+"/"+selected.Role;
        plugin.QueueAction(()=>plugin.Store.Browse(selected));
        if(selected.Role=="reader")plugin.QueueAction(()=>plugin.Friends.Refresh(selected));
    }

    private void DrawMyStock()
    {
        if(plugin.Store.Publisher==null)
        {
            TraderStyle.Section("Supply items from this PC","Connect this PC before choosing what to share.");
            if(TraderStyle.PrimaryButton("Set up this PC"))OpenStorePage(StorePage.People);
            return;
        }
        DrawFriendSupplier();
        ImGui.Spacing();
        if(ImGui.CollapsingHeader("View-only catalogue · optional###StoreViewOnly"))
        {
            ImGui.TextWrapped("A separate selection for friends to browse while you are offline. Publishing here does not make these items requestable.");
            if(plugin.Store.Busy)ImGui.TextWrapped(plugin.Store.Status);
            DrawPublisher();
        }
    }
}
