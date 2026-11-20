using AsheTrader.Workflow.Native;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class SaddlebagReadSessionTests
{
    [Fact]
    public void LoadedContainersRequireOneVerifiedOpeningThenSurviveRepeatedInspections()
    {
        var session = new SaddlebagReadSession();
        session.Observe(1, 100, true, true);
        Assert.False(session.CanReadSilently);
        session.ConfirmLoaded();
        for (var item = 0; item < 8; item++)
        {
            session.Observe(1, 100, true, true);
            Assert.True(session.CanReadSilently);
        }
    }

    [Theory]
    [InlineData(0UL, 100U, true, true)]
    [InlineData(2UL, 100U, true, true)]
    [InlineData(1UL, 101U, true, true)]
    [InlineData(1UL, 100U, false, true)]
    [InlineData(1UL, 100U, true, false)]
    public void SessionChangesRequireFreshOpeningEvenIfOldContainersRemainLoaded(
        ulong character, uint territory, bool premium, bool loaded)
    {
        var session = new SaddlebagReadSession();
        session.Observe(1, 100, true, true);
        session.ConfirmLoaded();
        session.Observe(character, territory, premium, loaded);
        Assert.False(session.CanReadSilently);
        session.Observe(1, 100, true, true);
        Assert.False(session.CanReadSilently);
        session.ConfirmLoaded();
        Assert.True(session.CanReadSilently);
    }
}
