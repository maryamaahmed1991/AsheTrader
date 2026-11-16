using System.Text.Json;

namespace AsheTrader.Core;

/// <summary>Separate receipts never enlarge the hot purchase journal. Failed writes preserve its prior archive.</summary>
public sealed class FuelRunHistoryStore
{
    private readonly SettingsFileStore file;
    private bool readable = true;
    public IReadOnlyList<FuelRunReceipt> Records { get; private set; } = [];
    public string Error { get; private set; } = "";

    public FuelRunHistoryStore(string path)
    {
        file = new(path);
        try
        {
            var json = file.Load();
            Records = json == null ? [] : JsonSerializer.Deserialize<List<FuelRunReceipt>>(json)
                ?? throw new InvalidDataException("The history file did not contain a receipt list.");
            if (Records.Any(r => r == null || r.Id == Guid.Empty || r.Buyers == null || r.Buyers.Any(b => b == null)))
                throw new InvalidDataException("The history file contained an invalid receipt.");
        }
        catch (Exception ex) { readable = false; Error = "Fuel history could not be loaded: " + ex.Message; Records = []; }
    }

    public bool Record(FuelRunReceipt receipt)
    {
        if (!readable) return false;
        if (Records.Any(r => r.Id == receipt.Id)) return true; // A finished run is immutable, including across reloads.
        var next = FuelRunHistory.Upsert(Records, receipt);
        if (!file.TrySave(JsonSerializer.Serialize(next), out var error))
        { Error = "Fuel history could not be saved: " + error; return false; }
        Records = next; Error = ""; return true;
    }
}
