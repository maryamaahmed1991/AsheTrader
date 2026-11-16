using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AsheTrader.Core.Store;

/// <summary>HTTPS only; credentials stay in headers, redirects are never followed.</summary>
public sealed class StoreApi : IDisposable
{
    private readonly HttpClient http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public StoreApi() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
    public StoreApi(HttpMessageHandler handler) => http = new(handler) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 8_000_000 };
    public async Task<StoreCatalogue> Browse(StoreAccess access, CancellationToken ct)
    {
        var result=await Send<StoreCatalogue>(access, HttpMethod.Get, "v1/catalogue", null, ct).ConfigureAwait(false);
        static bool Label(string? text)=>!string.IsNullOrWhiteSpace(text) && text.Length<=80 && !text.Any(char.IsControl);
        if(!Label(result.Name) || result.Items==null || result.Sources==null || result.Items.Length>40_000 || result.Sources.Length>8
            || result.Items.Any(x=>x==null || x.ItemId==0 || !Label(x.Name) || x.Quantity>40_000_000_000_000UL)
            || result.Sources.Any(x=>x==null)) throw new InvalidDataException("The catalogue returned invalid stock data.");
        return result;
    }
    public Task<StoreManagement> Manage(StoreAccess access, CancellationToken ct) => Send<StoreManagement>(access, HttpMethod.Get, "v1/manage", null, ct);
    public Task<StoreSourceState> Source(StoreAccess access, CancellationToken ct) => Send<StoreSourceState>(access, HttpMethod.Get, "v1/source", null, ct);
    public Task<StoreCreatedKey> CreateKey(StoreAccess access, string role, string label, CancellationToken ct) => Send<StoreCreatedKey>(access, HttpMethod.Post, "v1/keys", new {role,label}, ct);
    public Task<JsonElement> Revoke(StoreAccess access, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var key)) throw new ArgumentException("Invalid invitation.");
        return Send<JsonElement>(access, HttpMethod.Delete, "v1/keys/" + key.ToString("D"), null, ct);
    }
    public Task<StorePublishResult> Publish(StoreAccess access, StoreSnapshot snapshot, CancellationToken ct) => Send<StorePublishResult>(access, HttpMethod.Put, "v1/source", snapshot, ct);
    public Task<StorePublishResult> Clear(StoreAccess access, long revision, CancellationToken ct) => Send<StorePublishResult>(access, HttpMethod.Delete, "v1/source", new {revision}, ct);
    private async Task<T> Send<T>(StoreAccess access, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        access.Validate();
        using var request = new HttpRequestMessage(method, new Uri(new Uri(access.Url), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        if (body != null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException((int)response.StatusCode switch
            {
                401 or 403 => "Store access was denied. The invitation may have been revoked or has the wrong role.",
                409 => "Stock changed or another PC already publishes these inventories. Refresh and use separate character selections on each PC.",
                400 or 413 => "The catalogue rejected this snapshot. Check item selections and refresh the preview.",
                429 => "The catalogue is busy. Wait a minute before trying again.",
                _ => $"Catalogue unavailable (HTTP {(int)response.StatusCode}). Your last published stock is retained."
            });
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false) ?? throw new InvalidDataException("The catalogue returned an empty response.");
    }
    public void Dispose() => http.Dispose();
}
