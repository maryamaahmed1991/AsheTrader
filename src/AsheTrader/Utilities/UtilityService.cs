using AsheTrader.Core;
using AsheTrader.Core.Utilities;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using Newtonsoft.Json;

namespace AsheTrader.Utilities;

public sealed class UtilityService : IUtilityRuntime
{
    public const string QuestId = "1162";
    private readonly Plugin plugin;
    private readonly SettingsFileStore file;
    private readonly GameWorkflowExecutor executor = new();
    private UtilityQueue? queue;
    private bool loginIssued, ownsQuest, ownsSuppression;
    private bool journalHealthy = true;
    private long nextUpdate;
    private string lastStatus = "";
    public UtilityRun? Run { get; private set; }
    public string Error { get; private set; } = "";
    public bool Active => queue?.Active == true;
    public bool HasUnfinishedRun => Run is { Stage: not UtilityStage.Finished };
    public bool ExpectsLogout => Active && Run?.Stage == UtilityStage.Login;
    public bool Available => Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == "Questionable" && p.IsLoaded);

    public UtilityService(Plugin plugin)
    {
        this.plugin = plugin;
        file = new(Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "utility-queue.json"));
        executor.Diagnostic = text => plugin.Workflow.RecordDiagnostic("utilities " + text);
        try
        {
            var json = file.Load();
            if (json != null) Run = JsonConvert.DeserializeObject<UtilityRun>(json) ?? throw new InvalidDataException("Empty utility journal.");
            if (Run != null)
            {
                if (Run.Entries == null || Run.Index < 0 || Run.Index > Run.Entries.Count) throw new InvalidDataException("Invalid utility queue progress.");
                Run.Paused = true;
                if (HasUnfinishedRun) Run.Status = "Saved utility queue paused. Resume to check quest completion and continue.";
                queue = new(Run, this, Save);
            }
        }
        catch (Exception ex) { journalHealthy = false; Error = "Utility queue could not be loaded: " + ex.Message; }
    }
    private void Save()
    {
        if (!journalHealthy || !file.TrySave(JsonConvert.SerializeObject(Run), out var error))
            throw new IOException("Utility progress could not be saved. No new quest will start.");
        if (Run != null && lastStatus != Run.Status)
        { lastStatus = Run.Status; plugin.Workflow.RecordDiagnostic("utilities " + Run.Status); }
    }
    private void RequireAvailable()
    {
        if (!journalHealthy) throw new InvalidOperationException(Error);
        if (!Available) throw new InvalidOperationException("Install and enable Questionable on this PC first.");
        if (plugin.Workflow.Busy || plugin.Workflow.Run is { Step: not CoordinatorStep.Finished }
            || plugin.FuelQueue.HasUnfinishedRun || plugin.Fuel.Active || plugin.Trader.IsRunning)
            throw new InvalidOperationException("Finish or end this PC's transfer/fuel run before starting Utilities.");
        var ar = plugin.AutoRetainer.GetStatus();
        if (!ar.Ready || ar.Busy != false || ar.MultiModeEnabled != false)
            throw new InvalidOperationException("AutoRetainer must be ready and idle with multi-mode paused.");
        if (QuestRunning) throw new InvalidOperationException("Stop Questionable's current task before starting Utilities.");
        if (plugin.Lifestream.GetStatus() is { Busy: true }) throw new InvalidOperationException("Wait for Lifestream to finish before starting Utilities.");
    }
    public void Start()
    {
        RequireAvailable();
        if (HasUnfinishedRun) throw new InvalidOperationException("Resume or end the saved utility queue first.");
        var selected = plugin.Config.Characters.Where(c => plugin.Config.UtilityCharacters.Contains(c.ContentId))
            .OrderBy(c => c.World).ThenBy(c => c.Name).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select at least one character on this PC.");
        Run = new() { EnableAutoRetainerAfterCompletion = plugin.Config.UtilityEnableAutoRetainer, Entries = selected.Select(c => new UtilityEntry { CharacterId = c.ContentId, Name = c.Name, World = c.World }).ToList() };
        queue = new(Run, this, Save); Error = ""; queue.Resume();
    }
    public void Resume() { RequireAvailable(); Error = ""; loginIssued = false; queue?.Resume(); }
    public void Pause(string reason = "Utility queue paused.")
    {
        if (!HasUnfinishedRun) return;
        try { queue?.Pause(reason); } catch (Exception ex) { Error = ex.Message; }
    }
    public void End() { try { queue?.End(); } catch (Exception ex) { Error = ex.Message; } }
    public void Update()
    {
        if (!Active) return;
        try
        {
            executor.Update();
            if (Environment.TickCount64 < nextUpdate) return;
            nextUpdate = Environment.TickCount64 + 250;
            if (!Available) throw new InvalidOperationException("Questionable became unavailable.");
            if (Run!.Stage != UtilityStage.Login)
            {
                var ar = plugin.AutoRetainer.GetStatus();
                if (!ar.Ready || ar.Busy != false || ar.MultiModeEnabled != false)
                    throw new InvalidOperationException("AutoRetainer is no longer idle; Utilities paused.");
            }
            queue!.Tick(Environment.TickCount64);
            if (Run is { CompletedNormally: true, EnableAutoRetainerAfterCompletion: true })
                plugin.CompletionHandoff.Request("Utilities queue");
        }
        catch (Exception ex) { Pause(ex.Message); plugin.Record("Utilities: " + ex.Message); }
    }
    bool IUtilityRuntime.Login(UtilityEntry entry)
    {
        if (executor.TryTakeResult(out var result))
        {
            loginIssued = false;
            if (!result.Success) throw new InvalidOperationException(result.Error);
            return true;
        }
        if (!loginIssued)
        {
            ReleaseSuppression();
            if (QuestRunning) throw new InvalidOperationException("Questionable is running before character login; Utilities paused.");
            loginIssued = executor.Execute(new() { Id = Guid.NewGuid(), Kind = GameOperationKind.Login,
                CharacterId = entry.CharacterId, CharacterName = entry.Name, World = entry.World });
        }
        return false;
    }
    public bool PlayerReady(ulong characterId) => Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.ContentId == characterId
        && Plugin.ObjectTable.LocalPlayer != null && !NativeRetainers.HasActiveCutscene && !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
        && !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51];
    public bool QuestRunning => Plugin.PluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning").InvokeFunc();
    public bool ExpectedQuest => Plugin.PluginInterface.GetIpcSubscriber<string>("Questionable.GetCurrentQuestId").InvokeFunc() == QuestId;
    public bool QuestComplete => Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestComplete").InvokeFunc(QuestId);
    public QuestEligibility Check()
    {
        if (QuestComplete) return new(true, false, "Already completed");
        if (Plugin.ObjectTable.LocalPlayer!.Level < 30) return new(false, false, "Current class/job is below level 30.");
        var locked = Plugin.PluginInterface.GetIpcSubscriber<string, (bool, string)>("Questionable.IsQuestLockedReason").InvokeFunc(QuestId);
        if (locked.Item1) return new(false, false, string.IsNullOrWhiteSpace(locked.Item2) ? "Quest prerequisites are incomplete (requires My Little Chocobo)." : locked.Item2);
        var accepted = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestAccepted").InvokeFunc(QuestId);
        var ready = accepted || Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsReadyToAcceptQuest").InvokeFunc(QuestId);
        return new(false, ready, ready ? "Ready" : "Quest cannot be accepted on the current class/job or quest state.");
    }
    public bool StartQuest()
    {
        if (!Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed").InvokeFunc())
        { Plugin.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(true); ownsSuppression = true; }
        ownsQuest = true;
        return Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.StartSingleQuest").InvokeFunc(QuestId);
    }
    public void Stop()
    {
        try
        {
            executor.Stop("Utilities stopped.");
            while (executor.TryTakeResult(out _)) { }
            loginIssued = false;
            if (ownsQuest)
            {
                if (ExpectedQuest) Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.Stop").InvokeFunc("Ashe's Trader utility queue");
                ownsQuest = false;
            }
        }
        finally { ReleaseSuppression(); }
    }
    private void ReleaseSuppression()
    {
        if (!ownsSuppression) return;
        Plugin.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(false);
        ownsSuppression = false;
    }
    public void Shutdown()
    {
        try { ReleaseSuppression(); } catch { }
        // Match the native executor's unload convention: game/IPC cleanup on the framework.
        if (Plugin.Framework.IsInFrameworkUpdateThread) Pause("Utility queue paused on unload.");
        else if (!Plugin.Framework.IsFrameworkUnloading) _ = Plugin.Framework.RunOnFrameworkThread(() => Pause("Utility queue paused on unload."));
        executor.Shutdown();
    }
}
