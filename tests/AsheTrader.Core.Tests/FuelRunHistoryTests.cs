using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelRunHistoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AsheTrader-history-" + Guid.NewGuid());
    private string FilePath => Path.Combine(directory, "history.json");
    private static FuelRunReceipt Receipt(Guid? id = null) => FuelRunHistory.Capture(id ?? Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow, false,
        [new() { CharacterId = 1, CharacterName = "Buyer", Purchased = 99, Deposited = 80, CreditsSpent = 9900, Status = FuelQueueEntryStatus.Completed }]);

    [Fact]
    public void ReceiptSurvivesRestartAndDuplicateArchivalDoesNotDoubleCount()
    {
        var receipt = Receipt(); var store = new FuelRunHistoryStore(FilePath);
        Assert.True(store.Record(receipt)); Assert.True(store.Record(receipt));
        var reopened = new FuelRunHistoryStore(FilePath);
        Assert.Single(reopened.Records); Assert.Equal(9900UL, reopened.Records[0].CreditsSpent);
        Assert.Equal(99UL, reopened.Records[0].Purchased);
    }

    [Fact]
    public void ReceiptIsDetachedFromMutableQueue()
    {
        var row = new FuelQueueEntry { CharacterName = "Original", Purchased = 10, CreditsSpent = 1000 };
        var receipt = FuelRunHistory.Capture(Guid.NewGuid(), null, DateTimeOffset.UtcNow, true, [row]);
        row.CharacterName = "Changed"; row.Purchased = 500;
        Assert.Equal("Original", receipt.Buyers[0].CharacterName); Assert.Equal(10UL, receipt.Purchased); Assert.True(receipt.EndedByUser);
    }

    [Fact]
    public void KeepsNewestHundredRunsAndUpsertsByRunId()
    {
        var records = Enumerable.Range(0, 110).Select(i => Receipt() with { FinishedAtUtc = DateTimeOffset.UnixEpoch.AddDays(i) }).ToArray();
        var latest = records[^1]; var retained = FuelRunHistory.Upsert(records, latest);
        Assert.Equal(100, retained.Count); Assert.Equal(latest.Id, retained[0].Id);
        Assert.Equal(100, retained.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void CorruptArchiveIsPreservedRatherThanOverwritten()
    {
        Directory.CreateDirectory(directory); File.WriteAllText(FilePath, "{bad-json");
        var store = new FuelRunHistoryStore(FilePath);
        Assert.False(store.Record(Receipt())); Assert.NotEmpty(store.Error);
        Assert.Equal("{bad-json", File.ReadAllText(FilePath));
    }

    [Fact]
    public void AnotherClientsArchiveCannotBeOverwritten()
    {
        var first = new FuelRunHistoryStore(FilePath); var second = new FuelRunHistoryStore(FilePath);
        var receipt = Receipt(); Assert.True(first.Record(receipt));
        Assert.False(second.Record(Receipt()));
        Assert.Equal(receipt.Id, Assert.Single(new FuelRunHistoryStore(FilePath).Records).Id);
    }

    [Fact]
    public void LegacyUnknownSpendingIsNotInventedFromSharedCreditChanges()
    {
        Assert.Null(FuelRunHistory.AddPurchaseCost(null, 1000, 99, 100));
        Assert.Equal(9900UL, FuelRunHistory.AddPurchaseCost(null, 0, 99, 100));
        Assert.Equal(19800UL, FuelRunHistory.AddPurchaseCost(9900, 99, 99, 100));
        var receipt = FuelRunHistory.Capture(Guid.NewGuid(), null, DateTimeOffset.UtcNow, false, [new() { Purchased = 1000 }]);
        Assert.Null(receipt.CreditsSpent); Assert.Contains("not recorded", FuelRunHistory.Format(receipt));
    }

    [Fact]
    public void ZeroPurchasesHaveKnownZeroCostAndReasonsRemainInReceipt()
    {
        var receipt = FuelRunHistory.Capture(Guid.NewGuid(), null, DateTimeOffset.UtcNow, false,
            [new() { Status = FuelQueueEntryStatus.Skipped, Note = "Housing quest incomplete" }]);
        Assert.Equal(0UL, receipt.CreditsSpent); Assert.Contains("Housing quest incomplete", FuelRunHistory.Format(receipt));
    }

    [Fact]
    public void ImportedLegacyFinishTimeIsExplicitlyUnknown()
    {
        var receipt = FuelRunHistory.Capture(Guid.NewGuid(), null, DateTimeOffset.UtcNow, false, [], true);
        Assert.Contains("original finish time unknown", FuelRunHistory.Format(receipt));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
