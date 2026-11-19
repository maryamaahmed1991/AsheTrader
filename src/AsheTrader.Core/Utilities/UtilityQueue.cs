namespace AsheTrader.Core.Utilities;

public enum UtilityStage { Login, Check, Quest, Finished }
public sealed class UtilityEntry
{
    public ulong CharacterId { get; set; }
    public string Name { get; set; } = "";
    public string World { get; set; } = "";
    public string Result { get; set; } = "Waiting";
}
public sealed class UtilityRun
{
    public List<UtilityEntry> Entries { get; set; } = [];
    public int Index { get; set; }
    public UtilityStage Stage { get; set; }
    public bool Paused { get; set; } = true;
    public string Status { get; set; } = "Ready.";
}
public sealed record QuestEligibility(bool Complete, bool Eligible, string Reason);
public interface IUtilityRuntime
{
    bool Login(UtilityEntry entry);
    bool PlayerReady(ulong characterId);
    QuestEligibility Check();
    bool StartQuest();
    bool QuestRunning { get; }
    bool QuestComplete { get; }
    bool ExpectedQuest { get; }
    void Stop();
}

/// <summary>One local queue; restarting always rechecks live quest completion before dispatch.</summary>
public sealed class UtilityQueue(UtilityRun run, IUtilityRuntime runtime, Action persist)
{
    private long questStarted;
    private long? idleSince;
    public bool Active => !run.Paused && run.Stage != UtilityStage.Finished;
    public void Resume()
    {
        if (run.Stage == UtilityStage.Finished) return;
        run.Stage = UtilityStage.Login;
        run.Paused = false;
        run.Status = "Checking selected characters…";
        idleSince = null;
        persist();
    }
    public void Pause(string reason)
    {
        run.Paused = true; run.Status = reason;
        // Persist the pause even if a dependent plugin has unloaded.
        try { runtime.Stop(); } finally { persist(); }
    }
    public void End()
    {
        Pause("Utility queue ended. Completed quests are retained.");
        run.Stage = UtilityStage.Finished; persist();
    }
    public void Tick(long now)
    {
        if (!Active) return;
        if (run.Index >= run.Entries.Count)
        { run.Stage = UtilityStage.Finished; run.Status = "Saddlebag quest queue complete."; persist(); return; }
        var entry = run.Entries[run.Index];
        switch (run.Stage)
        {
            case UtilityStage.Login:
                SetStatus($"Logging in {entry.Name}@{entry.World}…");
                if (runtime.Login(entry)) { run.Stage = UtilityStage.Check; persist(); }
                break;
            case UtilityStage.Check:
                if (!runtime.PlayerReady(entry.CharacterId)) return;
                var eligibility = runtime.Check();
                if (eligibility.Complete) { Next("Already unlocked"); return; }
                if (!eligibility.Eligible) { Next("Skipped: " + eligibility.Reason); return; }
                if (runtime.QuestRunning) throw new InvalidOperationException("Questionable is already running. Stop its current task, then resume Utilities.");
                // Save intent before dispatch. Reload/retry rechecks the server's completion flag.
                run.Stage = UtilityStage.Quest; questStarted = now; idleSince = null;
                SetStatus($"Unlocking saddlebags for {entry.Name}…"); persist();
                if (!runtime.StartQuest()) throw new InvalidOperationException("Questionable could not start My Feisty Little Chocobo. Check its quest path and dependencies, then resume.");
                break;
            case UtilityStage.Quest:
                if (!runtime.PlayerReady(entry.CharacterId)) return;
                if (runtime.QuestComplete) { runtime.Stop(); Next("Unlocked"); return; }
                if (runtime.QuestRunning)
                {
                    idleSince = null;
                    if (!runtime.ExpectedQuest) throw new InvalidOperationException("Questionable changed to another quest; Utilities paused.");
                }
                else
                {
                    idleSince ??= now;
                    if (now - idleSince > 10_000) throw new InvalidOperationException("Questionable stopped before the saddlebag quest completed. Check its window, then resume.");
                }
                if (now - questStarted > 45 * 60_000) throw new TimeoutException("The saddlebag quest has not completed after 45 minutes. Check Questionable, then resume.");
                break;
        }
    }
    private void Next(string result)
    {
        run.Entries[run.Index].Result = result; run.Index++;
        run.Stage = UtilityStage.Login; SetStatus(result); persist();
    }
    private void SetStatus(string text)
    {
        if (run.Status == text) return;
        run.Status = text; persist();
    }
}
