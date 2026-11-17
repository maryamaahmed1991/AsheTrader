using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace AsheTrader.Windows;

internal enum MascotPose { Welcome, Parcel, Coin, Fuel, Cozy }

/// <summary>Local, lazily loaded art. Shared wrappers are borrowed only for the current UI frame.</summary>
internal sealed class MascotArt
{
    private readonly Dictionary<MascotPose, ISharedImmediateTexture> textures = [];
    private readonly HashSet<MascotPose> unavailable = [];

    internal ImTextureID? Get(MascotPose pose)
    {
        if (unavailable.Contains(pose)) return null;
        try
        {
            if (!textures.TryGetValue(pose, out var texture))
            {
                var path = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "Assets", "Mascot", $"{pose}.png");
                if (!File.Exists(path)) { unavailable.Add(pose); return null; }
                textures[pose] = texture = Plugin.TextureProvider.GetFromFile(path);
            }
            if (texture.TryGetWrap(out var wrap, out var error)) return wrap.Handle;
            if (error != null) unavailable.Add(pose);
        }
        catch (Exception ex)
        {
            unavailable.Add(pose);
            Plugin.Log.Warning(ex, "Could not load optional mascot artwork {Pose}", pose);
        }
        return null;
    }
}
