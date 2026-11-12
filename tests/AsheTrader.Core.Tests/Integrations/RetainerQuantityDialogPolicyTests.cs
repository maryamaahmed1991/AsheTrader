using AsheTrader.Workflow.Native;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class RetainerQuantityDialogPolicyTests
{
    private static readonly HashSet<uint> RetainerOnly = [10];
    private static readonly HashSet<uint> DepositInventories = [10, 20];
    private static readonly Dictionary<uint, QuantityDialogAddon> NoChildren = [];

    [Fact]
    public void DepositCanBelongToPlayerInventoryWhileBlockingRetainerInventory()
    {
        Assert.True(RetainerQuantityDialogPolicy.IsOwned(20, 10, DepositInventories, NoChildren));
        Assert.True(RetainerQuantityDialogPolicy.IsOwned(10, 20, DepositInventories, NoChildren));
        Assert.True(RetainerQuantityDialogPolicy.IsOwned(0, 20, DepositInventories, NoChildren));
    }

    [Fact]
    public void RetrievalRequiresTheRetainerInventoryOwner()
    {
        Assert.True(RetainerQuantityDialogPolicy.IsOwned(10, 0, RetainerOnly, NoChildren));
        Assert.True(RetainerQuantityDialogPolicy.IsOwned(0, 10, RetainerOnly, NoChildren));
        Assert.False(RetainerQuantityDialogPolicy.IsOwned(20, 0, RetainerOnly, NoChildren));
    }

    [Theory]
    [InlineData(0U, 0U)]
    [InlineData(10U, 30U)]
    [InlineData(30U, 20U)]
    [InlineData(30U, 0U)]
    public void AbsentOrUnrelatedOwnersAreRejected(uint parent, uint blocked)
        => Assert.False(RetainerQuantityDialogPolicy.IsOwned(parent, blocked, DepositInventories, NoChildren));

    [Fact]
    public void ReadyVisibleInventoryGridMayResolveThroughParentOrHost()
    {
        var children = new Dictionary<uint, QuantityDialogAddon>
        {
            [21] = new(21, 20, 0, true), [22] = new(22, 0, 21, true), [11] = new(11, 0, 10, true),
        };
        Assert.True(RetainerQuantityDialogPolicy.IsOwned(22, 11, DepositInventories, children));
        Assert.False(RetainerQuantityDialogPolicy.IsOwned(22, 11, RetainerOnly, children));
    }

    [Fact]
    public void HiddenOrUnreadyChildDoesNotEstablishOwnership()
    {
        var children = new Dictionary<uint, QuantityDialogAddon> { [21] = new(21, 20, 0, false) };
        Assert.False(RetainerQuantityDialogPolicy.IsOwned(21, 10, DepositInventories, children));
    }

    [Fact]
    public void CyclicOrExcessivelyDeepParentLinksAreRejected()
    {
        var children = new Dictionary<uint, QuantityDialogAddon> { [21] = new(21, 22, 0, true), [22] = new(22, 21, 0, true) };
        Assert.False(RetainerQuantityDialogPolicy.IsOwned(21, 10, DepositInventories, children));
        children = Enumerable.Range(30, 10).ToDictionary(id => (uint)id,
            id => new QuantityDialogAddon((uint)id, id == 39 ? 20U : (uint)id + 1, 0, true));
        Assert.False(RetainerQuantityDialogPolicy.IsOwned(30, 10, DepositInventories, children));
    }

    [Fact]
    public void ReportedPartialDepositAllows681FromThePending999Stack()
    {
        Assert.True(RetainerQuantityDialogPolicy.LimitsMatch(681, 999, 1, 999));
        Assert.True(RetainerQuantityDialogPolicy.LimitsMatch(681, 999, 1, 681));
    }

    [Theory]
    [InlineData(0U, 999U, 1U, 999U)]
    [InlineData(681U, 999U, 682U, 999U)]
    [InlineData(681U, 999U, 1U, 680U)]
    [InlineData(681U, 999U, 1U, 1000U)]
    [InlineData(999U, 999U, 1U, 999U)]
    public void QuantityLimitsMustFitTheOwnedPartialRequest(uint quantity, uint source, uint minimum, uint maximum)
        => Assert.False(RetainerQuantityDialogPolicy.LimitsMatch(quantity, source, minimum, maximum));
}
