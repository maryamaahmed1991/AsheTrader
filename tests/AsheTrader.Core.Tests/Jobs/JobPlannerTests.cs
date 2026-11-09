using AsheTrader.Core.Jobs;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class JobPlannerTests
{
    internal static readonly Guid PeerA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid PeerB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    internal static JobCharacterSnapshot Character(ulong id, Guid peer, ulong bags = 0, ulong retainer = 0) => new()
    {
        CharacterId = id, PeerId = peer, Name = $"Character {id}", World = "Test world",
        BagQuantity = bags, BagCapacity = 999,
        Verification = InventoryVerification.Verified, RetainerRosterVerified = true,
        Retainers = [new RetainerStock { RetainerId = id + 1000, Quantity = retainer, Verification = InventoryVerification.Verified }],
    };

    internal static TransferJobDefinition Job(ulong[] sources, params (ulong Character, ulong? Quantity)[] recipients) => new()
    {
        ItemId = 10155, Sources = [.. sources],
        Recipients = recipients.Select(recipient => new RecipientRule { CharacterId = recipient.Character, Quantity = recipient.Quantity }).ToList(),
    };

    [Fact]
    public void FillsRecipientsInOrderAcrossBagsAndRetainers()
    {
        var job = Job([1, 2], (3, 120), (4, 50), (5, null));
        var plan = JobPlanner.Plan(job, [Character(1, PeerA, 20, 100), Character(2, PeerA, 100), Character(3, PeerB), Character(4, PeerB), Character(5, PeerB)]);

        Assert.Equal(new ulong[] { 120, 50, 50 }, plan.Allocations.Select(allocation => allocation.Quantity));
        Assert.Equal(new ulong[] { 3, 4, 5 }, plan.Allocations.Select(allocation => allocation.RecipientCharacterId));
        Assert.Equal(220UL, plan.AllocatedQuantity);
        Assert.False(plan.HasShortage);
    }

    [Fact]
    public void SupplyShortageDoesNotAllocateLowPriorityBeforeHighPriority()
    {
        var plan = JobPlanner.Plan(Job([1], (2, 100), (3, 100)), [Character(1, PeerA, 130), Character(2, PeerB), Character(3, PeerB)]);

        Assert.Equal(new ulong[] { 100, 30 }, plan.Allocations.Select(allocation => allocation.Quantity));
        var shortage = Assert.Single(plan.UnmetTargets);
        Assert.Equal(3UL, shortage.CharacterId);
        Assert.Equal(70UL, shortage.Missing);
    }

    [Fact]
    public void RestockCountsRecipientRetainersAndOnlyDonatesExcessAboveProtectedTarget()
    {
        var job = Job([1, 2], (2, 100), (3, 60));
        job.QuantityMode = JobQuantityMode.RestockToTotal;
        var plan = JobPlanner.Plan(job, [Character(1, PeerA, 10), Character(2, PeerA, 20, 100), Character(3, PeerB, 10, 20)]);

        Assert.Equal(100UL, plan.ProtectedStock[2]);
        Assert.Equal(20UL, plan.SourceBudgets[2]);
        Assert.Equal(new ulong[] { 10, 20 }, plan.Allocations.Select(allocation => allocation.Quantity));
        Assert.All(plan.Allocations, allocation => Assert.Equal(3UL, allocation.RecipientCharacterId));
        Assert.False(plan.HasShortage);
    }

    [Fact]
    public void AdditionalDeliveryProtectsRecipientsExistingStock()
    {
        var job = Job([1, 2], (2, 50), (3, null));
        var plan = JobPlanner.Plan(job, [Character(1, PeerA, 100), Character(2, PeerB, 60, 50), Character(3, PeerB)]);

        Assert.Equal(0UL, plan.SourceBudgets[2]);
        Assert.Equal(110UL, plan.ProtectedStock[2]);
        Assert.All(plan.Allocations, allocation => Assert.Equal(1UL, allocation.SourceCharacterId));
        Assert.Equal(100UL, plan.AllocatedQuantity);
    }

    [Fact]
    public void SameAccountSourceRoutesThroughDesignatedOtherAccountRelay()
    {
        var job = Job([1], (2, 70));
        job.RelayCharacters[PeerB] = 3;
        var plan = JobPlanner.Plan(job, [Character(1, PeerA, 70), Character(2, PeerA), Character(3, PeerB)]);
        var allocation = Assert.Single(plan.Allocations);

        Assert.Equal(2, allocation.Legs.Count);
        Assert.Equal(1UL, allocation.Legs[0].SenderId);
        Assert.Equal(3UL, allocation.Legs[0].ReceiverId);
        Assert.Equal(3UL, allocation.Legs[1].SenderId);
        Assert.Equal(2UL, allocation.Legs[1].ReceiverId);
        Assert.Equal(PeerB, allocation.Legs[0].ReceiverPeerId);
    }

    [Fact]
    public void SameAccountSourceWithoutRelayIsRejected()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => JobPlanner.Plan(Job([1], (2, 10)), [Character(1, PeerA, 10), Character(2, PeerA)]));
        Assert.Contains("relay", failure.Message);
    }

    [Fact]
    public void RelayMembershipCannotClaimAnotherPeer()
    {
        var job = Job([1], (2, 10));
        job.RelayCharacters[PeerB] = 3;
        Assert.Throws<InvalidOperationException>(() => JobPlanner.Plan(job, [Character(1, PeerA, 10), Character(2, PeerA), Character(3, PeerA)]));
    }

    [Fact]
    public void VerifiedEmptySourceIsValidAndReportedAsShortage()
    {
        var plan = JobPlanner.Plan(Job([1], (2, 10)), [Character(1, PeerA), Character(2, PeerB)]);
        Assert.Empty(plan.Allocations);
        Assert.Equal(10UL, Assert.Single(plan.UnmetTargets).Missing);
    }

    [Theory]
    [InlineData(InventoryVerification.Unknown)]
    [InlineData(InventoryVerification.Cached)]
    public void CachedOrUnknownBagsCannotBeTreatedAsVerifiedZero(InventoryVerification verification)
    {
        var source = Character(1, PeerA);
        source.Verification = verification;
        Assert.Throws<InvalidOperationException>(() => JobPlanner.Plan(Job([1], (2, 10)), [source, Character(2, PeerB)]));
    }

    [Fact]
    public void MissingRetainerRosterPreventsMoveAllCompletingPrematurely()
    {
        var source = Character(1, PeerA, 10);
        source.RetainerRosterVerified = false;
        source.Retainers.Clear();
        Assert.Throws<InvalidOperationException>(() => JobPlanner.Plan(Job([1], (2, null)), [source, Character(2, PeerB)]));
    }

    [Fact]
    public void OneUnvisitedRetainerPreventsPlanning()
    {
        var source = Character(1, PeerA, 10);
        source.Retainers[0].Verification = InventoryVerification.Cached;
        Assert.Throws<InvalidOperationException>(() => JobPlanner.Plan(Job([1], (2, null)), [source, Character(2, PeerB)]));
    }

    [Fact]
    public void CompleteCacheCanBudgetStockWithoutBeingDeclaredVerified()
    {
        var source = Character(1, PeerA, 4, 16);
        source.Verification = InventoryVerification.Cached;
        source.RetainerRosterVerified = false;
        source.HasCompleteCachedInventory = true;
        source.Retainers[0].Verification = InventoryVerification.Cached;

        var plan = JobPlanner.Plan(Job([1], (2, 20)), [source, Character(2, PeerB)]);

        Assert.True(plan.PlannedFromCache);
        Assert.Equal(20UL, plan.AllocatedQuantity);
        Assert.False(source.IsFullyVerified);
        Assert.True(source.IsPlanningReady);
    }

    [Fact]
    public void CacheCompletenessFlagCannotOverrideUnknownRetainerData()
    {
        var source = Character(1, PeerA, 4, 16);
        source.Verification = InventoryVerification.Cached;
        source.HasCompleteCachedInventory = true;
        source.Retainers[0].Verification = InventoryVerification.Unknown;

        Assert.Throws<InvalidOperationException>(() => JobPlanner.Plan(Job([1], (2, null)), [source, Character(2, PeerB)]));
    }

    [Fact]
    public void CachedRecipientSuppliesEstimatedRestockBaseline()
    {
        var recipient = Character(2, PeerB, 4, 16);
        recipient.Verification = InventoryVerification.Cached;
        recipient.HasCompleteCachedInventory = true;
        var definition = Job([1], (2, 100));
        definition.QuantityMode = JobQuantityMode.RestockToTotal;

        var plan = JobPlanner.Plan(definition, [Character(1, PeerA, 100), recipient]);

        Assert.True(plan.PlannedFromCache);
        Assert.Equal(80UL, plan.AllocatedQuantity);
        Assert.Equal(20UL, plan.ProtectedStock[2]);
        Assert.False(recipient.IsFullyVerified);
    }

    [Fact]
    public void MissingCharacterSnapshotIsNotEmptyStock()
    {
        Assert.Throws<InvalidOperationException>(() => JobPlanner.Plan(Job([1], (2, null)), [Character(2, PeerB)]));
    }

    [Fact]
    public void UnlimitedRecipientMustBeLastAndOnlyMoveAll()
    {
        Assert.Throws<ArgumentException>(() => JobPlanner.Plan(Job([1], (2, null), (3, 10)), [Character(1, PeerA), Character(2, PeerB), Character(3, PeerB)]));
        var job = Job([1], (2, null));
        job.QuantityMode = JobQuantityMode.RestockToTotal;
        Assert.Throws<ArgumentException>(() => JobPlanner.Plan(job, [Character(1, PeerA), Character(2, PeerB)]));
    }

    [Fact]
    public void DuplicateSourcesAndRecipientsCannotDoubleCount()
    {
        var snapshots = new[] { Character(1, PeerA, 30), Character(2, PeerB) };
        Assert.Throws<ArgumentException>(() => JobPlanner.Plan(Job([1, 1], (2, 10)), snapshots));
        Assert.Throws<ArgumentException>(() => JobPlanner.Plan(Job([1], (2, 10), (2, 10)), snapshots));
    }

    [Fact]
    public void PlanCapturesRulesAndBudgetsRatherThanMutableInputs()
    {
        var source = Character(1, PeerA, 10, 20);
        var job = Job([1], (2, 30));
        var plan = JobPlanner.Plan(job, [source, Character(2, PeerB)]);
        source.BagQuantity = 9999;
        job.Recipients[0].Quantity = 9999;
        job.Sources.Clear();

        Assert.Equal(30UL, plan.SourceBudgets[1]);
        Assert.Equal(30UL, plan.Definition.Recipients[0].Quantity);
        Assert.Single(plan.Definition.Sources);
        Assert.Equal(30UL, Assert.Single(plan.Allocations).Quantity);
    }

    [Fact]
    public void LargeTotalsDoNotOverflowA32BitJob()
    {
        var amount = (ulong)uint.MaxValue + 500;
        var plan = JobPlanner.Plan(Job([1], (2, null)), [Character(1, PeerA, uint.MaxValue, 500), Character(2, PeerB)]);
        Assert.Equal(amount, plan.AllocatedQuantity);
    }

    [Fact]
    public void RecipientIsNeverItsOwnTransferSource()
    {
        var plan = JobPlanner.Plan(Job([1, 2], (2, 10)), [Character(1, PeerA, 10), Character(2, PeerB, 999)]);
        Assert.All(plan.Allocations, allocation => Assert.NotEqual(allocation.SourceCharacterId, allocation.RecipientCharacterId));
    }
}
