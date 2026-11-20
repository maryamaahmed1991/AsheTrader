using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class SaddlebagCoordinatorTests
{
    [Theory]
    [InlineData(31_000)]
    [InlineData(120_000)]
    public void ResumingLongCargoLoadWaitsForOriginalCommandWithoutManualResume(int elapsed)
    {
        var source = Character(1, PeerA, 297); var target = Character(2, PeerB, bags: 10);
        var service = new WorkflowService(PeerA, PeerB, source, target)
        { DeferGameCommand = c => c.Operation.Kind == GameOperationKind.ArrangeSaddlebags };
        service.StartJob(Job([1], 2, 297));
        service.RunUntil(_ => service.DeferredGameCommands.Count > 0);
        var original = Assert.Single(service.DeferredGameCommands);
        service.PauseForTest(); service.SimulateCoordinatorRestart(); service.Resume();
        service.ElapsePreparationRecoveryForTest(elapsed);
        for (var i = 0; i < 10; i++) service.Tick();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Contains(service.Run.Pending, p => p.Command.Id == original.Id);
        Assert.Empty(service.Trades);
        service.DeferGameCommand = null;
        service.ReleaseDeferredGameCommand(original.Id);
        Complete(service);
        Assert.Equal(297U, target.Bags); Assert.Equal(0U, source.Saddle);
    }

    [Fact]
    public void InterruptedCargoAutomaticallyReconcilesSameAbsoluteGoalExactlyOnce()
    {
        var source = Character(1, PeerA, 297); var target = Character(2, PeerB, bags: 10);
        var service = new WorkflowService(PeerA, PeerB, source, target);
        Guid? interrupted = null;
        var calls = 0;
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.ArrangeSaddlebags && interrupted == null)
            { interrupted = command.Id; result.Success = false; result.MovementUncertain = true; result.Error = "Delayed movement acknowledgement"; }
            if (command.Id == interrupted) calls++;
            return result;
        };
        service.StartJob(Job([1], 2, 297)); Complete(service);
        Assert.Equal(2, calls);
        Assert.Equal(297U, target.Bags); Assert.Equal(0U, source.Saddle);
        Assert.Contains(service.Diagnostics, d => d.Contains("Automatically reconciling saddlebag cargo"));
    }

    [Fact]
    public void RepeatedUnreconciledCargoStopsAfterBoundedAutomaticAttempts()
    {
        var service = new WorkflowService(PeerA, PeerB, Character(1, PeerA, 297), Character(2, PeerB, bags: 10));
        var attempts = 0;
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.ArrangeSaddlebags)
            { attempts++; result.Success = false; result.MovementUncertain = true; result.Error = "Unresolved inventory evidence"; }
            return result;
        };
        service.StartJob(Job([1], 2, 297)); service.RunToTerminal();
        Assert.True(service.Run!.Paused);
        Assert.Equal(3, attempts);
        Assert.Contains("two automatic attempts", service.LastError);
        Assert.Empty(service.Trades);
    }

    [Fact]
    public void UserPauseDoesNotTriggerAutomaticCargoRecovery()
    {
        var service = new WorkflowService(PeerA, PeerB, Character(1, PeerA, 297), Character(2, PeerB, bags: 10));
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.ArrangeSaddlebags)
            { result.Success = false; result.MovementUncertain = true; }
            return result;
        };
        service.StartJob(Job([1], 2, 297));
        service.RunUntil(r => r.Pending.Any(p => p.Command.Operation.Kind == GameOperationKind.ArrangeSaddlebags));
        service.PauseForTest();
        for (var i = 0; i < 10; i++) service.Tick();
        Assert.True(service.Run!.Paused);
        Assert.All(service.Run.Pending, p => Assert.Equal(0, p.CargoRecoveryAttempts));
        Assert.DoesNotContain(service.Diagnostics, d => d.Contains("Automatically reconciling saddlebag cargo"));
    }

    [Fact]
    public void PermanentlyMissingCargoOutcomeStillStopsWithOriginalIntentPreserved()
    {
        var service = new WorkflowService(PeerA, PeerB, Character(1, PeerA, 297), Character(2, PeerB, bags: 10))
        { DeferGameCommand = c => c.Operation.Kind == GameOperationKind.ArrangeSaddlebags };
        service.StartJob(Job([1], 2, 297));
        service.RunUntil(_ => service.DeferredGameCommands.Count > 0);
        var original = Assert.Single(service.DeferredGameCommands);
        service.PauseForTest(); service.SimulateCoordinatorRestart(); service.Resume();
        service.ElapsePreparationRecoveryForTest(22 * 60_000);
        service.Tick();
        Assert.True(service.Run!.Paused);
        Assert.Contains("twenty-one minutes", service.LastError);
        Assert.Equal(original.Id, Assert.Single(service.Run.Pending).Command.Id);
        Assert.Empty(service.Trades);
    }

    [Fact]
    public void ConsecutiveBatchesKeepPreparedPairAndDrainBagsBeforeSaddleRefill()
    {
        var source = Character(1, PeerA, 1980, bags: 10, saddles: 10);
        var target = Character(2, PeerB, bags: 50);
        var service = new WorkflowService(PeerA, PeerB, source, target);
        service.StartJob(Job([1], 2, 1980)); Complete(service);
        Assert.Equal(1980U, target.Bags);
        var sends = service.Trace.Select((c, i) => (c, i)).Where(x => x.c.Kind == WorkerCommandKind.SendBatch).ToArray();
        Assert.Equal(4, sends.Length);
        var between = service.Trace.Skip(sends[0].i + 1).Take(sends[1].i - sends[0].i - 1).ToArray();
        Assert.DoesNotContain(between, c => c.Kind == WorkerCommandKind.Game && c.Operation is
            GameOperationKind.ArrangeSaddlebags or GameOperationKind.Login or GameOperationKind.TravelMeeting or GameOperationKind.Withdraw);
        Assert.Contains(between, c => c.Kind == WorkerCommandKind.Game && c.Operation == GameOperationKind.Inspect);
        Assert.Contains(service.Diagnostics, d => d.Contains("Continuing ready trade pair"));
    }

    private static SimulatedCharacter Character(ulong id, Guid peer, uint stock = 0, uint bags = 1, uint saddles = 2) => new()
    {
        Id = id, PeerId = peer, BagLimit = uint.MaxValue, BagSlotLimit = bags, SaddleSlotLimit = saddles,
        StackSize = 99, StorageLimit = uint.MaxValue, Retainers = new() { [id + 1000] = stock }
    };
    private static SavedTransferJob Job(ulong[] sources, ulong recipient, ulong quantity) => new()
    {
        Definition = new() { ItemId = 10155, Sources = sources.ToList(), QuantityMode = JobQuantityMode.DeliverAmount,
            Recipients = [new() { CharacterId = recipient, Quantity = quantity }] },
        Meeting = new() { WorldId = 1, TerritoryId = 2, AetheryteId = 3 }
    };
    private static void Complete(WorkflowService service)
    {
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(CoordinatorStep.Finished, service.Run.Step);
    }
    [Fact]
    public void ProviderPacksExtraLoadsBeforeFirstMeetingAndUnpacksToFinish()
    {
        var source = Character(1, PeerA, 297); var target = Character(2, PeerB, bags: 10);
        var service = new WorkflowService(PeerA, PeerB, source, target);
        service.StartJob(Job([1], 2, 297)); Complete(service);
        var firstTravel = service.Trace.FindIndex(c => c.CharacterId == 1 && c.Operation == GameOperationKind.TravelMeeting);
        Assert.Equal(297L, service.Trace.Take(firstTravel).Where(c => c.Operation == GameOperationKind.Withdraw).Sum(c => (long)c.Quantity));
        Assert.Equal(297U, target.Bags); Assert.Equal(0U, source.Saddle); Assert.Equal(0U, source.RetainerTotal);
    }
    [Fact]
    public void RelayUsesSaddlesBeforeFlushingAndLeavesNoCargo()
    {
        var a = Character(1, PeerA, 99); var b = Character(3, PeerA, 99); var c = Character(5, PeerA, 99);
        var recipient = Character(7, PeerA, bags: 10); var relay = Character(2, PeerB);
        var service = new WorkflowService(PeerA, PeerB, a, b, c, recipient, relay);
        service.StartJob(Job([1, 3, 5], 7, 297)); Complete(service);
        Assert.Equal(3, service.Trades.TakeWhile(t => t.SenderId != 2).Count());
        Assert.Equal(297U, recipient.Bags); Assert.Equal(0U, relay.Bags); Assert.Equal(0U, relay.Saddle);
    }
    [Fact]
    public void RecipientPacksThenDepositsPackedCargoWithoutOverDepositing()
    {
        var source = Character(1, PeerA, 495, bags: 10); var target = Character(2, PeerB);
        var service = new WorkflowService(PeerA, PeerB, source, target);
        service.StartJob(Job([1], 2, 495)); Complete(service);
        Assert.Equal(495UL, (ulong)target.Bags + target.Saddle + target.RetainerTotal);
        Assert.Contains(service.Trace, c => c.CharacterId == 2 && c.Operation == GameOperationKind.ArrangeSaddlebags);
        Assert.Contains(service.Trace, c => c.CharacterId == 2 && c.Operation == GameOperationKind.Deposit);
    }
    [Fact]
    public void LostSaddleResponseSurvivesRestartWithoutPackingTwice()
    {
        var source = Character(1, PeerA, 297); var target = Character(2, PeerB, bags: 10);
        var service = new WorkflowService(PeerA, PeerB, source, target);
        service.SuppressInitialResponse = c => c.Operation.Kind == GameOperationKind.ArrangeSaddlebags;
        service.StartJob(Job([1], 2, 297));
        service.RunUntil(r => source.Saddle > 0);
        service.SimulateCoordinatorRestart(); service.SuppressInitialResponse = null; service.Resume(); Complete(service);
        Assert.Equal(297U, target.Bags); Assert.Equal(0U, source.Saddle);
    }
    [Fact]
    public void SlotPolicyPreservesOtherItemsAndRespectsCargoBudget()
    {
        CargoSlot[] source = [new(0, 0, 10155, false, 99, 999, true)];
        CargoSlot[] destinations = [new(4000, 0, 200, false, 5, 999, true), new(4000, 1, 10155, false, 980, 999, true)];
        Assert.Null(SaddlebagCargoPolicy.NextMove(source, destinations, 10155, false, 18));
        var move = SaddlebagCargoPolicy.NextMove(source, destinations, 10155, false, 19)!;
        Assert.Equal(19U, move.Quantity); Assert.Equal((ushort)1, move.Destination.Slot);
        Assert.Equal(119U, SaddlebagCargoPolicy.ReconcileCargo(100, 980, 999));
        Assert.Throws<InvalidDataException>(() => SaddlebagCargoPolicy.ReconcileCargo(0, 99, 0));
    }
}
