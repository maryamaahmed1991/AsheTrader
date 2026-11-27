using AsheTrader.Core;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Integrations;

internal sealed class CompletionHandoffService(Plugin plugin)
{
    private readonly CompletionHandoff handoff = new();
    private string reported = "";
    public string Status => handoff.Status;
    public bool Pending => handoff.Pending;
    public void Request(string task) => handoff.Request(Environment.TickCount64, task);
    public void Cancel() => handoff.Cancel("stopped by user.");
    public void Update()
    {
        if (handoff.Pending)
        {
            var otherJob = plugin.Workflow.Run is { Step: not CoordinatorStep.Finished }
                || plugin.Workflow.WaitingForPeer || plugin.FuelQueue.HasUnfinishedRun || plugin.Utilities.HasUnfinishedRun;
            var ar = plugin.AutoRetainer.GetStatus();
            var ready = !plugin.Workflow.Busy && !plugin.Trader.IsRunning && !plugin.Fuel.Active
                && Plugin.ClientState.IsLoggedIn && Plugin.ObjectTable.LocalPlayer != null
                && !NativeRetainers.BellInteractionBlocked
                && !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
                && !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51]
                && ar.Ready && ar.Busy == false && plugin.Lifestream.GetStatus().Busy == false;
            handoff.Tick(Environment.TickCount64, otherJob, ready,
                () => plugin.AutoRetainer.TryEnableMultiMode(out var error) ? null : error);
        }
        if (reported != Status) { reported = Status; plugin.Record(Status); }
    }
}
