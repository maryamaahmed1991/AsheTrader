using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class VisitCoordinatorTests
{
    private const uint Fuel = 10_155;
    private const uint Coke = 200;
    private const uint Sap = 201;

    private static SimulatedCharacter Sim(ulong id, Guid peer, uint fuel = 0, uint storedFuel = 0, uint slots = 20, uint stack = 99) => new()
    {
        Id = id, PeerId = peer, Bags = fuel, StackSize = stack, BagSlotLimit = slots,
        BagLimit = uint.MaxValue, StorageLimit = uint.MaxValue,
        Retainers = new() { [id + 1000] = storedFuel }
    };

    private static void Stock(SimulatedCharacter character, uint itemId, uint bags = 0, uint stored = 0, uint stack = 99, bool highQuality = false) =>
        character.OtherItems[new(itemId, highQuality)] = new()
        {
            Bags = bags, StackSize = stack, Retainers = new() { [character.Id + 1000] = stored }
        };

    private static TransferJobDefinition Item(uint itemId, ulong[] sources, params (ulong Character, ulong Quantity)[] recipients) => new()
    {
        ItemId = itemId, Sources = sources.ToList(), QuantityMode = JobQuantityMode.DeliverAmount,
        Recipients = recipients.Select(rule => new RecipientRule { CharacterId = rule.Character, Quantity = rule.Quantity }).ToList()
    };

    private static SavedTransferJob Saved(params TransferJobDefinition[] items) => new()
    {
        Definition = items[0], AdditionalItems = items.Skip(1).ToList(),
        Meeting = new() { WorldId = 999, TerritoryId = 2, AetheryteId = 3 }
    };

    private static ulong Delivered(WorkflowService service, uint itemId, ulong recipientId, bool highQuality = false) =>
        new TransferJobLedger(service.Run!.CompletedItems.Single(item => item.Definition.ItemId == itemId
            && item.Definition.HighQuality == highQuality).Journal!).DeliveredTo(recipientId);

    private static void Finished(WorkflowService service)
    {
        Assert.Equal(CoordinatorStep.Finished, service.Run!.Step);
        Assert.False(service.Run.Paused, service.LastError);
        Assert.True(service.Run.VisitMode);
    }

    [Fact]
    public void EachProviderCollectsAllAssignedItemsBeforeLeavingAndSendsOneMixedTrade()
    {
        var first = Sim(1, PeerA, storedFuel: 20);
        var second = Sim(3, PeerA, storedFuel: 20);
        var recipient = Sim(2, PeerB);
        Stock(first, Coke, stored: 10);
        Stock(second, Coke, stored: 10);
        var service = new WorkflowService(PeerA, PeerB, first, second, recipient);
        service.StartJob(Saved(Item(Fuel, [1, 3], (2, 40)), Item(Coke, [1, 3], (2, 20))));
        service.RunToTerminal();

        Finished(service);
        Assert.Equal(40UL, Delivered(service, Fuel, 2));
        Assert.Equal(20UL, Delivered(service, Coke, 2));
        Assert.Equal(2, service.MixedTrades.Count);
        Assert.All(service.MixedTrades, trade => Assert.Equal(new uint[] { Fuel, Coke }, trade.Items.Select(item => item.ItemId)));
        foreach (var provider in new[] { first, second })
        {
            var commands = service.Trace.Where(command => command.CharacterId == provider.Id).ToArray();
            Assert.Single(commands, command => command.Operation == GameOperationKind.Login);
            var pickups = commands.Select((command, index) => (command, index))
                .Where(entry => entry.command.Operation == GameOperationKind.Withdraw).ToArray();
            Assert.Equal(new uint[] { Fuel, Coke }, pickups.Select(entry => entry.command.ItemId));
            var firstTravel = Array.FindIndex(commands, command => command.Operation == GameOperationKind.TravelMeeting);
            Assert.True(firstTravel > pickups.Max(entry => entry.index), "A provider left before collecting its other planned item.");
            Assert.Single(service.MixedTrades, trade => trade.SenderId == provider.Id);
            Assert.Equal(0UL, provider.Total);
            Assert.Equal(0UL, provider.Inventory(Coke).Total);
        }
        Assert.Equal(40U, recipient.Bags);
        Assert.Equal(20U, recipient.Inventory(Coke).Bags);
    }

    [Fact]
    public void CharacterAssignmentsAndQualityStayIsolatedAcrossSharedProviderVisits()
    {
        var provider = Sim(1, PeerA, fuel: 20);
        Stock(provider, Coke, bags: 10);
        Stock(provider, Sap, bags: 8);
        Stock(provider, Coke, bags: 4, highQuality: true);
        var first = Sim(2, PeerB);
        var second = Sim(4, PeerB);
        var highQuality = Item(Coke, [1], (4, 4));
        highQuality.HighQuality = true;
        var service = new WorkflowService(PeerA, PeerB, provider, first, second);
        service.StartJob(Saved(Item(Fuel, [1], (2, 5), (4, 15)), Item(Coke, [1], (2, 10)),
            Item(Sap, [1], (4, 8)), highQuality));
        service.RunToTerminal();

        Finished(service);
        Assert.Equal(5U, first.Bags);
        Assert.Equal(10U, first.Inventory(Coke).Bags);
        Assert.Equal(0U, first.Inventory(Sap).Bags);
        Assert.Equal(0U, first.Inventory(Coke, true).Bags);
        Assert.Equal(15U, second.Bags);
        Assert.Equal(0U, second.Inventory(Coke).Bags);
        Assert.Equal(8U, second.Inventory(Sap).Bags);
        Assert.Equal(4U, second.Inventory(Coke, true).Bags);
        foreach (var trade in service.MixedTrades)
            foreach (var line in trade.Items)
                Assert.Contains(service.Run!.Job.ItemDefinitions, item => item.ItemId == line.ItemId
                    && item.HighQuality == line.HighQuality && item.Recipients.Any(rule => rule.CharacterId == trade.ReceiverId));
        Assert.Equal(4UL, Delivered(service, Coke, 4, true));
    }

    [Fact]
    public void PerItemRecipientPriorityRestockTargetsAndShortagesRemainIndependent()
    {
        var provider = Sim(1, PeerA, fuel: 20);
        Stock(provider, Coke, bags: 6);
        var first = Sim(2, PeerB, fuel: 8);
        var second = Sim(4, PeerB);
        var fuel = Item(Fuel, [1], (2, 12), (4, 16));
        fuel.QuantityMode = JobQuantityMode.RestockToTotal;
        var service = new WorkflowService(PeerA, PeerB, provider, first, second);
        service.StartJob(Saved(fuel, Item(Coke, [1], (2, 2), (4, 5))));
        service.RunToTerminal();

        Assert.Equal(CoordinatorStep.Finished, service.Run!.Step);
        Assert.True(service.Run.Paused);
        Assert.True(service.Run.HasShortages);
        Assert.Equal(12U, first.Bags);
        Assert.Equal(16U, second.Bags);
        Assert.Equal(2U, first.Inventory(Coke).Bags);
        Assert.Equal(4U, second.Inventory(Coke).Bags);
        Assert.Equal(4UL, Delivered(service, Fuel, 2));
        Assert.Equal(16UL, Delivered(service, Fuel, 4));
        Assert.Equal(2UL, Delivered(service, Coke, 2));
        Assert.Equal(4UL, Delivered(service, Coke, 4));
        Assert.Empty(service.Run.CompletedItems.Single(item => item.Definition.ItemId == Fuel).Journal!.Plan.UnmetTargets);
        Assert.Equal(1UL, Assert.Single(service.Run.CompletedItems.Single(item => item.Definition.ItemId == Coke).Journal!.Plan.UnmetTargets).Missing);
    }

    [Fact]
    public void RelayCollectsMixedCargoFromMultipleProvidersUntilItsSharedSlotsAreFull()
    {
        var providers = new[] { Sim(1, PeerA, fuel: 2, stack: 4), Sim(3, PeerA, fuel: 2, stack: 4), Sim(5, PeerA, fuel: 2, stack: 4) };
        foreach (var provider in providers) Stock(provider, Coke, bags: 2, stack: 4);
        var recipient = Sim(2, PeerA, stack: 4);
        Stock(recipient, Coke, stack: 4);
        var relay = Sim(4, PeerB, slots: 2, stack: 4);
        Stock(relay, Coke, stack: 4);
        var service = new WorkflowService(PeerA, PeerB, [.. providers, recipient, relay]);
        // A generous stale ranking hint cannot replace the actual two-slot inspection.
        service.SetCachedFreeSlots(relay.Id, 140);
        service.StartJob(Saved(Item(Fuel, [1, 3, 5], (2, 6)), Item(Coke, [1, 3, 5], (2, 6))));
        service.RunToTerminal();

        Finished(service);
        var firstDelivery = service.MixedTrades.FindIndex(trade => trade.SenderId == relay.Id);
        Assert.Equal(2, firstDelivery);
        Assert.All(service.MixedTrades.Take(firstDelivery), trade =>
        {
            Assert.Equal(relay.Id, trade.ReceiverId);
            Assert.Equal(2, trade.Items.Count);
        });
        var nextPickup = service.MixedTrades.FindIndex(firstDelivery, trade => trade.ReceiverId == relay.Id);
        Assert.True(nextPickup > firstDelivery);
        var drained = service.MixedTrades.Skip(firstDelivery).Take(nextPickup - firstDelivery).ToArray();
        Assert.All(drained, trade => Assert.Equal(relay.Id, trade.SenderId));
        Assert.Equal(4U, drained.SelectMany(trade => trade.Items).Where(item => item.ItemId == Fuel).Aggregate(0U, (sum, item) => sum + item.Quantity));
        Assert.Equal(4U, drained.SelectMany(trade => trade.Items).Where(item => item.ItemId == Coke).Aggregate(0U, (sum, item) => sum + item.Quantity));
        Assert.All(service.MixedTrades.Where(trade => trade.ReceiverId == relay.Id), trade => Assert.True(trade.Items.Sum(item => item.Quantity) <= 8));
        Assert.Equal(6UL, Delivered(service, Fuel, 2));
        Assert.Equal(6UL, Delivered(service, Coke, 2));
        Assert.Equal(0U, relay.Bags);
        Assert.Equal(0U, relay.Inventory(Coke).Bags);
        Assert.All(service.Run!.CompletedItems, item => Assert.Equal(0UL, new TransferJobLedger(item.Journal!).RelayCargo(relay.Id)));
    }

    [Fact]
    public void RecipientDepositsReceivedItemsToMakeRoomForTheNextMixedBatch()
    {
        var first = Sim(1, PeerA, fuel: 4, stack: 4);
        var second = Sim(3, PeerA, fuel: 4, stack: 4);
        Stock(first, Coke, bags: 4, stack: 4);
        Stock(second, Coke, bags: 4, stack: 4);
        var recipient = Sim(2, PeerB, slots: 2, stack: 4);
        Stock(recipient, Coke, stack: 4);
        var service = new WorkflowService(PeerA, PeerB, first, second, recipient);
        service.StartJob(Saved(Item(Fuel, [1, 3], (2, 8)), Item(Coke, [1, 3], (2, 8))));
        service.RunToTerminal();

        Finished(service);
        Assert.Equal(8UL, recipient.Total);
        Assert.Equal(8UL, recipient.Inventory(Coke).Total);
        Assert.Equal(8UL, Delivered(service, Fuel, 2));
        Assert.Equal(8UL, Delivered(service, Coke, 2));
        Assert.True(recipient.RetainerTotal >= 4);
        Assert.True(recipient.Inventory(Coke).RetainerTotal >= 4);
        Assert.Contains(service.Trace, command => command.CharacterId == 2 && command.Operation == GameOperationKind.Deposit && command.ItemId == Fuel);
        Assert.Contains(service.Trace, command => command.CharacterId == 2 && command.Operation == GameOperationKind.Deposit && command.ItemId == Coke);
        Assert.All(service.MixedTrades, trade => Assert.Equal(2, trade.Items.Count));
    }

    [Fact]
    public void LostMixedTradeReplyAndRestartCommitEachItemExactlyOnce()
    {
        var provider = Sim(1, PeerA, fuel: 20);
        Stock(provider, Coke, bags: 10);
        var recipient = Sim(2, PeerB);
        var service = new WorkflowService(PeerA, PeerB, provider, recipient);
        service.SuppressInitialResponse = command => command.Kind == WorkerCommandKind.SendBatch && command.TradeItems.Count == 2;
        service.StartJob(Saved(Item(Fuel, [1], (2, 20)), Item(Coke, [1], (2, 10))));
        service.RunUntil(run => service.MixedTrades.Count == 1 && run.Pending.Any(pending => pending.Command.Kind == WorkerCommandKind.SendBatch && pending.Response == null));
        var command = Assert.Single(service.Run!.Pending, pending => pending.Command.Kind == WorkerCommandKind.SendBatch).Command.Id;
        service.SimulateCoordinatorRestart();
        service.SuppressInitialResponse = null;
        service.Resume();
        service.RunToTerminal();

        Finished(service);
        Assert.Equal(command, Assert.Single(service.MixedTrades).SenderCommandId);
        Assert.Equal(20UL, Delivered(service, Fuel, 2));
        Assert.Equal(10UL, Delivered(service, Coke, 2));
        Assert.Equal(20U, recipient.Bags);
        Assert.Equal(10U, recipient.Inventory(Coke).Bags);
        Assert.All(service.Run!.CompletedItems, item => Assert.Single(item.Journal!.Operations, operation => operation.State == TransferOperationState.Committed));
    }

    [Fact]
    public void LostSecondPickupResultResumesTheSameCharacterVisitWithoutWithdrawingTwice()
    {
        var provider = Sim(1, PeerA, storedFuel: 20);
        Stock(provider, Coke, stored: 10);
        var service = new WorkflowService(PeerA, PeerB, provider, Sim(2, PeerB));
        service.SuppressInitialResponse = command => command.Operation.Kind == GameOperationKind.Withdraw && command.Operation.ItemId == Coke;
        service.StartJob(Saved(Item(Fuel, [1], (2, 20)), Item(Coke, [1], (2, 10))));
        service.RunUntil(run => run.Pending.Any(pending => pending.Command.Operation.Kind == GameOperationKind.Withdraw
            && pending.Command.Operation.ItemId == Coke && pending.Response == null));
        Assert.Equal(20U, provider.Bags);
        Assert.Equal(10U, provider.Inventory(Coke).Bags);
        service.SimulateCoordinatorRestart();
        service.SuppressInitialResponse = null;
        service.Resume();
        service.RunToTerminal();

        Finished(service);
        Assert.Single(service.Trace, command => command.Operation == GameOperationKind.Withdraw && command.ItemId == Fuel);
        Assert.Single(service.Trace, command => command.Operation == GameOperationKind.Withdraw && command.ItemId == Coke);
        Assert.Equal(20UL, service.Run!.CompletedItems.Single(item => item.Definition.ItemId == Fuel).Withdrawn[1]);
        Assert.Equal(10UL, service.Run.CompletedItems.Single(item => item.Definition.ItemId == Coke).Withdrawn[1]);
        Assert.Equal(20UL, Delivered(service, Fuel, 2));
        Assert.Equal(10UL, Delivered(service, Coke, 2));
    }

    [Fact]
    public void PausedLegacyRunUpgradesAtAnIdleBoundaryWithoutReplayingItsCompletedItem()
    {
        var provider = Sim(1, PeerA, fuel: 10);
        Stock(provider, Coke, bags: 20);
        Stock(provider, Sap, bags: 6);
        var recipient = Sim(2, PeerB);
        var service = new WorkflowService(PeerA, PeerB, provider, recipient);
        service.StartLegacyJob(Saved(Item(Fuel, [1], (2, 10)), Item(Coke, [1], (2, 20)), Item(Sap, [1], (2, 6))));
        service.RunUntil(run => run.ActiveItemIndex == 1 && run.Step == CoordinatorStep.SelectLeg && run.Pending.Count == 0 && run.Journal != null);
        var originalFuelTrade = Assert.Single(service.Trades);
        var originalFuelOperations = Assert.Single(service.Run!.CompletedItems).Journal!.Operations.Select(operation => operation.Id).ToArray();
        Assert.Equal(10UL, Delivered(service, Fuel, 2));
        service.Run.VisitMode = false;
        service.Run.PreserveLegacyItemOrder = false;
        service.PauseForTest("Old saved multi-item run paused between visits.");
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.RunToTerminal();

        Finished(service);
        Assert.Equal(originalFuelTrade.SenderCommandId, Assert.Single(service.Trades, trade => trade.ItemId == Fuel).SenderCommandId);
        Assert.Equal(originalFuelOperations, service.Run!.CompletedItems.Single(item => item.Definition.ItemId == Fuel).Journal!.Operations.Select(operation => operation.Id));
        Assert.Equal(10UL, Delivered(service, Fuel, 2));
        Assert.Equal(20UL, Delivered(service, Coke, 2));
        Assert.Equal(6UL, Delivered(service, Sap, 2));
        Assert.Equal(new uint[] { Coke, Sap }, Assert.Single(service.MixedTrades).Items.Select(item => item.ItemId));
        Assert.Equal(10U, recipient.Bags);
        Assert.Equal(20U, recipient.Inventory(Coke).Bags);
        Assert.Equal(6U, recipient.Inventory(Sap).Bags);
    }

    [Fact]
    public void EqualTotalWithWrongItemDeltasCannotCommitAnInterruptedMixedTrade()
    {
        var provider = Sim(1, PeerA, fuel: 20);
        Stock(provider, Coke, bags: 10);
        var recipient = Sim(2, PeerB);
        var service = new WorkflowService(PeerA, PeerB, provider, recipient) { DeferTradeCompletion = true };
        service.StartJob(Saved(Item(Fuel, [1], (2, 20)), Item(Coke, [1], (2, 10))));
        service.RunUntil(run => run.Pending.Count(pending => pending.Command.Kind is WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch) == 2);
        provider.Bags = 0;
        provider.Inventory(Coke).Bags = 0;
        recipient.Bags = 10;
        recipient.Inventory(Coke).Bags = 20;
        service.FailTradeForTest(WorkerCommandKind.SendBatch, "Interrupted mixed trade.");
        service.RunToTerminal();

        Assert.True(service.Run!.Paused);
        Assert.Empty(service.MixedTrades);
        Assert.All(service.Run.VisitItems.Values.Where(item => item.Journal != null), item =>
            Assert.DoesNotContain(item.Journal!.Operations, operation => operation.State == TransferOperationState.Committed));
        Assert.Single(service.Trace, command => command.Kind == WorkerCommandKind.SendBatch);
        Assert.Equal(10U, recipient.Bags);
        Assert.Equal(20U, recipient.Inventory(Coke).Bags);
    }
}
