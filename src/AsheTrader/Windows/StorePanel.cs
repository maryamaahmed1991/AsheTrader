using System.Numerics;
using AsheTrader.Core.Search;
using AsheTrader.Core.Store;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace AsheTrader.Windows;

internal sealed class StorePanel(Plugin plugin)
{
    private string code="",itemSearch="",stockSearch="",accessLabel="",notice="";
    private List<(uint Id,string Name)>? catalog;
    private string? revoke;
    private bool clearConfirm;
    private StoreAccess? parsed;
    private int browsePage;
    private const int PageSize=100;
    public void Draw()
    {
        var service=plugin.Store;
        if(ImGui.BeginChild("StoresPage",Vector2.Zero))
        {
            TraderStyle.Badge(service.Busy?"WORKING":"PRIVATE CATALOGUE",service.Busy?TraderStyle.Gold:TraderStyle.Accent);
            ImGui.SameLine();ImGui.TextDisabled("No PC pairing required");
            ImGui.TextWrapped(service.Status);
            if(service.Error.Length>0) UiTone.Wrapped(UiTone.Warning,service.Error);
            if(notice.Length>0) UiTone.Wrapped(UiTone.Good,notice);
            ImGui.Spacing();
            if(ImGui.BeginTabBar("StoreTabs"))
            {
                if(ImGui.BeginTabItem("My store")){DrawPublisher();ImGui.EndTabItem();}
                if(ImGui.BeginTabItem("Browse stores")){DrawBrowse();ImGui.EndTabItem();}
                if(ImGui.BeginTabItem("Access & invitations")){DrawAccess();ImGui.EndTabItem();}
                ImGui.EndTabBar();
            }
        }
        ImGui.EndChild();
    }
    private void DrawPublisher()
    {
        var service=plugin.Store;var settings=plugin.Config.Store;
        if(service.Publisher==null)
        {
            MascotGuide.Card("AsheStoreGuide", plugin.Config.ShowMascotIllustrations ? plugin.Mascot.Get(MascotPose.Parcel) : null,
                "Your little shop starts here", "Connect your store in Access & invitations, then choose the items and characters you want to share. You can preview your stock before publishing.", plugin.Config.ShowMascotIllustrations);
            if(TraderStyle.BeginCard("PublisherWelcome"))
            {
                TraderStyle.Section("Your stock, on your terms", "Pick the items your friends can see. Your character and retainer names stay on this PC.");
                ImGui.TextWrapped("Open Access & invitations to connect your owner code, then authorize this PC. On your other PC, connect a publishing code from the same store.");
            }TraderStyle.EndCard();return;
        }
        if(TraderStyle.BeginCard("PublishSelection"))
        {
            TraderStyle.Section("1. Choose your stock",$"{settings.Items.Count} items • {settings.Characters.Count} characters selected on this PC");
            ImGui.TextWrapped("Only carried bags and retainer bags are included. Market listings, equipped items and shared FC storage are excluded.");
            ImGui.BeginDisabled(service.Busy);
            catalog ??= Plugin.DataManager.GetExcelSheet<Item>().Where(x=>x.RowId>1 && !x.Name.IsEmpty && !x.IsUntradable).Select(x=>(x.RowId,x.Name.ToString())).OrderBy(x=>x.Item2).ToList();
            ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##storeItemFilter","Find items to share…",ref itemSearch,200);
            if(itemSearch.Trim().Length>=2)
            {
                if(ImGui.BeginChild("MatchingStoreItems",new Vector2(0,TraderStyle.Px(145)),true))
                    foreach(var item in catalog.Where(x=>x.Name.Contains(itemSearch.Trim(),StringComparison.OrdinalIgnoreCase)).Take(100))
                    {var selected=settings.Items.Contains(item.Id);if(ImGui.Checkbox($"{item.Name}##storeItem{item.Id}",ref selected)){if(selected)settings.Items.Add(item.Id);else settings.Items.Remove(item.Id);service.ChangedSelection();}}
                ImGui.EndChild();
            }
            if(ImGui.CollapsingHeader($"Shared items ({settings.Items.Count})###StoreSharedItems"))
            {
                foreach(var id in settings.Items.Order().ToArray())
                {if(ImGui.SmallButton($"Remove##shared{id}")){settings.Items.Remove(id);service.ChangedSelection();}ImGui.SameLine();ImGui.TextUnformatted(catalog.FirstOrDefault(x=>x.Id==id).Name ?? $"Item {id}");}
            }
            // Keep expansion state independent of the changing selection-count caption.
            if(ImGui.CollapsingHeader($"Characters on this PC ({settings.Characters.Count} selected)###StoreCharacters"))
            {
                if(ImGui.Button("Select All##storeCharacters"))
                {
                    settings.Characters=plugin.Config.Characters.Select(x=>x.ContentId).Where(id=>id!=0).ToHashSet();
                    service.ChangedSelection();
                }
                ImGui.SameLine();
                if(ImGui.Button("Select None##storeCharacters"))
                {
                    settings.Characters.Clear();
                    service.ChangedSelection();
                }
                ImGui.TextWrapped("Select a different set on each PC. The server rejects overlapping inventories to prevent double counting.");
                if(plugin.Config.Characters.Count==0) ImGui.TextWrapped("Import your AutoRetainer roster from Connection first.");
                foreach(var world in plugin.Config.Characters.GroupBy(x=>x.World).OrderBy(x=>x.Key))
                {
                    ImGui.TextColored(TraderStyle.Gold,world.Key);
                    foreach(var character in world.OrderBy(x=>x.Name))
                    {var selected=settings.Characters.Contains(character.ContentId);if(ImGui.Checkbox($"{character.Name}##publishChar{character.ContentId}",ref selected)){if(selected)settings.Characters.Add(character.ContentId);else settings.Characters.Remove(character.ContentId);service.ChangedSelection();}}
                }
            }
            ImGui.EndDisabled();
        }TraderStyle.EndCard();
        if(TraderStyle.BeginCard("StockPreview"))
        {
            TraderStyle.Section("2. Preview & publish", "A cached stock estimate. Reading the cache does not verify live inventory.");
            ImGui.BeginDisabled(service.Busy || settings.Items.Count==0 || settings.Characters.Count==0);
            if(TraderStyle.PrimaryButton("Refresh preview",new(145,32)))plugin.QueueAction(()=>service.PreviewStock());
            ImGui.EndDisabled();ImGui.SameLine();
            ImGui.BeginDisabled(service.Busy || !service.PreviewCurrent || service.Warnings.Length>0);
            if(TraderStyle.PrimaryButton("Publish this stock",new(155,32)))plugin.QueueAction(service.Publish);
            ImGui.EndDisabled();
            if(service.Preview is {} preview)
            {
                ImGui.TextDisabled($"Cache read {service.PreviewAt?.ToLocalTime():g}. Inventory observation time is unknown.");
                var totals=preview.Owners.SelectMany(x=>x.Items).GroupBy(x=>(x.ItemId,x.Name,x.HighQuality)).Select(g=>new StoreCatalogueItem(g.Key.ItemId,g.Key.Name,g.Key.HighQuality,g.Aggregate(0UL,(sum,x)=>sum+x.Quantity),null)).OrderBy(x=>x.Name).ToArray();
                DrawItems(totals,false);
                if(totals.Length==0)UiTone.Wrapped(UiTone.Warning,"No selected stock remains. Publishing will remove this PC's previous item totals.");
            }
            foreach(var warning in service.Warnings.Take(10))UiTone.Wrapped(UiTone.Warning,warning);
            if(service.Warnings.Length>10)ImGui.TextWrapped($"And {service.Warnings.Length-10} more unavailable inventories.");
            ImGui.BeginDisabled(service.Busy || (!service.PreviewCurrent && !settings.AutoPublish));
            var auto=settings.AutoPublish;if(ImGui.Checkbox("Automatically refresh and publish every 5 minutes",ref auto))service.SetAuto(auto);
            ImGui.EndDisabled();
            ImGui.TextDisabled("Changing the selection turns automatic publishing off.");
        }TraderStyle.EndCard();
        if(TraderStyle.BeginCard("RemovePublished"))
        {
            TraderStyle.Section("Remove this PC's published stock");
            ImGui.TextWrapped("Turning off automatic publishing keeps the last snapshot available. Use this to remove it from the hosted catalogue.");
            ImGui.BeginDisabled(service.Busy);
            if(TraderStyle.SecondaryButton("Remove published stock…",new(210,30)))clearConfirm=true;
            if(clearConfirm)
            {
                UiTone.Wrapped(UiTone.Warning,"Remove this PC's stock and disable automatic publishing?");
                if(TraderStyle.DangerButton("Remove now",new(120,30))){clearConfirm=false;plugin.QueueAction(service.ClearPublished);}ImGui.SameLine();if(ImGui.Button("Cancel##clearStock"))clearConfirm=false;
            }
            ImGui.EndDisabled();
        }TraderStyle.EndCard();
    }
    private void DrawBrowse()
    {
        var service=plugin.Store;
        if(TraderStyle.BeginCard("BrowseStore"))
        {
            TraderStyle.Section(service.Catalogue?.Name ?? "Browse a private store","Connect a friend's invitation in Access & invitations. No game login or PC pairing is required to read published stock.");
            ImGui.BeginDisabled(service.Busy);
            if(ImGui.BeginCombo("Store service",service.Catalogue?.Name ?? "Select connected store"))
            {
                foreach(var access in service.Readers.Concat(service.Owner is {} owner?[owner]:Array.Empty<StoreAccess>()).DistinctBy(x=>x.Url))
                    if(ImGui.Selectable(new Uri(access.Url).Host)){browsePage=0;plugin.QueueAction(()=>service.Browse(access));}
                ImGui.EndCombo();
            }
            if(TraderStyle.SecondaryButton("Refresh stock",new(130,30)))plugin.QueueAction(()=>service.Browse());
            ImGui.EndDisabled();
            ImGui.SetNextItemWidth(-1);if(ImGui.InputTextWithHint("##stockSearch","coke, ceruleum, hardened sap",ref stockSearch,2048))browsePage=0;
            ImGui.TextDisabled("Comma-separated partial names. Leave empty to see all published items.");
            if(service.Catalogue is {} catalogue)
            {
                ImGui.TextDisabled($"Catalogue fetched {catalogue.RetrievedAt.ToLocalTime():g}");
                var times=catalogue.Sources.Where(x=>x.PublishedAt!=null).Select(x=>x.PublishedAt!.Value).ToArray();
                if(times.Length>0)UiTone.Wrapped(TraderStyle.Muted,$"Stock snapshots: {times.Min().ToLocalTime():g} to {times.Max().ToLocalTime():g}. These are publishing times, not live inventory verification.");
                if(times.Any(x=>DateTimeOffset.UtcNow-x>TimeSpan.FromHours(24)))UiTone.Wrapped(UiTone.Warning,"Some published stock is over a day old. Availability may have changed.");
                if(catalogue.Sources.Any(x=>x.PublishedAt==null))ImGui.TextWrapped("A connected publisher has not uploaded stock yet.");
                string[] terms=stockSearch.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
                var filtered=catalogue.Items.Where(x=>terms.Length==0 || terms.Any(term=>x.Name.Contains(term,StringComparison.OrdinalIgnoreCase))).ToArray();
                browsePage=Math.Clamp(browsePage,0,Math.Max(0,(filtered.Length-1)/PageSize));
                ImGui.TextUnformatted($"{filtered.Length:N0} matches");
                DrawItems(filtered.Skip(browsePage*PageSize).Take(PageSize),true);
                ImGui.BeginDisabled(browsePage==0);if(ImGui.Button("Previous"))browsePage--;ImGui.EndDisabled();ImGui.SameLine();
                ImGui.BeginDisabled((browsePage+1)*PageSize>=filtered.Length);if(ImGui.Button("Next"))browsePage++;ImGui.EndDisabled();
            }
        }TraderStyle.EndCard();
    }
    private static void DrawItems(IEnumerable<StoreCatalogueItem> items,bool timestamps)
    {
        if(!ImGui.BeginTable("StoreItems",timestamps?3:2,ImGuiTableFlags.RowBg|ImGuiTableFlags.SizingStretchProp))return;
        ImGui.TableSetupColumn("Item");ImGui.TableSetupColumn("Quantity",ImGuiTableColumnFlags.WidthFixed,TraderStyle.Px(100));
        if(timestamps)ImGui.TableSetupColumn("Oldest stock update",ImGuiTableColumnFlags.WidthFixed,TraderStyle.Px(165));
        ImGui.TableHeadersRow();
        foreach(var item in items){ImGui.TableNextRow();ImGui.TableNextColumn();ImGui.TextWrapped(item.Name+(item.HighQuality?"  HQ":""));ImGui.TableNextColumn();ImGui.TextColored(TraderStyle.Accent,$"{item.Quantity:N0}");if(timestamps){ImGui.TableNextColumn();ImGui.TextDisabled(item.OldestPublishedAt?.ToLocalTime().ToString("g")??"Unknown");}}
        ImGui.EndTable();
    }
    private void DrawAccess()
    {
        var service=plugin.Store;
        if(TraderStyle.BeginCard("ConnectStore"))
        {
            TraderStyle.Section("Connect a store", "Owner, publishing and friend invitation codes have separate permissions.");
            ImGui.SetNextItemWidth(-1);
            if(ImGui.InputTextWithHint("##storeCode","Paste a private access code…",ref code,4096,ImGuiInputTextFlags.Password))
            {try{parsed=StoreAccess.Parse(code);}catch{parsed=null;}}
            if(parsed!=null){ImGui.TextWrapped($"Service: {parsed.Url}");TraderStyle.Badge(parsed.Role.ToUpperInvariant()+" ACCESS",TraderStyle.Gold);}
            ImGui.BeginDisabled(service.Busy || parsed==null);
            if(TraderStyle.PrimaryButton("Connect",new(110,32))){var savedCode=code;code="";parsed=null;plugin.QueueAction(()=>service.Import(savedCode));}
            ImGui.EndDisabled();
            ImGui.TextWrapped("Access is stored with your operating system's credential encryption. Anyone with a friend code can read the catalogue until you revoke that code.");
        }TraderStyle.EndCard();
        if(service.Owner==null)return;
        if(TraderStyle.BeginCard("ManageStore"))
        {
            TraderStyle.Section("Owner controls",service.Owner.Url);
            ImGui.BeginDisabled(service.Busy);
            if(TraderStyle.SecondaryButton("Refresh access list",new(165,30)))plugin.QueueAction(service.RefreshManagement);
            ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##accessLabel","Label: e.g. Alex, or Second PC",ref accessLabel,80);
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(accessLabel));
            if(TraderStyle.PrimaryButton("Invite a friend",new(130,32))){var label=accessLabel;plugin.QueueAction(()=>service.CreateKey("reader",label,false));}
            ImGui.SameLine();if(TraderStyle.SecondaryButton("Authorize another PC",new(185,32))){var label=accessLabel;plugin.QueueAction(()=>service.CreateKey("publisher",label,false));}
            ImGui.EndDisabled();
            if(service.Publisher==null && TraderStyle.SecondaryButton("Authorize this PC",new(155,30)))plugin.QueueAction(()=>service.CreateKey("publisher","This PC",true));
            ImGui.EndDisabled();
            if(service.GeneratedCode.Length>0)
            {
                UiTone.Wrapped(TraderStyle.Gold,$"New {service.GeneratedRole} code — copy it now. It will not be displayed again after closing.");
                if(ImGui.Button("Copy new access code")){ImGui.SetClipboardText(service.GeneratedCode);notice="Private code copied. Share it only with the intended person or PC.";}
                ImGui.SameLine();if(ImGui.Button("Dismiss code"))service.DismissCode();
            }
            if(service.Management is {} management)
            {
                ImGui.Separator();
                foreach(var key in management.Keys)
                {
                    ImGui.TextUnformatted($"{key.Label} · {(key.Role=="reader"?"Read only":"PC publisher")}");ImGui.SameLine();
                    ImGui.BeginDisabled(service.Busy);if(ImGui.SmallButton($"Revoke…##{key.Id}"))revoke=key.Id;ImGui.EndDisabled();
                    if(revoke==key.Id)
                    {
                        UiTone.Wrapped(UiTone.Warning,key.Role=="publisher"?"Revoke this PC's access and remove all its published stock?":"Revoke this invitation? Future requests with this code will be rejected.");
                        if(ImGui.Button($"Confirm revoke##{key.Id}")){var id=key.Id;revoke=null;plugin.QueueAction(()=>service.Revoke(id));}ImGui.SameLine();if(ImGui.Button($"Cancel##{key.Id}"))revoke=null;
                    }
                }
            }
        }TraderStyle.EndCard();
    }
}
