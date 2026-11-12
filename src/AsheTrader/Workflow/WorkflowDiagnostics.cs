using System.Text;
using AsheTrader.Core.Diagnostics;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private readonly WorkflowTrace diagnosticTrace = new();

    public void RecordActivity(string message)
    {
        try { diagnosticTrace.Add("Activity", message); }
        catch { }
    }

    public void RecordTradeDiagnostic(string message) => RecordDiagnostic("trade " + message);

    private void RecordDiagnostic(string message)
    {
        // Diagnostics must never interrupt gameplay or change an operation's outcome.
        try
        {
            diagnosticTrace.Add("Workflow", message);
            Plugin.Log.Information($"[WorkflowTrace] {message}");
        }
        catch { }
    }

    /// <summary>Capture game state on the framework thread; the UI copies the returned text on its own thread.</summary>
    public string CaptureDiagnosticReport()
    {
        try { return BuildDiagnosticReport(); }
        catch (Exception ex)
        {
            diagnosticTrace.Add("CaptureError", $"Snapshot unavailable: {ex.GetType().Name}: {ex.Message}");
            return "Ashe's Trader diagnostics — snapshot capture failed; recent trace preserved.\n" + diagnosticTrace.ExportRecent(28000);
        }
    }

    private string BuildDiagnosticReport()
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread || Plugin.Framework.IsFrameworkUnloading)
            throw new InvalidOperationException("Capture diagnostics on the active framework thread.");
        executor.CaptureDiagnosticSnapshot("User requested diagnostic report");
        plugin.Trader.CaptureDiagnosticSnapshot("User requested diagnostic report");
        var header = new WorkflowTrace(40, 2000);
        header.Add("Report", $"Ashe's Trader {typeof(Plugin).Assembly.GetName().Version}; format 3; timestamps UTC; this PC only");
        header.Add("Session", $"Role={(IsHost ? "coordinator" : "worker")}; connected={Connected}; paired={HasAcceptedConnection}; remoteControl={Settings.AllowRemoteControl}; journalHealthy={journalHealthy}");
        header.Add("Game", $"loggedIn={Plugin.ClientState.IsLoggedIn}; characterId={Plugin.PlayerState.ContentId}; territory={Plugin.ClientState.TerritoryType}; language={Plugin.ClientState.ClientLanguage}");
        header.Add("Status", Status);
        if (Run is { } run)
        {
            header.Add("Coordinator", $"job={run.Job.Definition.Id}; step={run.Step}; paused={run.Paused}; itemIndex={run.ActiveItemIndex}; discoveryIndex={run.DiscoveryIndex}/{run.DiscoveryOrder.Count}; pending={run.Pending.Count}");
            header.Add("Routing", $"meetingWorld={run.EffectiveMeeting?.WorldId ?? 0}; DC={run.MeetingDataCenterName}; pinned={run.MeetingPinned}; collectionStops={run.CollectionRoute?.Count ?? 0}; reason={run.MeetingReason}; relayDelivering={run.RelayFlushCharacterId?.ToString() ?? "none"}");
            foreach (var pending in run.Pending.Take(4))
            {
                header.Add("Pending", $"command={pending.Command.Id}; kind={pending.Command.Kind}/{pending.Command.Operation.Kind}; character={pending.Command.Operation.CharacterId}; item={pending.Command.Operation.ItemId}; HQ={pending.Command.Operation.HighQuality}; quantity={pending.Command.Operation.Quantity}; response={pending.Response?.Success.ToString() ?? "waiting"}; error={pending.Response?.Error ?? ""}");
                if (StorageReconciliation.IsStorage(pending.Command))
                    header.Add("StorageRecovery", $"command={pending.Command.Id}; responsePresent={pending.Response != null}; ambiguous={pending.Response?.Ambiguous}; operatorReviewed={pending.Response?.OperatorReviewed}; observationPresent={pending.Response?.Observation != null}; confirmedMoved={pending.Response?.Observation?.MovedQuantity}; bagAtStop={pending.Response?.Observation?.BagQuantity}; movementUncertain={pending.Response?.Observation?.MovementUncertain}; accountingApplied={pending.InventoryAdjustmentApplied}; reviewAccepted={StorageReconciliation.IsReviewed(pending.Response)}");
                if (pending.Command.Kind is WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch)
                    header.Add("TradeRecovery", $"trade={run.TradeOperationId}; command={pending.Command.Id}; peer={pending.PeerId}; side={pending.Command.Kind}; responsePresent={pending.Response != null}; ambiguous={pending.Response?.Ambiguous}; operatorReviewed={pending.Response?.OperatorReviewed}; beforeQuantity={pending.Response?.BeforeQuantity}; currentQuantity={pending.Response?.CurrentQuantity}; expectedQuantity={pending.Command.Operation.Quantity}; neverStarted={pending.Response?.NeverStarted}; unchanged={pending.Response?.Unchanged}");
            }
        }
        if (activeWorker is { } worker)
            header.Add("Worker", $"command={worker.Command.Id}; job={worker.Command.JobId}; kind={worker.Command.Kind}/{worker.Command.Operation.Kind}; character={worker.Command.Operation.CharacterId}; item={worker.Command.Operation.ItemId}; HQ={worker.Command.Operation.HighQuality}; quantity={worker.Command.Operation.Quantity}; tradeStarted={worker.TradeStarted}");
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AsheTrader", "AutoRetainer", "Lifestream", "vnavmesh", "InventoryTools", "AllaganTools" };
            foreach (var installed in Plugin.PluginInterface.InstalledPlugins.Where(x => names.Contains(x.InternalName)))
                header.Add("Plugin", $"{installed.InternalName} {installed.Version}; loaded={installed.IsLoaded}");
            var ar = plugin.AutoRetainer.GetStatus();
            var ls = plugin.Lifestream.GetStatus();
            header.Add("Integration", $"AutoRetainer ready={ar.Ready}, busy={ar.Busy}, multi={ar.MultiModeEnabled}; Lifestream ready={ls.Ready}, busy={ls.Busy}");
        }
        catch (Exception ex) { header.Add("Integration", $"Could not read plugin status: {ex.GetType().Name}"); }
        var report = new StringBuilder();
        report.AppendLine("Ashe's Trader diagnostics — includes character/retainer identifiers and menu labels; excludes pairing keys, configuration dumps and chat.");
        report.AppendLine(header.ExportRecent(10000));
        report.AppendLine("--- Latest trace (28,000-character budget, retained entries oldest first) ---");
        report.AppendLine(diagnosticTrace.ExportRecent(28000));
        return report.ToString();
    }
}
