using AsheTrader.Core;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using Newtonsoft.Json;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class MultiItemTradeEvidenceTests
{
    private static readonly ItemKey Fuel = new(10155, false);
    private static readonly ItemKey Material = new(1001, true);

    private static WorkerCommand Command(bool receive = false)
    {
        var id = Guid.NewGuid();
        return new()
        {
            Id = id, JobId = Guid.NewGuid(), Kind = receive ? WorkerCommandKind.ReceiveBatch : WorkerCommandKind.SendBatch,
            Operation = new() { Id = id, Kind = GameOperationKind.Inspect, CharacterId = 7, ItemId = Fuel.ItemId, Quantity = 30 },
            TradeItems = [new() { ItemId = Fuel.ItemId, Quantity = 30 }, new() { ItemId = Material.ItemId, HighQuality = true, Quantity = 17 }],
        };
    }

    private static List<TradeItemObservation> Baselines() =>
    [new() { ItemId = Fuel.ItemId, BeforeQuantity = 100 }, new() { ItemId = Material.ItemId, HighQuality = true, BeforeQuantity = 200 }];

    private static Dictionary<ItemKey, uint> Current(uint fuel = 70, uint material = 183) => new() { [Fuel] = fuel, [Material] = material };

    private static WorkerRecord TalliedRecord(bool transferred = true)
    {
        var record = new WorkerRecord { Command = Command(), TradeStarted = true, TradeBaselines = Baselines(), TradeTallyRound = Guid.NewGuid() };
        record.Response = MultiItemTradeEvidence.Capture(record.Command, null, record.TradeBaselines, transferred ? Current() : Current(100, 200));
        record.TradeTallyItems = Clone(record.Response.TradeItems);
        return record;
    }

    private static AutomaticTradeResolution Resolution(WorkerRecord record, bool transferred = true)
        => new(record.Command.Id, record.TradeTallyRound!.Value, transferred, null, null) { ExpectedItems = Clone(record.TradeTallyItems) };

    private static T Clone<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value))!;

    [Theory]
    [InlineData(false, 70, 183)]
    [InlineData(true, 130, 217)]
    public void EveryItemAndQualityMustHaveItsExactDirectionalDelta(bool receive, uint fuel, uint material)
    {
        var command = Command(receive);
        var response = MultiItemTradeEvidence.Capture(command, null, Baselines(), Current(fuel, material));
        Assert.Equal(TradeTallyOutcome.Transferred, TradeReconciliationEvidence.Classify(command, response));
        Assert.Equal(47U, response.Quantity);
        Assert.Null(response.BeforeQuantity);
        Assert.Null(response.CurrentQuantity);
        Assert.True(response.Ambiguous);
        Assert.Equal(2, response.TradeItems.Count);
    }

    [Fact]
    public void EqualGrandTotalCannotHideWrongItemAmountsOrAPartialAtomicTrade()
    {
        var command = Command();
        var swapped = MultiItemTradeEvidence.Capture(command, null, Baselines(), Current(80, 173));
        var partial = MultiItemTradeEvidence.Capture(command, null, Baselines(), Current(70, 200));
        Assert.Equal(TradeTallyOutcome.Conflicting, MultiItemTradeEvidence.Classify(command, swapped));
        Assert.Equal(TradeTallyOutcome.Conflicting, MultiItemTradeEvidence.Classify(command, partial));
    }

    [Fact]
    public void UnchangedRequiresAllItemsToStayUnchanged()
    {
        var record = TalliedRecord(false);
        var result = MultiItemTradeEvidence.Settle(record, Resolution(record, false), Current(100, 200));
        Assert.True(result.AutoReconciled);
        Assert.True(result.Unchanged);
        Assert.False(result.Success);
        Assert.False(result.Ambiguous);
        Assert.Equal(0U, result.Quantity);
        Assert.All(result.TradeItems, item => Assert.Equal(0U, item.Quantity));
    }

    [Fact]
    public void SuccessfulSettlementIsDurableAndReplayedWithoutReadingNewInventory()
    {
        var record = TalliedRecord();
        var resolution = Resolution(record);
        record.Response = MultiItemTradeEvidence.Settle(record, resolution, Current());
        Assert.True(record.Response.Success);
        Assert.True(record.Response.AutoReconciled);
        Assert.False(record.Response.Ambiguous);
        Assert.Same(record.Response, MultiItemTradeEvidence.Settle(record, resolution, new Dictionary<ItemKey, uint>()));
        Assert.Throws<InvalidOperationException>(() => MultiItemTradeEvidence.Settle(record, Resolution(record, false), Current()));
    }

    [Fact]
    public void AChangedItemBetweenTallyAndCommitCannotBeAcknowledged()
    {
        var record = TalliedRecord();
        Assert.Throws<InvalidOperationException>(() => MultiItemTradeEvidence.Settle(record, Resolution(record), Current(69, 183)));
        Assert.True(record.Response!.Ambiguous);
        Assert.False(record.Response.AutoReconciled);
    }

    [Fact]
    public void StaleRoundAndChangedExpectedVectorAreRejected()
    {
        var record = TalliedRecord();
        var stale = Resolution(record) with { RoundId = Guid.NewGuid() };
        Assert.Throws<InvalidOperationException>(() => MultiItemTradeEvidence.Settle(record, stale, Current()));
        var changed = Resolution(record);
        changed.ExpectedItems[1].Quantity++;
        Assert.Throws<InvalidOperationException>(() => MultiItemTradeEvidence.Settle(record, changed, Current()));
    }

    [Fact]
    public void MissingExtraDuplicateAndWrongQualityEvidenceCannotCommit()
    {
        var record = TalliedRecord();
        var missing = Clone(record.Response!); missing.TradeItems.RemoveAt(1);
        var extra = Clone(record.Response!); extra.TradeItems.Add(new() { ItemId = 42, BeforeQuantity = 10, CurrentQuantity = 10 });
        var duplicate = Clone(record.Response!); duplicate.TradeItems.Add(Clone(duplicate.TradeItems[0]));
        var quality = Clone(record.Response!); quality.TradeItems[1].HighQuality = false;
        foreach (var response in new[] { missing, extra, duplicate, quality })
            Assert.Equal(TradeTallyOutcome.Conflicting, MultiItemTradeEvidence.Classify(record.Command, response));
        Assert.Throws<InvalidDataException>(() => MultiItemTradeEvidence.Capture(record.Command, null, Baselines().Take(1).ToArray(), Current()));
    }

    [Fact]
    public void ScalarSuccessCannotStandInForAMixedItemSuccess()
    {
        var command = Command();
        Assert.Equal(TradeTallyOutcome.Missing, TradeReconciliationEvidence.Classify(command,
            new() { CommandId = command.Id, Success = true, Quantity = 47 }));
        Assert.Throws<InvalidOperationException>(() => TradeReconciliationEvidence.Capture(command, null, 300, 253));
    }

    [Fact]
    public void NeverStartedCanResolveWithoutInventingBaselines()
    {
        var command = Command();
        Assert.Equal(TradeTallyOutcome.Unchanged, MultiItemTradeEvidence.Classify(command,
            new() { CommandId = command.Id, NeverStarted = true, Unchanged = true }));
    }

    [Fact]
    public void CommandValidationRejectsDuplicateKeysZeroesAndOverflow()
    {
        var command = Command();
        command.TradeItems.Add(new() { ItemId = Fuel.ItemId, Quantity = 1 });
        Assert.Throws<InvalidDataException>(() => MultiItemTradeEvidence.Lines(command));
        command.TradeItems.RemoveAt(2);
        command.TradeItems[0].Quantity = 0;
        Assert.Throws<InvalidDataException>(() => MultiItemTradeEvidence.Lines(command));
        command.TradeItems[0].Quantity = uint.MaxValue;
        Assert.Throws<OverflowException>(() => MultiItemTradeEvidence.Lines(command));
    }

    [Fact]
    public void EmptyTradeItemsPreserveLegacyCommandFingerprintSerialization()
    {
        var command = Command();
        command.TradeItems.Clear();
        var fingerprint = JsonConvert.SerializeObject(command);
        Assert.DoesNotContain("TradeItems", fingerprint);
        Assert.Equal(fingerprint, JsonConvert.SerializeObject(Clone(command)));
        Assert.Single(MultiItemTradeEvidence.Lines(command));
        command.TradeItems.Add(new() { ItemId = Fuel.ItemId, Quantity = 30 });
        Assert.Contains("TradeItems", JsonConvert.SerializeObject(command));
    }
}
