using AsheTrader.Core.Store;
using Dalamud.Bindings.ImGui;

namespace AsheTrader.Windows;

internal sealed partial class StorePanel
{
    private bool peopleLoaded;
    private bool invitePc;
    private void RefreshPeople()
    {
        if(plugin.Store.Owner==null)return;
        peopleLoaded=true;
        if(!plugin.Store.Busy)plugin.QueueAction(plugin.Store.RefreshManagement);
        if(!plugin.Friends.Busy)plugin.QueueAction(plugin.Friends.RefreshGrants);
    }
    private void DrawPeople()
    {
        var service=plugin.Store;
        TraderStyle.Section("People & PCs","Connect a store, invite friends, and choose who may request deliveries.");
        var connected=service.Owner!=null||service.Publisher!=null||service.Readers.Count>0;
        if(!connected||ImGui.CollapsingHeader("Connect another store or PC###StoreConnectCode"))
        {
            if(TraderStyle.BeginCard("ConnectStore"))
            {
                ImGui.TextUnformatted("Paste an invitation or setup code");ImGui.SameLine();
                StoreHelp("AccessKinds","Which code do I need?","Friends receive a friend invitation: it lets them browse, and you can grant delivery requests below. Each of your supplying PCs needs its own publisher code. Keep your owner code private; it manages the store and its invitations. Access is saved using your operating system's credential encryption.");
                ImGui.SetNextItemWidth(-1);
                if(ImGui.InputTextWithHint("##storeCode","Paste your private code…",ref code,4096,ImGuiInputTextFlags.Password))
                {try{parsed=StoreAccess.Parse(code);}catch{parsed=null;}}
                if(parsed!=null)ImGui.TextWrapped($"{new Uri(parsed.Url).Host} · {AccessName(parsed.Role)}");
                else if(code.Length>0)UiTone.Wrapped(UiTone.Warning,"Paste the complete code beginning with ashe-store:v1:.");
                ImGui.BeginDisabled(service.Busy||parsed==null);
                if(TraderStyle.PrimaryButton("Connect")){var saved=code;code="";parsed=null;shopLoadedIdentity="";plugin.QueueAction(()=>service.Import(saved));}
                ImGui.EndDisabled();
            }
            TraderStyle.EndCard();
        }
        if(connected)
        {
            if(TraderStyle.BeginCard("ConnectedStoreAccess"))
            {
                TraderStyle.Section("Connected on this PC");
                if(service.Owner is {} owner)ImGui.TextWrapped($"Store owner · {new Uri(owner.Url).Host}");
                if(service.Publisher is {} publisher)ImGui.TextWrapped($"Supplies items · {new Uri(publisher.Url).Host}");
                foreach(var reader in service.Readers)ImGui.TextWrapped($"Friend access · {new Uri(reader.Url).Host}");
                if(service.Readers.Count>0&&TraderStyle.SecondaryButton("Open shop"))OpenStorePage(StorePage.Shop);
                if(service.Owner!=null&&service.Publisher==null)
                {
                    ImGui.BeginDisabled(service.Busy);
                    if(TraderStyle.PrimaryButton("Use this PC to supply items"))plugin.QueueAction(()=>service.CreateKey("publisher","This PC",true));
                    ImGui.EndDisabled();
                }
            }
            TraderStyle.EndCard();
        }
        if(service.Busy)ImGui.TextWrapped(service.Status);
        if(service.Owner==null)return;
        if(!peopleLoaded&&!service.Busy&&!plugin.Friends.Busy)RefreshPeople();
        if(TraderStyle.BeginCard("InviteToStore"))
        {
            TraderStyle.Section("Add someone");
            ImGui.BeginDisabled(service.Busy);
            if(ImGui.RadioButton("Invite a friend",!invitePc))invitePc=false;
            ImGui.SameLine();if(ImGui.RadioButton("Add my other PC",invitePc))invitePc=true;
            ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##accessLabel",invitePc?"PC name, e.g. Second PC":"Friend's name",ref accessLabel,80);
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(accessLabel));
            if(TraderStyle.PrimaryButton(invitePc?"Create PC setup code":"Create friend invitation"))
            {var label=accessLabel;var role=invitePc?"publisher":"reader";plugin.QueueAction(()=>service.CreateKey(role,label,false));}
            ImGui.EndDisabled();ImGui.EndDisabled();
            if(service.GeneratedCode.Length>0)
            {
                UiTone.Wrapped(UiTone.Good,service.GeneratedRole=="reader"?"Invitation ready. Send it privately, then set delivery access below.":"PC code ready. Connect it in People & PCs on the supplying PC.");
                if(TraderStyle.PrimaryButton("Copy invitation / setup code")){ImGui.SetClipboardText(service.GeneratedCode);notice="Code copied. Share it only with the intended person or PC.";}
                ImGui.SameLine();if(ImGui.Button("Done##dismissCode"))service.DismissCode();
            }
        }
        TraderStyle.EndCard();
        TraderStyle.Section("Friends and supplying PCs");
        ImGui.BeginDisabled(service.Busy||plugin.Friends.Busy);
        if(ImGui.Button("Refresh people & access"))RefreshPeople();
        ImGui.EndDisabled();ImGui.SameLine();
        StoreHelp("StorePermissions","Delivery permissions","Each friend invitation starts with catalogue viewing only. Expand a friend and allow requests from specific supplying PCs. The PC must also publish delivery stock and enable automatic deliveries in My stock. Removing permission cancels queued requests; end an active delivery safely in Activity before revoking it.");
        if(service.Management is not {} management)return;
        foreach(var key in management.Keys.Where(k=>k.Role=="reader"))
        {
            ImGui.PushID(key.Id);
            var count=plugin.Friends.Grants.Count(g=>g.ReaderId==key.Id);
            if(ImGui.CollapsingHeader($"{key.Label} · {(count==0?"View only":$"Requests from {count} PC(s)")}###StoreFriend"))
            {
                if(management.Sources.Length==0)ImGui.TextWrapped("Add a supplying PC before granting delivery requests.");
                ImGui.BeginDisabled(service.Busy||plugin.Friends.Busy);
                foreach(var source in management.Sources)
                {
                    var enabled=plugin.Friends.Grants.Any(g=>g.ReaderId==key.Id&&g.SourceId==source.Id);
                    if(ImGui.Checkbox($"May request from {source.Label}##{source.Id}",ref enabled))pendingGrant=(key.Id,source.Id,enabled);
                }
                if(pendingGrant is {} grant&&grant.Reader==key.Id)
                {
                    ImGui.TextWrapped(grant.Enable?"Allow this friend to request offered stock from this PC?":"Remove delivery access? Queued requests will be cancelled. End any active delivery first.");
                    if(TraderStyle.PrimaryButton("Save delivery access")){pendingGrant=null;plugin.QueueAction(()=>plugin.Friends.SetGrant(grant.Reader,grant.Source,grant.Enable));}
                    ImGui.SameLine();if(ImGui.Button("Cancel permission change"))pendingGrant=null;
                }
                ImGui.EndDisabled();
                DrawRevokeAccess(key);
            }
            ImGui.PopID();
        }
        foreach(var key in management.Keys.Where(k=>k.Role=="publisher"))
        {
            ImGui.PushID(key.Id);
            if(ImGui.CollapsingHeader($"{key.Label} · Supplying PC###StoreSupplierAccess"))
            {
                ImGui.TextWrapped("Choose and publish this PC's items in My stock on that PC.");
                DrawRevokeAccess(key);
            }
            ImGui.PopID();
        }
        if(management.Keys.Length==0)ImGui.TextDisabled("No invitations or supplying PCs yet.");
    }
    private static string AccessName(string role)=>role switch{"owner"=>"Store owner","publisher"=>"Supplying PC",_=>"Friend invitation"};
    private void DrawRevokeAccess(StoreKey key)
    {
        ImGui.BeginDisabled(plugin.Store.Busy||plugin.Friends.Busy);
        if(ImGui.SmallButton("Remove access…"))revoke=key.Id;
        if(revoke==key.Id)
        {
            UiTone.Wrapped(UiTone.Warning,key.Role=="publisher"?"Remove this PC and its published stock?":"Revoke this invitation, including catalogue and delivery access?");
            if(TraderStyle.DangerButton("Remove access now")){var id=key.Id;revoke=null;plugin.QueueAction(()=>plugin.Store.Revoke(id));}
            ImGui.SameLine();if(ImGui.Button("Keep access"))revoke=null;
        }
        ImGui.EndDisabled();
    }
}
