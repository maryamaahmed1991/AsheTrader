using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AsheTrader.Integrations;

/// <summary>A detached, read-only copy of the AutoRetainer fields used by Ashe's Trader.</summary>
internal sealed record AutoRetainerCharacterSnapshot(
    ulong ContentId,
    string Name,
    string World,
    int ServiceAccount,
    uint Gil,
    uint InventorySpace,
    int Ceruleum);

/// <summary>Unknown state values stay null so callers cannot mistake missing IPC for an idle plugin.</summary>
internal sealed record AutoRetainerStatus(bool Ready, bool? Busy, bool? MultiModeEnabled, string? Error);

/// <summary>
/// Optional AutoRetainer integration. Call from a framework update or through IFramework.RunOnFrameworkThread.
/// The adapter never changes AutoRetainer configuration and never retains its mutable objects.
/// </summary>
internal sealed class AutoRetainerClient(
    IDalamudPluginInterface pluginInterface,
    IFramework framework,
    IPluginLog log)
{
    public AutoRetainerStatus GetStatus()
    {
        if (!CanCall(out var error))
            return new(false, null, null, error);

        try
        {
            pluginInterface.GetIpcSubscriber<object>("AutoRetainer.Init").InvokeAction();
        }
        catch (Exception)
        {
            return new(false, null, null, "AutoRetainer is unavailable or its IPC is not ready.");
        }

        bool? busy = null;
        bool? multiMode = null;
        var unavailable = new List<string>();
        try
        {
            busy = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc();
        }
        catch (Exception)
        {
            unavailable.Add("busy state");
        }

        try
        {
            multiMode = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled").InvokeFunc();
        }
        catch (Exception)
        {
            unavailable.Add("multi-mode state");
        }

        return new(true, busy, multiMode, unavailable.Count == 0
            ? null
            : $"AutoRetainer is available, but its {string.Join(" and ", unavailable)} IPC is unavailable.");
    }

    public bool TryGetCharacters(out IReadOnlyList<AutoRetainerCharacterSnapshot> characters, out string error)
    {
        characters = Array.Empty<AutoRetainerCharacterSnapshot>();
        if (!CanCall(out error))
            return false;

        try
        {
            pluginInterface.GetIpcSubscriber<object>("AutoRetainer.Init").InvokeAction();
            var contentIds = pluginInterface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs").InvokeFunc();
            if (contentIds is null)
            {
                error = "AutoRetainer returned no character collection. Its IPC may be incompatible.";
                return false;
            }

            var imported = new List<AutoRetainerCharacterSnapshot>(contentIds.Count);
            foreach (var contentId in contentIds.Distinct())
            {
                if (contentId == 0)
                    continue;

                // Dalamud CallGateChannel.InvokeFunc converts a different return type through JSON.
                // A small DTO therefore produces a detached copy without depending on AutoRetainerAPI
                // or ECommons. Requesting object instead would produce a JObject, not an AR instance.
                // Upstream: goatcorp/Dalamud, Dalamud/Plugin/Ipc/Internal/CallGateChannel.cs.
                var data = pluginInterface.GetIpcSubscriber<ulong, CharacterWireData?>(
                    "AutoRetainer.GetOfflineCharacterData").InvokeFunc(contentId);

                // A record can disappear between roster discovery and reading its data.
                if (data is null)
                    continue;

                if (data.CID != contentId || string.IsNullOrWhiteSpace(data.Name)
                    || string.IsNullOrWhiteSpace(data.World) || data.ServiceAccount is null
                    || data.Gil is null || data.InventorySpace is null || data.Ceruleum is null)
                {
                    error = "AutoRetainer returned an incomplete or incompatible character record. No characters were imported.";
                    return false;
                }

                imported.Add(new(
                    contentId, data.Name, data.World, data.ServiceAccount.Value,
                    data.Gil.Value, data.InventorySpace.Value, data.Ceruleum.Value));
            }

            characters = imported.AsReadOnly();
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            log.Debug(exception, "AutoRetainer character import failed.");
            error = "Could not read AutoRetainer characters. Check that AutoRetainer is loaded and compatible; details are in the plugin log.";
            return false;
        }
    }

    /// <summary>Requests a relog only when AutoRetainer reports idle and its own multi-mode is disabled.</summary>
    public bool TryRelog(string nameAtWorld, out string error)
    {
        if (!CanCall(out error))
            return false;

        if (string.IsNullOrWhiteSpace(nameAtWorld))
        {
            error = "Select a character before requesting a relog.";
            return false;
        }

        var status = GetStatus();
        if (!status.Ready || status.Busy is null || status.MultiModeEnabled is null)
        {
            error = status.Error ?? "AutoRetainer's state could not be verified.";
            return false;
        }

        if (status.Busy.Value || status.MultiModeEnabled.Value)
        {
            error = "Wait for AutoRetainer to finish and disable its multi-mode before starting an Ashe's Trader relog.";
            return false;
        }

        if (!TryGetCharacters(out var characters, out error))
            return false;

        var requested = nameAtWorld.Trim();
        var character = characters.FirstOrDefault(candidate =>
            string.Equals($"{candidate.Name}@{candidate.World}", requested, StringComparison.OrdinalIgnoreCase));
        if (character is null)
        {
            error = "The selected character is not in AutoRetainer's registered character list.";
            return false;
        }

        try
        {
            if (!pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.CanAutoLogin").InvokeFunc())
            {
                error = "AutoRetainer cannot relog from the current game state.";
                return false;
            }

            // Preserve AR's exact spelling: its provider performs a case-sensitive roster lookup.
            var accepted = pluginInterface.GetIpcSubscriber<string, bool>("AutoRetainer.PluginState.Relog")
                .InvokeFunc($"{character.Name}@{character.World}");
            error = accepted ? string.Empty : "AutoRetainer did not accept the relog request.";
            return accepted;
        }
        catch (Exception exception)
        {
            log.Debug(exception, "AutoRetainer relog request failed.");
            error = "AutoRetainer's relog IPC failed. Check its current state and the plugin log.";
            return false;
        }
    }

    private bool CanCall(out string error)
    {
        if (framework.IsFrameworkUnloading)
        {
            error = "The game framework is unloading.";
            return false;
        }

        if (!framework.IsInFrameworkUpdateThread)
        {
            error = "AutoRetainer operations must run on the game framework thread.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    // Names and nullable types are deliberate: unknown fields from AR are ignored, and missing
    // required fields stay null instead of silently becoming believable zero-value snapshots.
    private sealed class CharacterWireData
    {
        public ulong? CID { get; set; }
        public string? Name { get; set; }
        public string? World { get; set; }
        public int? ServiceAccount { get; set; }
        public uint? Gil { get; set; }
        public uint? InventorySpace { get; set; }
        public int? Ceruleum { get; set; }
    }
}
