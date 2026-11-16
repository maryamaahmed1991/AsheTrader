using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AsheTrader.Core.Store;

public sealed record StoreAccess(int Version, string Url, string Token, string Role)
{
    public const string Prefix = "ashe-store:v1:";
    public static StoreAccess Parse(string code)
    {
        try
        {
            if (code.Length > 4096 || !code.Trim().StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException();
            var access = JsonSerializer.Deserialize<StoreAccess>(Convert.FromBase64String(code.Trim()[Prefix.Length..])) ?? throw new FormatException();
            access.Validate(); return access;
        }
        catch { throw new FormatException("Invalid store access code. Copy the complete code from the store owner."); }
    }
    public void Validate()
    {
        if (Version != 1 || Role is not ("owner" or "publisher" or "reader") || Token.Length != 64
            || Token.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))
            || !Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.IsLoopback
            || uri.HostNameType != UriHostNameType.Dns || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/" || !uri.IsDefaultPort)
            throw new FormatException("Store access requires a valid HTTPS service and credential.");
    }
    public string Encode() { Validate(); return Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)); }
    public override string ToString() => $"Store access ({Role})"; // Never render the token in accidental logging.
}

public sealed class StorePreferences
{
    public HashSet<ulong> Characters { get; set; } = [];
    public HashSet<uint> Items { get; set; } = [];
    public bool AutoPublish { get; set; }
}
public sealed record StoreStockItem(uint ItemId, string Name, bool HighQuality, ulong Quantity);
public sealed record StoreOwnerStock(string Key, IReadOnlyList<StoreStockItem> Items);
public sealed record StoreSnapshot(long Revision, IReadOnlyList<StoreOwnerStock> Owners);
public sealed record StoreSourceState(long Revision, string Salt, DateTimeOffset? PublishedAt);
public sealed record StorePublishResult(long Revision, DateTimeOffset PublishedAt);
public sealed record StoreCatalogueItem(uint ItemId, string Name, bool HighQuality, ulong Quantity, DateTimeOffset? OldestPublishedAt);
public sealed record StoreCatalogueSource(DateTimeOffset? PublishedAt);
public sealed record StoreCatalogue(string Name, DateTimeOffset RetrievedAt, StoreCatalogueSource[] Sources, StoreCatalogueItem[] Items);
public sealed record StoreKey(string Id, string Role, string Label, string? SourceId, DateTimeOffset CreatedAt);
public sealed record StoreSource(string Id, string Label, long Revision, DateTimeOffset? PublishedAt);
public sealed record StoreManagement(string Name, string Salt, StoreKey[] Keys, StoreSource[] Sources);
public sealed record StoreCreatedKey(string Id, string Token, string Role);

public static class StorePrivacy
{
    // Shared store salt makes overlapping caches detectable, without uploading native IDs.
    public static string OwnerKey(string salt, ulong ownerId)
    {
        if (ownerId == 0 || salt.Length != 64 || salt.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Invalid inventory identity.");
        return Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromHexString(salt), Encoding.UTF8.GetBytes(ownerId.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }
}
