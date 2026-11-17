using AsheTrader.Core.Networking;
using Dalamud.Bindings.ImGui;

namespace AsheTrader.Windows;

public sealed partial class WorkflowPanel
{
    private string deviceCode = "";
    private bool confirmForgetDevice;
    private void DrawTrustedDevices()
    {
        if (TraderStyle.BeginCard("TrustedDevices"))
        {
            TraderStyle.Section("Trusted PCs", "Set up once. Reconnect automatically, search saved inventory offline, and run jobs when both PCs are ready.");
            if (Service.HasTrustedPeer || Service.HasHostedDeviceLink)
            {
                var automatic = Settings.AutoConnect;
                if (ImGui.Checkbox("Connect automatically when the plugin loads", ref automatic))
                { Settings.AutoConnect = automatic; plugin.Save(); if (automatic) plugin.QueueAction(Service.ReconnectTrustedPc); else plugin.QueueAction(Service.Disconnect); }
                var resume = Settings.ResumeAfterReconnect;
                if (ImGui.Checkbox("Recover a running transfer after a brief disconnect", ref resume))
                { Settings.ResumeAfterReconnect = resume; plugin.Save(); }
                ImGui.TextDisabled("Reloading the plugin always leaves saved jobs paused.");
                if (!Service.Connected && TraderStyle.SecondaryButton("Reconnect now")) plugin.QueueAction(Service.ReconnectTrustedPc);
                if (Service.Connected && TraderStyle.SecondaryButton("Disconnect for this session")) plugin.QueueAction(Service.Disconnect);
                ImGui.TextWrapped(Service.InventorySyncStatus);
                if (Service.RemoteInventorySyncedAt is { } synced) ImGui.TextDisabled($"Other PC's cache retrieved: {synced.ToLocalTime():g} · game observation time may be older");
                ImGui.BeginDisabled(!Service.Connected);
                if (TraderStyle.SecondaryButton("Refresh private inventory")) plugin.QueueAction(Service.RefreshPrivateInventory);
                ImGui.EndDisabled();
                ImGui.BeginDisabled(LocalActionRunning || HasUnfinishedJob || Service.HasQueuedTransfer || Service.DeviceSetupBusy);
                if (TraderStyle.SecondaryButton("Forget this device link")) confirmForgetDevice = true;
                if (confirmForgetDevice)
                {
                    ImGui.TextWrapped("Remove this pairing and its saved private inventory? Hosted links will be revoked for both PCs. Friend store access is separate.");
                    if (ImGui.Button("Forget device")) { confirmForgetDevice = false; plugin.QueueAction(Service.ForgetTrustedPc); }
                    ImGui.SameLine(); if (ImGui.Button("Keep device")) confirmForgetDevice = false;
                }
                ImGui.EndDisabled();
            }
            else
            {
                ImGui.BeginDisabled(LocalActionRunning || HasUnfinishedJob || Service.DeviceSetupBusy);
                var name = Settings.PeerName;
                if (ImGui.InputText("This PC's name", ref name, 80)) { Settings.PeerName = name; plugin.Save(); }
                ImGui.BeginDisabled(plugin.Store.Owner == null);
                if (TraderStyle.PrimaryButton("Add another PC")) plugin.QueueAction(Service.CreateDeviceInvitation);
                ImGui.EndDisabled();
                if (plugin.Store.Owner == null) ImGui.TextDisabled("Create the invitation on the PC with store owner access, or use manual LAN pairing below.");
                ImGui.InputText("Trusted-PC invitation", ref deviceCode, 4096, ImGuiInputTextFlags.Password);
                DeviceInvitation? invite = null;
                if (deviceCode.Length > 0)
                {
                    try { invite = DeviceInvitation.Parse(deviceCode); ImGui.TextWrapped($"Coordinator: {invite.HostName} · service: {new Uri(invite.Url).Host}"); }
                    catch (Exception ex) { UiTone.Wrapped(UiTone.Warning, ex.Message); }
                }
                ImGui.BeginDisabled(invite == null);
                if (TraderStyle.PrimaryButton("Trust and connect")) { var code = deviceCode; deviceCode = ""; plugin.QueueAction(() => Service.JoinDevice(code)); }
                ImGui.EndDisabled(); ImGui.EndDisabled();
            }
            if (Service.DeviceInvitationCode.Length > 0)
            {
                if (TraderStyle.PrimaryButton("Copy one-time PC invitation")) ImGui.SetClipboardText(Service.DeviceInvitationCode);
                ImGui.SameLine(); if (ImGui.Button("Hide code")) plugin.QueueAction(Service.DismissDeviceInvitation);
            }
            if (Service.DeviceSetupStatus.Length > 0) UiTone.Status(Service.DeviceSetupStatus);
            ImGui.TextWrapped("Private inventories sync directly between trusted PCs. Friends only see your published store selection. Live trades require a reachable local-network connection.");
        }
        TraderStyle.EndCard();
    }
}
