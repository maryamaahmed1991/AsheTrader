using AsheTrader.Core.Utilities;

namespace AsheTrader.Core.Tests;

public sealed class UtilityQueueTests
{
    private sealed class Runtime : IUtilityRuntime
    {
        public bool Complete, Eligible = true, Running, Ready = true, Expected = true;
        public int Starts, Stops, Logins;
        public bool Login(UtilityEntry entry) { Logins++; return true; }
        public bool PlayerReady(ulong id) => Ready;
        public QuestEligibility Check() => new(Complete, Eligible, "Missing prerequisite");
        public bool StartQuest() { Starts++; Running = true; return true; }
        public bool QuestRunning => Running;
        public bool QuestComplete => Complete;
        public bool ExpectedQuest => Expected;
        public void Stop() { Stops++; Running = false; }
    }
    private static UtilityRun Run(int count = 1) => new() { Entries = Enumerable.Range(1, count).Select(i => new UtilityEntry { CharacterId = (ulong)i, Name = $"Alt {i}" }).ToList() };
    [Theory]
    [InlineData(true, true, "Already unlocked")]
    [InlineData(false, false, "Skipped: Missing prerequisite")]
    public void SkipsCompletedOrIneligibleAndFinishes(bool complete, bool eligible, string result)
    {
        var run = Run(2); var runtime = new Runtime { Complete = complete, Eligible = eligible }; var q = new UtilityQueue(run, runtime, () => { });
        q.Resume(); for (var i = 0; i < 5; i++) q.Tick(i);
        Assert.Equal(UtilityStage.Finished, run.Stage); Assert.Equal(0, runtime.Starts);
        Assert.All(run.Entries, e => Assert.Equal(result, e.Result));
    }
    [Fact]
    public void DoesNotChangeCharactersBeforeQuestCompletion()
    {
        var run = Run(2); var runtime = new Runtime(); var q = new UtilityQueue(run, runtime, () => { });
        q.Resume(); q.Tick(0); q.Tick(1); q.Tick(100);
        Assert.Equal(1, runtime.Logins); Assert.Equal(0, run.Index);
        runtime.Complete = true; q.Tick(200); q.Tick(201);
        Assert.Equal("Unlocked", run.Entries[0].Result); Assert.Equal(2, runtime.Logins);
    }
    [Fact]
    public void ResumeRechecksCompletedQuestWithoutStartingAgain()
    {
        var run = Run(); var runtime = new Runtime(); var q = new UtilityQueue(run, runtime, () => { });
        q.Resume(); q.Tick(0); q.Tick(1); q.Pause("Test"); runtime.Complete = true;
        q.Resume(); q.Tick(2); q.Tick(3); q.Tick(4);
        Assert.Equal(1, runtime.Starts); Assert.Equal(UtilityStage.Finished, run.Stage);
    }
    [Fact]
    public void WaitsThroughLoadingAndReportsStoppedQuestionable()
    {
        var run = Run(); var runtime = new Runtime { Ready = false }; var q = new UtilityQueue(run, runtime, () => { });
        q.Resume(); q.Tick(0); q.Tick(1); Assert.Equal(0, runtime.Starts);
        runtime.Ready = true; q.Tick(2); runtime.Running = false; q.Tick(3);
        Assert.Throws<InvalidOperationException>(() => q.Tick(10_004));
        q.Pause("Stopped"); Assert.False(q.Active); q.End(); Assert.Equal(UtilityStage.Finished, run.Stage);
    }
    [Fact]
    public void CannotDispatchBeforeIntentIsSaved()
    {
        var run = Run(); var runtime = new Runtime(); var fail = false;
        var q = new UtilityQueue(run, runtime, () => { if (fail) throw new IOException(); });
        q.Resume(); q.Tick(0); fail = true;
        Assert.Throws<IOException>(() => q.Tick(1)); Assert.Equal(0, runtime.Starts);
    }
}
