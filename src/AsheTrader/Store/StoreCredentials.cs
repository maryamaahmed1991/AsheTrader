using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AsheTrader.Core;
using AsheTrader.Core.Store;

namespace AsheTrader.Store;

internal sealed class StoreCredentials
{
    public StoreAccess? Owner { get; set; }
    public StoreAccess? Publisher { get; set; }
    public List<StoreAccess> Readers { get; set; } = [];
}

/// <summary>Current-user Windows DPAPI (also provided by Wine), separate from ordinary settings/backups.</summary>
internal sealed class StoreCredentialFile(string path)
{
    private readonly SettingsFileStore file = new(path);
    public StoreCredentials Load()
        => LoadValue<StoreCredentials>() ?? new();
    public T? LoadValue<T>()
    {
        var encoded = file.Load();
        if (encoded == null) return default;
        var plain = Transform(Convert.FromBase64String(encoded), false);
        try { return JsonSerializer.Deserialize<T>(plain); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }
    public void Save(StoreCredentials credentials)
        => SaveValue(credentials);
    public void SaveValue<T>(T credentials)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            if (!file.TrySave(Convert.ToBase64String(Transform(plain, true)), out _))
                throw new IOException("Could not save store credentials. Another client may have changed them; reload the plugin.");
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }
    private static byte[] Transform(byte[] input, bool encrypt)
    {
        var blob = new Blob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(input, 0, blob.Data, input.Length);
            var ok = encrypt ? CryptProtectData(ref blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new IOException("The operating system could not protect or unlock store credentials.");
            var bytes = new byte[output.Length]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return bytes;
        }
        finally
        {
            for (var i=0;i<blob.Length;i++) Marshal.WriteByte(blob.Data,i,0);
            Marshal.FreeHGlobal(blob.Data);
            if (output.Data != IntPtr.Zero) { for(var i=0;i<output.Length;i++) Marshal.WriteByte(output.Data,i,0); LocalFree(output.Data); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
