using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class MultiItemCoordinatorTests
{
    private static SimulatedCharacter Sim(ulong id, Guid peer, uint bags = 0, uint stored = 0, uint capacity = 999) => new()
    {
        Id = id, PeerId = peer, Bags = bags, BagLimit = capacity,
        Retainers = new() { [id + 1000] = stored }
    };

    private static SavedTransferJob Saved(params TransferJobDefinition[] items) => new()
    {
        Definition = items[0], AdditionalItems = items.Skip(1).ToList(),
        Meeting = new() { WorldId = 10, TerritoryId = 20, AetheryteId = 30 }
    };

    private static TransferJobDefinition Other(uint itemId, ulong quantity, bool highQuality = false)
    {
        var item = Job([1], (2, quantity));
        item.ItemId = itemId; item.HighQuality = highQuality;
        return item;
    }

    private static ulong Delivered(CoordinatorItemResult item, ulong recipient = 2) => new TransferJobLedger(item.Journal!).DeliveredTo(recipient);

    [Fact]
    public void MultipleItemsKeepQuantitiesQualityRulesAndRetainerPickupCountersSeparate()
    {
        var provider = Sim(1, PeerA, 3, 17);
        var recipient = Sim(2, PeerB);
        provider.Inventory(200, true).Bags = 2;
        provider.Inventory(200, true).Retainers[1001] = 8;
        provider.Inventory(200).Bags = 50;
        recipient.Inventory(200, true).Bags = 1;
        var second = Other(200, 7, true);
        second.QuantityMode = JobQuantityMode.RestockToTotal;
        var service = new WorkflowService(PeerA, PeerB, provider, recipient);
        service.StartJob(Saved(Job([1], (2, 10)), second));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(2, service.Run.CompletedItems.Count);
        Assert.Equal(1, service.Run.ActiveItemIndex);
        Assert.Equal(10155U, service.Run.Job.Definition.ItemId);
        Assert.Equal(200U, service.Run.ActiveDefinition.ItemId);
        Assert.Equal(10UL, Delivered(service.Run.CompletedItems[0]));
        Assert.Equal(6UL, Delivered(service.Run.CompletedItems[1]));
        Assert.Equal(7UL, service.Run.CompletedItems[0].Withdrawn[1]);
        Assert.Equal(4UL, service.Run.CompletedItems[1].Withdrawn[1]);
        Assert.Equal(10U, recipient.Bags);
        Assert.Equal(7U, recipient.Inventory(200, true).Bags);
        Assert.Equal(50U, provider.Inventory(200).Bags);
        Assert.Equal(10U, provider.RetainerTotal);
        Assert.Equal(4U, provider.Inventory(200, true).RetainerTotal);
        Assert.Equal(new uint[] { 10155, 10155, 200, 200 }, service.Trades.Select(trade => trade.ItemId));
        Assert.All(service.Trades.Where(trade => trade.ItemId == 200), trade => Assert.True(trade.HighQuality));
    }

    [Fact]
    public void InventoryPreviewReadsAllItemCachesWithoutLoggingInOrMovingAnything()
    {
        var provider = Sim(1, PeerA, 3, 17);
        provider.Inventory(200).Retainers[1001] = 6;
        var service = new WorkflowService(PeerA, PeerB, provider, Sim(2, PeerB));
        service.ScanJob(Saved(Job([1], (2, 10)), Other(200, 6)));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(2, service.Run.CompletedItems.Count);
        Assert.All(service.Run.CompletedItems, item => { Assert.True(item.DiscoveryOnly); Assert.Null(item.Journal); Assert.Equal(2, item.Snapshots.Count); });
        Assert.Equal(20UL, service.Run.CompletedItems[0].Snapshots[1].TotalQuantity);
        Assert.Equal(6UL, service.Run.CompletedItems[1].Snapshots[1].TotalQuantity);
        Assert.Equal(4, service.Trace.Count);
        Assert.All(service.Trace, command => Assert.Equal(GameOperationKind.ReadCachedInventory, command.Operation));
        Assert.Empty(service.Trades);
        Assert.Equal(20UL, provider.Total);
        Assert.Equal(6UL, provider.Inventory(200).Total);
    }

    [Fact]
    public void RestartAtItemBoundaryNeverReplaysCompletedItemOrCopiesItsPickupCounters()
    {
        var provider = Sim(1, PeerA, 3, 17);
        provider.Inventory(200).Retainers[1001] = 6;
        var service = new WorkflowService(PeerA, PeerB, provider, Sim(2, PeerB));
        service.StartJob(Saved(Job([1], (2, 10)), Other(200, 6)));
        service.RunUntil(run => run.ActiveItemIndex == 1);

        Assert.Single(service.Run!.CompletedItems);
        Assert.Empty(service.Run.Withdrawn);
        Assert.Empty(service.Run.Deposited);
        Assert.Empty(service.Run.Pending);
        Assert.Null(service.Run.Journal);
        Assert.Equal(7UL, service.Run.CompletedItems[0].Withdrawn[1]);
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(10U, service.Trades.Where(trade => trade.ItemId == 10155).Aggregate(0U, (sum, trade) => sum + trade.Quantity));
        Assert.Equal(6U, service.Trades.Where(trade => trade.ItemId == 200).Aggregate(0U, (sum, trade) => sum + trade.Quantity));
        Assert.Equal(new[] { 0, 1 }, service.Run.CompletedItems.Select(item => item.ItemIndex));
        Assert.Equal(6UL, service.Run.Withdrawn[1]);
        Assert.Equal(7UL, service.Run.CompletedItems[0].Withdrawn[1]);
    }

    [Fact]
    public void LostSecondItemPickupRecoversItsOriginalIdentityAndDoesNotWithdrawAgain()
    {
        var provider = Sim(1, PeerA, 10);
        provider.Inventory(200).Retainers[1001] = 20;
        var service = new WorkflowService(PeerA, PeerB, provider, Sim(2, PeerB));
        service.SuppressInitialResponse = command => command.Operation.ItemId == 200 && command.Operation.Kind == GameOperationKind.Withdraw;
        service.StartJob(Saved(Job([1], (2, 10)), Other(200, 6)));
        service.RunUntil(run => run.ActiveItemIndex == 1 && run.Pending.Any(pending => pending.Command.Operation.Kind == GameOperationKind.Withdraw));
        var commandId = Assert.Single(service.Run!.Pending, pending => pending.Command.Operation.Kind == GameOperationKind.Withdraw).Command.Id;
        Assert.Equal(6U, provider.Inventory(200).Bags);
        service.SimulateCoordinatorRestart();
        service.Resume();
        var recoveredPickup = Assert.Single(service.Run.Pending, pending => pending.Command.Operation.Kind == GameOperationKind.Withdraw);
        Assert.Equal(commandId, recoveredPickup.Command.Id);
        Assert.Equal(200U, recoveredPickup.Command.Operation.ItemId);
        service.SuppressInitialResponse = null;
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Single(service.Trace, command => command.Operation == GameOperationKind.Withdraw && command.ItemId == 200);
        Assert.Equal(14U, provider.Inventory(200).RetainerTotal);
        Assert.Equal(10UL, Delivered(service.Run.CompletedItems[0]));
        Assert.Equal(6UL, Delivered(service.Run.CompletedItems[1]));
    }

    [Fact]
    public void LostSecondItemTradeResultRecoversWithoutTradingEitherItemTwice()
    {
        var provider = Sim(1, PeerA, 10);
        provider.Inventory(200, true).Bags = 6;
        var service = new WorkflowService(PeerA, PeerB, provider, Sim(2, PeerB));
        service.SuppressInitialResponse = command => command.Operation.ItemId == 200 && command.Kind == WorkerCommandKind.SendBatch;
        service.StartJob(Saved(Job([1], (2, 10)), Other(200, 6, true)));
        service.RunUntil(run => run.ActiveItemIndex == 1 && run.Step == CoordinatorStep.AwaitTrade && run.Pending.Any(command => command.Response == null));
        Assert.Equal(2, service.Trades.Count);
        var tradeId = service.Run!.TradeOperationId;
        service.SimulateCoordinatorRestart();
        service.Resume();
        Assert.Equal(tradeId, service.Run.TradeOperationId);
        service.SuppressInitialResponse = null;
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(2, service.Trades.Count);
        Assert.Equal(10UL, Delivered(service.Run.CompletedItems[0]));
        Assert.Equal(6UL, Delivered(service.Run.CompletedItems[1]));
        Assert.True(service.Run.CompletedItems[1].Definition.HighQuality);
    }

    [Fact]
    public void EarlierShortageDoesNotSkipLaterItemsAndRemainsVisibleAtCompletion()
    {
        var provider = Sim(1, PeerA, 3);
        provider.Inventory(200).Bags = 6;
        var service = new WorkflowService(PeerA, PeerB, provider, Sim(2, PeerB));
        service.StartJob(Saved(Job([1], (2, 10)), Other(200, 6)));
        service.RunToTerminal();

        Assert.Equal(CoordinatorStep.Finished, service.Run!.Step);
        Assert.True(service.Run.Paused);
        Assert.True(service.Run.HasShortages);
        Assert.Equal(2, service.Run.CompletedItems.Count);
        Assert.Equal(7UL, Assert.Single(service.Run.CompletedItems[0].Journal!.Plan.UnmetTargets).Missing);
        Assert.Equal(3UL, Delivered(service.Run.CompletedItems[0]));
        Assert.Equal(6UL, Delivered(service.Run.CompletedItems[1]));
        Assert.Empty(service.Run.CompletedItems[1].Journal!.Plan.UnmetTargets);
        Assert.Contains("All job items processed", service.Run.Status);
    }

    [Fact]
    public void SecondItemOverflowUsesOnlyItsOwnReceivedAllowanceAndKeepsFirstItemDelivery()
    {
        var provider = Sim(1, PeerA, 10);
        provider.Inventory(200).Bags = 20;
        var recipient = Sim(2, PeerB, capacity: 15);
        var service = new WorkflowService(PeerA, PeerB, provider, recipient);
        service.StartJob(Saved(Job([1], (2, 10)), Other(200, 20)));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(10U, recipient.Bags);
        Assert.Equal(0U, recipient.RetainerTotal);
        Assert.Equal(20UL, recipient.Inventory(200).Total);
        Assert.Equal(15UL, service.Run.CompletedItems[1].Deposited[2]);
        Assert.Empty(service.Run.CompletedItems[0].Deposited);
        Assert.Equal(10UL, Delivered(service.Run.CompletedItems[0]));
        Assert.Equal(20UL, Delivered(service.Run.CompletedItems[1]));
        Assert.All(service.Trace.Where(command => command.Operation == GameOperationKind.Deposit), command => Assert.Equal(200U, command.ItemId));
    }

    [Fact]
    public void InvalidLaterItemAndDuplicateItemQualityFailBeforeAnyGameCommand()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerB));
        Assert.Throws<ArgumentException>(() => service.StartJob(Saved(Job([1], (2, 10)), Job([1], (2, 1)))));
        var invalid = Other(200, 5); invalid.Sources = [0];
        Assert.Throws<ArgumentException>(() => service.StartJob(Saved(Job([1], (2, 10)), invalid)));
        Assert.Empty(service.Trace);
        Assert.Null(service.Run);
    }

    [Fact]
    public void MissingRosterSourceOnLaterItemSkipsThatItemAndPreservesEarlierDelivery()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerB));
        var later = Other(200, 5); later.Sources = [999];
        service.StartJob(Saved(Job([1], (2, 10)), later));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(2, service.Run.CompletedItems.Count);
        Assert.Equal(10UL, Delivered(service.Run.CompletedItems[0]));
        Assert.Contains(999UL, service.Run.CompletedItems[1].SkippedCharacters.Keys);
        Assert.False(string.IsNullOrWhiteSpace(service.Run.CompletedItems[1].ItemSkipReason));
        Assert.Null(service.Run.CompletedItems[1].Journal);
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 999);
        Assert.Equal(10U, Assert.Single(service.Trades).Quantity);
        Assert.Equal(10UL, service.Characters[2].Total);
        Assert.True(service.Run.HasSkippedCharacters);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("lost response")]
    [InlineData("saved response")]
    public void CompletelyFullReceiverStoresPriorItemAndRecoversItsExactDeposit(string interruption)
    {
        var provider = Sim(1, PeerA, 10); provider.Inventory(200).Bags = 6;
        var recipient = Sim(2, PeerB, capacity: 10);
        var service = new WorkflowService(PeerA, PeerB, provider, recipient);
        if (interruption == "lost response")
            service.SuppressInitialResponse = command => command.Operation.ItemId == 10155 && command.Operation.Kind == GameOperationKind.Deposit;
        service.StartJob(Saved(Job([1], (2, 10)), Other(200, 6)));
        if (interruption != "none")
        {
            service.RunUntil(run => run.ActiveItemIndex == 1 && run.Pending.Any(pending => pending.Command.Operation.Kind == GameOperationKind.Deposit && pending.InventoryItemIndex == 0));
            var pending = Assert.Single(service.Run!.Pending, candidate => candidate.Command.Operation.Kind == GameOperationKind.Deposit && candidate.InventoryItemIndex == 0);
            Assert.Equal(0, pending.InventoryItemIndex);
            Assert.Equal(10155U, pending.Command.Operation.ItemId);
            Assert.Equal(10U, recipient.RetainerTotal);
            Assert.Equal(0UL, service.Run.CompletedItems[0].Deposited.GetValueOrDefault(2UL));
            service.SimulateCoordinatorRestart();
            service.Resume();
            if (interruption == "lost response") Assert.Equal(pending.Command.Id,
                Assert.Single(service.Run.Pending, candidate => candidate.Command.Operation.Kind == GameOperationKind.Deposit).Command.Id);
            service.SuppressInitialResponse = null;
        }
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(10UL, Delivered(service.Run.CompletedItems[0]));
        Assert.Equal(6UL, Delivered(service.Run.CompletedItems[1]));
        Assert.Equal(10UL, service.Run.CompletedItems[0].Deposited[2]);
        Assert.Empty(service.Run.CompletedItems[1].Deposited);
        Assert.Equal(0U, recipient.Bags);
        Assert.Equal(10U, recipient.RetainerTotal);
        Assert.Equal(6U, recipient.Inventory(200).Bags);
        var deposit = Assert.Single(service.Trace, command => command.Operation == GameOperationKind.Deposit);
        Assert.Equal(10155U, deposit.ItemId);
        Assert.Equal(10U, deposit.Quantity);
    }

    [Fact]
    public void PartialPriorItemDepositIsCountedOnceBeforeCurrentItemContinues()
    {
        var provider = Sim(1, PeerA, 10); provider.Inventory(200).Bags = 6;
        var recipient = Sim(2, PeerB, capacity: 10);
        var service = new WorkflowService(PeerA, PeerB, provider, recipient) { DepositMovementLimit = 4 };
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.Deposit && command.Operation.ItemId == 10155)
            {
                service.DepositMovementLimit = null;
                result.Success = false; result.Error = "Interrupted after a verified prior-item deposit.";
            }
            return result;
        };
        service.StartJob(Saved(Job([1], (2, 10)), Other(200, 6)));
        service.RunToTerminal();
        Assert.True(service.Run!.Paused);
        Assert.Equal(4UL, service.Run.CompletedItems[0].Deposited[2]);
        Assert.Empty(service.Run.Deposited);
        service.SimulateCoordinatorRestart(); service.Resume(); service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(4UL, service.Run.CompletedItems[0].Deposited[2]);
        Assert.Equal(4UL, service.Run.CompletedItems[1].Deposited[2]);
        Assert.Equal(10UL, Delivered(service.Run.CompletedItems[0]));
        Assert.Equal(6UL, Delivered(service.Run.CompletedItems[1]));
        Assert.Equal(6U, recipient.Bags);
        Assert.Equal(4U, recipient.RetainerTotal);
        Assert.Single(service.Trace, command => command.Operation == GameOperationKind.Deposit && command.ItemId == 10155);
    }

    [Fact]
    public void UncertainPriorItemDepositNeverReplaysOrChargesCurrentItemAcrossRestarts()
    {
        var provider = Sim(1, PeerA, 10); provider.Inventory(200).Bags = 6;
        var recipient = Sim(2, PeerB, capacity: 10);
        var service = new WorkflowService(PeerA, PeerB, provider, recipient) { DepositMovementLimit = 6 };
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.Deposit)
            {
                result.Success = false; result.MovementUncertain = true; result.MovedQuantity = 4;
                result.Error = "Only four of six physically stored earlier-item units were verified.";
            }
            return result;
        };
        service.StartJob(Saved(Job([1], (2, 10)), Other(200, 6)));
        service.RunToTerminal();
        var original = Assert.Single(service.Run!.Pending, pending => pending.Command.Operation.Kind == GameOperationKind.Deposit).Command.Id;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.True(service.Run.Paused);
            Assert.Contains("could not be tallied automatically", service.LastError);
            Assert.Equal(4UL, service.Run.CompletedItems[0].Deposited[2]);
            Assert.Empty(service.Run.Deposited);
            Assert.Equal(6U, recipient.RetainerTotal);
            Assert.Single(service.Trace, command => command.Operation == GameOperationKind.Deposit);
            Assert.Equal(0UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
            service.SimulateCoordinatorRestart(); service.Resume();
            Assert.Equal(original, Assert.Single(service.Run.Pending, pending => pending.Command.Operation.Kind == GameOperationKind.Deposit).Command.Id);
            service.RunToTerminal();
        }
        Assert.True(service.Run.Paused);
        Assert.Single(service.Trace, command => command.Operation == GameOperationKind.Deposit);
    }

    [Fact]
    public void ProviderCanStoreItsEarlierReceivedItemBeforeCollectingTheNextItem()
    {
        var first = Sim(1, PeerA, 10);
        var second = Sim(2, PeerB, capacity: 10); second.Inventory(200).Retainers[1002] = 6;
        var next = Job([2], (1, 6)); next.ItemId = 200;
        var service = new WorkflowService(PeerA, PeerB, first, second);
        service.StartJob(Saved(Job([1], (2, 10)), next));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(10UL, Delivered(service.Run.CompletedItems[0]));
        Assert.Equal(6UL, Delivered(service.Run.CompletedItems[1], 1));
        Assert.Equal(10UL, service.Run.CompletedItems[0].Deposited[2]);
        Assert.Equal(6UL, service.Run.CompletedItems[1].Withdrawn[2]);
        Assert.Equal(10U, second.RetainerTotal);
        Assert.Equal(6U, first.Inventory(200).Bags);
        Assert.Equal(new uint[] { 10155, 200 }, service.Trades.Select(trade => trade.ItemId));
    }

    [Fact]
    public void PriorItemStorageDoesNotSpendItsProtectedPreJobBaseline()
    {
        var provider = Sim(1, PeerA, 5); provider.Inventory(200).Bags = 5;
        var recipient = Sim(2, PeerB, bags: 5, capacity: 10);
        var service = new WorkflowService(PeerA, PeerB, provider, recipient);
        service.StartJob(Saved(Job([1], (2, 5)), Other(200, 5)));
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(5U, Assert.Single(service.Trace, command => command.Operation == GameOperationKind.Deposit).Quantity);
        Assert.Equal(5U, recipient.Bags);
        Assert.Equal(5U, recipient.RetainerTotal);
        Assert.Equal(5UL, service.Run.CompletedItems[0].Deposited[2]);
        Assert.Equal(5UL, Delivered(service.Run.CompletedItems[1]));
    }

    [Fact]
    public void LegacySingleItemJournalRecoversPendingPickupWithoutRepeatingMovementOrDelivery()
    {
        var provider = Sim(1, PeerA, stored: 20);
        var recipient = Sim(2, PeerB);
        var service = new WorkflowService(PeerA, PeerB, provider, recipient)
        {
            SuppressInitialResponse = command => command.Operation.Kind == GameOperationKind.Withdraw
        };
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunUntil(run => run.Pending.Any(pending => pending.Command.Operation.Kind == GameOperationKind.Withdraw));
        var originalPickup = Assert.Single(service.Run!.Pending, pending => pending.Command.Operation.Kind == GameOperationKind.Withdraw);
        var originalCommandId = originalPickup.Command.Id;
        Assert.Null(originalPickup.Response);
        Assert.Equal(10U, provider.Bags); // The worker completed and saved the pickup before its response was lost.
        Assert.Equal(10U, provider.RetainerTotal);

        // Model an actual 0.2.2 checkpoint: these multi-item/automatic-relay fields did not exist.
        var legacy = Newtonsoft.Json.Linq.JObject.Parse(service.SavedJson);
        legacy.Remove("Preparation");
        legacy["Step"] = (int)CoordinatorStep.Withdraw;
        legacy["Pending"] = new Newtonsoft.Json.Linq.JArray(legacy["Pending"]!.Cast<Newtonsoft.Json.Linq.JObject>()
            .Where(pending => pending["Command"]!["Id"]!.ToObject<Guid>() == originalCommandId).Select(pending => pending.DeepClone()));
        foreach (var field in new[] { "ActiveItemIndex", "CompletedItems", "AutomaticRelays", "RejectedRelayCharacters",
                     "RelayCandidateId", "RelayCandidatePeerId", "PreviousItemDepositIndex", "PreviousItemDepositCharacterId" })
            legacy.Remove(field);
        ((Newtonsoft.Json.Linq.JObject)legacy["Job"]!).Remove("AdditionalItems");
        foreach (var pending in legacy["Pending"]!.Cast<Newtonsoft.Json.Linq.JObject>()) pending.Remove("InventoryItemIndex");
        // Replace only the harness's persisted file contents; its worker journal remains intact.
        typeof(WorkflowService).GetProperty(nameof(WorkflowService.SavedJson))!
            .SetValue(service, legacy.ToString(Newtonsoft.Json.Formatting.None));
        service.SimulateCoordinatorRestart();

        Assert.Equal(0, service.Run.ActiveItemIndex);
        Assert.Equal(1, service.Run.TotalItemCount);
        Assert.Empty(service.Run.CompletedItems);
        Assert.Empty(service.Run.Job.AdditionalItems);
        Assert.Equal(10155U, service.Run.ActiveDefinition.ItemId);
        service.Resume();
        Assert.Equal(originalCommandId, Assert.Single(service.Run.Pending).Command.Id);
        Assert.Equal(10155U, service.Run.Pending[0].Command.Operation.ItemId);
        service.SuppressInitialResponse = null;
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Equal(10UL, Delivered(Assert.Single(service.Run.CompletedItems)));
        Assert.Equal(10UL, service.Run.Withdrawn[1]);
        Assert.Single(service.Trace, command => command.Operation == GameOperationKind.Withdraw);
        Assert.Equal(10U, Assert.Single(service.Trades).Quantity);
        Assert.Equal(10U, recipient.Bags);
        Assert.Equal(0U, provider.Bags);
        Assert.Equal(10U, provider.RetainerTotal);
    }
}
