using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AsheTrader.Core.Store;

namespace AsheTrader.Core.Networking;

public sealed record DeviceInvitation(int Version, string Url, string Token, Guid HostPeerId, string HostName, string PairingKey, DateTimeOffset ExpiresAt)
{
    public const string Prefix = "ashe-pc:v1:";
    public string Encode() => Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));
    public static DeviceInvitation Parse(string code)
    {
        if (code.Length > 4096 || !code.Trim().StartsWith(Prefix, StringComparison.Ordinal)) throw new ArgumentException("Paste a trusted-PC invitation, not a store access code.");
        DeviceInvitation invite;
        try { invite = JsonSerializer.Deserialize<DeviceInvitation>(Convert.FromBase64String(code.Trim()[Prefix.Length..])) ?? throw new FormatException(); }
        catch (Exception ex) when (ex is FormatException or JsonException) { throw new ArgumentException("The trusted-PC invitation is invalid."); }
        try { invite.Validate(); }
        catch (FormatException) { throw new ArgumentException("The trusted-PC invitation has an invalid service address or key."); }
        return invite;
    }
    public void Validate()
    {
        new StoreAccess(1, Url, Token, "owner").Validate(); // Same HTTPS-root and secret validation; separate server permission domain.
        if (Version != 1 || HostPeerId == Guid.Empty || string.IsNullOrWhiteSpace(HostName) || HostName.Length > 80 || HostName.Any(char.IsControl))
            throw new ArgumentException("Invalid coordinator identity.");
        if (ExpiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("This invitation has expired. Create a new one on the coordinator.");
        if (Convert.FromBase64String(PairingKey).Length != 32) throw new ArgumentException("Invalid device pairing key.");
    }
    public override string ToString() => "Trusted-PC invitation (secret hidden)";
}

public sealed record DeviceCredential(string Url, string Token, Guid LinkId, bool Host)
{
    public override string ToString() => "Trusted device credential (secret hidden)";
}
public sealed record DeviceLinkCreated(Guid Id, string Token, string Invite, DateTimeOffset ExpiresAt);
public sealed record DeviceLinkJoined(Guid Id, string Token, Guid HostPeerId, string HostName);
public sealed record DeviceLinkState(Guid Id, Guid HostPeerId, string HostName, Guid? GuestPeerId, string? GuestName,
    string Address, int Port, DateTimeOffset? HostSeen, DateTimeOffset? GuestSeen);

public static class TrustedDevicePolicy
{
    public static bool IsPrivateAddress(string? value) => IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && IsPrivate(ip.GetAddressBytes());
    private static bool IsPrivate(byte[] b) => b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
    public static bool UsableEndpoint(DeviceLinkState state, Guid expectedHost, DateTimeOffset now) =>
        state.HostPeerId == expectedHost && state.Port is > 0 and <= 65535 && IsPrivateAddress(state.Address)
        && state.HostSeen is { } seen && seen <= now.AddMinutes(1) && seen >= now.AddMinutes(-2);
}

/// <summary>Rendezvous only: cannot publish stock, read friend catalogues or execute game operations.</summary>
public sealed class DeviceApi : IDisposable
{
    private readonly HttpClient http;
    public DeviceApi() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
    public DeviceApi(HttpMessageHandler handler) => http = new(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 32_000 };
    public Task<DeviceLinkCreated> Create(StoreAccess owner, Guid peerId, string name, CancellationToken ct) =>
        Send<DeviceLinkCreated>(owner.Url, owner.Token, HttpMethod.Post, "v1/device-links", new { peerId, name }, ct);
    public Task<DeviceLinkJoined> Join(DeviceInvitation invitation, Guid peerId, string name, CancellationToken ct) =>
        Send<DeviceLinkJoined>(invitation.Url, invitation.Token, HttpMethod.Post, "v1/device-links/redeem", new { peerId, name }, ct);
    public Task<DeviceLinkState> Poll(DeviceCredential access, string address, int port, CancellationToken ct) =>
        Send<DeviceLinkState>(access.Url, access.Token, HttpMethod.Put, "v1/device-link", new { address, port }, ct);
    public Task<JsonElement> Revoke(DeviceCredential access, CancellationToken ct) =>
        Send<JsonElement>(access.Url, access.Token, HttpMethod.Delete, "v1/device-link", null, ct);
    private async Task<T> Send<T>(string url, string token, HttpMethod method, string path, object? data, CancellationToken ct)
    {
        new StoreAccess(1, url, token, "owner").Validate();
        using var request = new HttpRequestMessage(method, new Uri(new Uri(url), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (data != null) request.Content = JsonContent.Create(data);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException((int)response.StatusCode switch
        {
            401 or 403 => "Trusted-PC access denied. The invitation may have expired or the link was revoked.",
            409 => "This invitation was already used, or the device limit was reached.",
            _ => $"Device discovery service unavailable (HTTP {(int)response.StatusCode}). Local-network discovery can still reconnect saved pairs."
        }, null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false) ?? throw new InvalidDataException("Empty device service response.");
    }
    public void Dispose() => http.Dispose();
}
